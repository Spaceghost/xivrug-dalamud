namespace XivSurface.Core;

/// <summary>Dynamic-resolution viewport inside an allocated depth texture.</summary>
public readonly record struct DepthDimensions(uint Width, uint Height, uint AllocatedWidth, uint AllocatedHeight)
{
    public bool IsValid => Width > 0 && Height > 0 && Width <= AllocatedWidth && Height <= AllocatedHeight
        && AllocatedWidth <= 16384 && AllocatedHeight <= 16384;
}
