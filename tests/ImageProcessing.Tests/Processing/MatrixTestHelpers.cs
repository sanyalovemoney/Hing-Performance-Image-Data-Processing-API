using System.Buffers.Binary;

namespace ImageProcessing.Tests.Processing;

/// <summary>Shared builders/parsers for the binary matrix format used in tests.</summary>
internal static class MatrixTestHelpers
{
    public static byte[] BuildBinary(int rows, int cols, Func<int, int, float> value)
    {
        var bytes = new byte[8 + rows * cols * 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rows);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), cols);
        for (var i = 0; i < rows; i++)
        {
            for (var j = 0; j < cols; j++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes.AsSpan(8 + (i * cols + j) * 4), value(i, j));
            }
        }

        return bytes;
    }

    public static byte[] BuildBinary(int rows, int cols, float[] values) =>
        BuildBinary(rows, cols, (i, j) => values[i * cols + j]);

    public static (int Rows, int Cols, float[] Data) ParseBinary(byte[] bytes)
    {
        var rows = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var cols = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        Assert.Equal(8 + rows * cols * 4, bytes.Length);
        var data = new float[rows * cols];
        for (var k = 0; k < data.Length; k++)
        {
            data[k] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8 + k * 4));
        }

        return (rows, cols, data);
    }
}
