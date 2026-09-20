using SkiaSharp;

namespace Curato.Media;

/// <summary>Reads just an image's pixel dimensions from its header, without decoding pixel data.</summary>
public static class ImageDimensionReader
{
    public static (int Width, int Height)? TryReadDimensions(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var codec = SKCodec.Create(stream);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }
}
