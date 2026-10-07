using System.Numerics;

namespace XivCloth.Core;

/// <summary>Closed volume in which the supplier captured complete collision
/// evidence. This is a geometric limit, NOT native-source authority. Every
/// collision query includes its existing outward-rounded padding; no clamping
/// or velocity-halo estimate substitutes containment.</summary>
public readonly record struct CollisionCoverage(Vector3 Minimum, Vector3 Maximum)
{
    public bool Valid => Geometry.Valid(Minimum) && Geometry.Valid(Maximum)
        && Minimum.X < Maximum.X && Minimum.Y < Maximum.Y && Minimum.Z < Maximum.Z;
    internal bool Contains(Box3 box) => Valid && Geometry.Valid(box.Minimum) && Geometry.Valid(box.Maximum)
        && Minimum.X <= box.Minimum.X && Minimum.Y <= box.Minimum.Y && Minimum.Z <= box.Minimum.Z
        && Maximum.X >= box.Maximum.X && Maximum.Y >= box.Maximum.Y && Maximum.Z >= box.Maximum.Z;
}
