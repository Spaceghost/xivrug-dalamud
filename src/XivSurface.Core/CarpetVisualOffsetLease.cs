using System.Numerics;

namespace XivSurface.Core;

/// <summary>Owns only the exact render values written by this excursion. A
/// foreign change ends ownership; restoration never overwrites that change.</summary>
public sealed class CarpetVisualOffsetLease
{
    private Vector3 savedOffset, lastOffset;
    private Quaternion savedRotation, lastRotation;
    public bool Held { get; private set; }
    public bool TryWrite(Vector3 currentOffset, Quaternion currentRotation, Vector3 worldOffset,float worldYaw,
        out Vector3 offset,out Quaternion rotation)
    {
        offset=currentOffset;rotation=currentRotation;
        if(!MathEx.Finite(currentOffset)||!MathEx.Finite(worldOffset)||!Finite(currentRotation)||!float.IsFinite(worldYaw)||worldOffset.LengthSquared()>2)return false;
        if(Held&&(!Near(currentOffset,lastOffset)||!Near(currentRotation,lastRotation)))return false;
        if(!Held)
        {
            // Another visual-position plugin may already own a displacement.
            // Keep that presentation intact rather than stacking an excursion.
            if(currentOffset.LengthSquared()>.000001f||Vector3.Transform(Vector3.UnitY,currentRotation).Y<.99999f)return false;
            savedOffset=currentOffset;savedRotation=currentRotation;Held=true;
        }
        // Native SetDrawOffset rotates its vector by the original draw heading.
        // Rotation below belongs only to Scene.Object, not GameObject.Rotation.
        offset=savedOffset+Vector3.Transform(worldOffset,Quaternion.Inverse(savedRotation));
        rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,worldYaw);
        lastOffset=offset;lastRotation=rotation;return true;
    }
    public (bool Offset,bool Rotation) Release(Vector3 currentOffset,Quaternion currentRotation,out Vector3 offset,out Quaternion rotation)
    {
        offset=savedOffset;rotation=savedRotation;
        var owned=(Held&&Near(currentOffset,lastOffset),Held&&Near(currentRotation,lastRotation));
        Reset();return owned;
    }
    public void Reset(){Held=false;savedOffset=lastOffset=default;savedRotation=lastRotation=Quaternion.Identity;}
    private static bool Near(Vector3 a,Vector3 b)=>MathEx.Finite(a)&&Vector3.DistanceSquared(a,b)<1e-10f;
    private static bool Near(Quaternion a,Quaternion b)=>Finite(a)&&Math.Abs(Quaternion.Dot(a,b))>1-.000001f;
    private static bool Finite(Quaternion q)=>float.IsFinite(q.LengthSquared())&&Math.Abs(q.LengthSquared()-1)<.001f;
}
