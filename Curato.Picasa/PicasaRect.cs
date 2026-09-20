using Curato.Core;

namespace Curato.Picasa;

/// <summary>
/// Decodes Picasa's `rect64(...)` coordinate scheme: a 16-hex-character string
/// packing four 16-bit fields (left, top, right, bottom), each a fraction of
/// the image's width/height computed as value/0xFFFF. Undocumented by Google -
/// reverse-engineered from community write-ups - so this only ever returns
/// null on anything that doesn't match the expected shape, rather than
/// guessing at a plausible-looking rectangle.
/// </summary>
public static class PicasaRect
{
    public static PixelRect? ToPixelRect(string rect64Hex, int imageWidth, int imageHeight)
    {
        if (rect64Hex.Length != 16)
        {
            return null;
        }

        try
        {
            var left = Convert.ToUInt16(rect64Hex[..4], 16) / 65535.0;
            var top = Convert.ToUInt16(rect64Hex[4..8], 16) / 65535.0;
            var right = Convert.ToUInt16(rect64Hex[8..12], 16) / 65535.0;
            var bottom = Convert.ToUInt16(rect64Hex[12..16], 16) / 65535.0;

            if (right < left || bottom < top)
            {
                return null;
            }

            var x = (int)Math.Round(left * imageWidth);
            var y = (int)Math.Round(top * imageHeight);
            var width = (int)Math.Round((right - left) * imageWidth);
            var height = (int)Math.Round((bottom - top) * imageHeight);
            return new PixelRect(x, y, width, height);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
