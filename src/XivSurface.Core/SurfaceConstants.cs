using System.Numerics;
using System.Runtime.InteropServices;

namespace XivSurface.Core;

/// <summary>Exact 192-byte, row-major HLSL cbuffer layout. Never upload a partially initialized instance.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SurfaceConstants
{
    public Matrix4x4 InverseViewProjection;
    public Vector2 ViewOrigin, ViewSize;
    public uint DepthOriginX, DepthOriginY, DepthWidth, DepthHeight;
    public uint FloorMaskWidth, FloorMaskHeight, FrameValidated, Shape;
    public Vector2 CenterXZ, HalfSize, RotationCS;
    public float CornerRadius, Feather, ClearDepth, FloorTolerance, MinimumNormalY, Opacity;
    public Vector4 RugGeometry, RugMaterial;

    public bool SetRug(RugStyle style, float seconds)
    {
        if (style.TryPack(seconds, out RugGeometry, out RugMaterial)) return true;
        FrameValidated = 0;
        return false;
    }

    public static bool TryCreate(SurfaceFrame frame, Footprint footprint, Matrix4x4 inverse,
        Vector2 viewOrigin, Vector2 viewSize, DepthDimensions depth, uint maskWidth, uint maskHeight,
        float clearDepth, float tolerance, float minimumNormalY, float opacity, out SurfaceConstants constants)
    {
        constants = default;
        if (!frame.CanRender || !footprint.IsValid || !depth.IsValid
            || !MathEx.Finite(viewOrigin) || !MathEx.Finite(viewSize) || viewSize.X <= 0 || viewSize.Y <= 0
            || maskWidth == 0 || maskHeight == 0 || maskWidth > 16384 || maskHeight > 16384
            || !float.IsFinite(clearDepth) || clearDepth < 0 || clearDepth > 1
            || !float.IsFinite(tolerance) || tolerance < 0 || tolerance > 0.25f
            || !float.IsFinite(minimumNormalY) || minimumNormalY < 0 || minimumNormalY > 1
            || !float.IsFinite(opacity) || opacity < 0 || opacity > 1) return false;
        // Both singular matrices and NaN/Inf elements must fail closed.
        foreach (var value in MemoryMarshal.CreateReadOnlySpan(ref inverse.M11, 16))
            if (!float.IsFinite(value)) return false;
        if (!Matrix4x4.Invert(inverse, out _)) return false;
        constants = new()
        {
            InverseViewProjection = inverse, ViewOrigin = viewOrigin, ViewSize = viewSize,
            DepthWidth = depth.Width, DepthHeight = depth.Height,
            FloorMaskWidth = maskWidth, FloorMaskHeight = maskHeight, FrameValidated = 1,
            Shape = (uint)footprint.Shape, CenterXZ = footprint.Center, HalfSize = footprint.HalfSize,
            RotationCS = new(MathF.Cos(footprint.Rotation), MathF.Sin(footprint.Rotation)),
            CornerRadius = footprint.CornerRadius, Feather = footprint.Feather, ClearDepth = clearDepth,
            FloorTolerance = tolerance, MinimumNormalY = minimumNormalY, Opacity = opacity,
        };
        constants.SetRug(RugStyle.Default, 0);
        return true;
    }
}
