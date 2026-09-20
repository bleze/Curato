namespace Curato.Core;

/// <summary>A rectangle in pixel coordinates, top-left origin — used for face boxes and crops.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
