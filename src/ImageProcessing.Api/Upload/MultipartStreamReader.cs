using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ImageProcessing.Api.Upload;

/// <summary>
/// Streams a multipart/form-data request WITHOUT framework form buffering:
/// the file part is copied straight to the destination through a pooled
/// buffer (constant memory), small text fields are collected into a map.
/// </summary>
public static class MultipartStreamReader
{
    private const int CopyBufferSize = 81920;
    private const int MaxFieldLength = 4096;

    public sealed record UploadResult(long BytesWritten, string? FileName, IReadOnlyDictionary<string, string> Fields);

    /// <summary>
    /// Returns null when the request is not multipart or contains no file part.
    /// Throws <see cref="PayloadTooLargeException"/> as soon as the cap is exceeded —
    /// oversized uploads are cut off mid-stream, never buffered.
    /// </summary>
    public static async Task<UploadResult?> ReadAsync(
        HttpRequest request, Stream destination, long maxBytes, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return null;
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.Boundary.HasValue)
        {
            return null;
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary.Value).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return null;
        }

        var reader = new MultipartReader(boundary, request.Body, CopyBufferSize);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        string? fileName = null;
        var sawFile = false;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                disposition is null)
            {
                continue;
            }

            if (disposition.FileName.HasValue || disposition.FileNameStar.HasValue)
            {
                sawFile = true;
                fileName ??= HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                total += await CopyCappedAsync(section.Body, destination, maxBytes - total, ct).ConfigureAwait(false);
            }
            else if (disposition.Name.HasValue)
            {
                using var fieldBuffer = new MemoryStream();
                await CopyCappedAsync(section.Body, fieldBuffer, MaxFieldLength, ct).ConfigureAwait(false);
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value!;
                fields[name] = Encoding.UTF8.GetString(fieldBuffer.GetBuffer(), 0, (int)fieldBuffer.Length);
            }
        }

        return sawFile ? new UploadResult(total, fileName, fields) : null;
    }

    /// <summary>Copies at most <paramref name="cap"/> bytes; throws past the cap.</summary>
    private static async Task<long> CopyCappedAsync(Stream from, Stream to, long cap, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                // Read one byte past the cap to detect overflow deterministically.
                var want = (int)Math.Min(buffer.Length, cap - copied + 1);
                if (want <= 0)
                {
                    throw new PayloadTooLargeException(cap);
                }

                var read = await from.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                copied += read;
                if (copied > cap)
                {
                    throw new PayloadTooLargeException(cap);
                }

                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }
}
