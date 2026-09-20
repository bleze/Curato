namespace Curato.Core;

/// <summary>PLAN.md §10 — non-destructive edit operations. Parameters for each live in Edit.Parameters (JSON).</summary>
public enum EditOperationType
{
    Crop,
    Rotate,
    Brightness,
    Contrast,
    Saturation,
    RedEye,
    BlackAndWhite,
    Sepia,
    FillLight,
    Sharpen
}
