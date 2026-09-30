using ImageProcessing.Processing.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageProcessing.Tests.Processing;

public class ImageFilterTests
{
    private static Image<Bgra32> Solid(byte r, byte g, byte b, int size = 8)
    {
        var image = new Image<Bgra32>(size, size);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                accessor.GetRowSpan(y).Fill(new Bgra32(r, g, b, 255));
            }
        });
        return image;
    }

    private static Bgra32 GetPixel(Image<Bgra32> image, int x, int y)
    {
        Bgra32 pixel = default;
        image.ProcessPixelRows(accessor => pixel = accessor.GetRowSpan(y)[x]);
        return pixel;
    }

    private static void SetPixel(Image<Bgra32> image, int x, int y, Bgra32 value) =>
        image.ProcessPixelRows(accessor => accessor.GetRowSpan(y)[x] = value);

    [Fact]
    public void Grayscale_converts_to_bt601_luma()
    {
        using var image = Solid(200, 100, 50);

        PixelSpanFilters.GrayscaleInPlace(image);

        var p = GetPixel(image, 3, 3);
        // 0.299*200 + 0.587*100 + 0.114*50 = 124.2 -> 124
        Assert.Equal(124, p.R);
        Assert.Equal(p.R, p.G);
        Assert.Equal(p.G, p.B);
        Assert.Equal(255, p.A); // alpha preserved
    }

    [Fact]
    public void Blur_on_solid_image_is_identity()
    {
        using var image = Solid(10, 200, 30);

        PixelSpanFilters.BoxBlur3x3InPlace(image);

        var p = GetPixel(image, 0, 0);
        Assert.Equal((10, 200, 30), ((int)p.R, (int)p.G, (int)p.B));
    }

    [Fact]
    public void Blur_spreads_single_bright_pixel_over_neighbors()
    {
        using var image = Solid(0, 0, 0, size: 5);
        SetPixel(image, 2, 2, new Bgra32(90, 90, 90, 255));

        PixelSpanFilters.BoxBlur3x3InPlace(image);

        Assert.Equal(10, GetPixel(image, 2, 2).R); // 90 / 9
        Assert.Equal(10, GetPixel(image, 1, 1).R);
        Assert.Equal(10, GetPixel(image, 3, 2).R);
        Assert.Equal(0, GetPixel(image, 0, 0).R); // outside the 3x3 window
    }

    [Fact]
    public void Threshold_binarizes_luma_and_preserves_alpha()
    {
        using var image = new Image<Bgra32>(2, 1);
        SetPixel(image, 0, 0, new Bgra32(255, 255, 255, 128));
        SetPixel(image, 1, 0, new Bgra32(10, 10, 10, 128));

        PixelSpanFilters.ThresholdInPlace(image, level: 128);

        Assert.Equal(255, GetPixel(image, 0, 0).R);
        Assert.Equal(0, GetPixel(image, 1, 0).R);
        Assert.Equal(128, GetPixel(image, 0, 0).A);
    }

    [Fact]
    public void Grayscale_allocates_nothing_proportional_to_image_size()
    {
        using var image = Solid(128, 64, 32, size: 64); // 64*64*4 = 16 KB of pixels

        PixelSpanFilters.GrayscaleInPlace(image); // warm up: delegate caching, pool fill

        var before = GC.GetTotalAllocatedBytes(true);
        PixelSpanFilters.GrayscaleInPlace(image);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        Assert.True(allocated < 8 * 1024, $"grayscale allocated {allocated} bytes on a 64x64 image");
    }

    [Fact]
    public void Blur_reuses_pooled_scratch_buffers()
    {
        using var image = Solid(200, 30, 90, size: 64);

        PixelSpanFilters.BoxBlur3x3InPlace(image); // warm the ArrayPool

        var before = GC.GetTotalAllocatedBytes(true);
        PixelSpanFilters.BoxBlur3x3InPlace(image);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;

        Assert.True(allocated < 8 * 1024, $"blur allocated {allocated} bytes on a 64x64 image");
    }
}

public class ImageParamsParserTests
{
    [Fact]
    public void Null_or_empty_json_yields_defaults()
    {
        Assert.Equal(default, ImageParamsParser.Parse(null));
        Assert.Equal(default, ImageParamsParser.Parse("  "));
    }

    [Fact]
    public void Reads_filter_resize_and_threshold_fields()
    {
        var p = ImageParamsParser.Parse(
            """{"filter":"resize","width":800,"maxDimension":1024,"thresholdLevel":42}""");

        Assert.Equal(ImageFilter.Resize, p.Filter);
        Assert.Equal(800, p.Width);
        Assert.Equal(1024, p.MaxDimension);
        Assert.Equal(42, p.ThresholdLevel);
    }

    [Theory]
    [InlineData("""{"filter":"quantum"}""")]
    [InlineData("""{"width":-5}""")]
    [InlineData("""{"thresholdLevel":999}""")]
    [InlineData("[1,2,3]")]
    [InlineData("{broken")]
    public void Throws_FormatException_on_invalid_params(string json) =>
        Assert.Throws<FormatException>(() => ImageParamsParser.Parse(json));
}
