using System.Numerics;

namespace XivSurface.Core;

/// <summary>Final render-only geometry, independent of the spring state.
/// Lift, hem flutter, current shared contact ceilings and floor clamps are
/// applied once to canonical shared vertices. Smooth normals are then derived
/// from those ACTUAL positions, not the earlier unconstrained publication.
/// Caller-owned spans avoid per-frame position/normal allocations.</summary>
public static class ClothRenderPose
{
    public static void Prepare(ClothMesh mesh, ReadOnlySpan<float> contactCeilings,
        Vector2 halfSize, bool circle, float corner, float seconds, bool motion, bool edges, float lift,
        Span<Vector3> positions, Span<Vector3> normals)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var count=mesh.Positions.Length;
        if(count is <3 or >ClothSurface.MaximumSize*ClothSurface.MaximumSize
            || mesh.UV.Length!=count || mesh.GroundMinimum is not {} ground || ground.Length!=count
            || contactCeilings.Length!=count || positions.Length!=count || normals.Length!=count
            || mesh.Indices.Length is <3 or >(ClothSurface.MaximumSize-1)*(ClothSurface.MaximumSize-1)*6
            || mesh.Indices.Length%3!=0)
            throw new ArgumentException("Final cloth geometry requires complete bounded shared vertices.");
        if(!MathEx.Finite(halfSize) || halfSize.X<=0 || halfSize.Y<=0 || !float.IsFinite(corner)
            || corner<0 || corner>Math.Min(halfSize.X,halfSize.Y) || !float.IsFinite(seconds)
            || !float.IsFinite(lift) || lift is <0 or >8)
            throw new ArgumentException("Invalid final cloth presentation parameters.");
        // Do not mutate an immutable source mesh through an aliased output.
        if(mesh.Positions.AsSpan().Overlaps(positions) || mesh.Positions.AsSpan().Overlaps(normals)
            || mesh.Normals.AsSpan().Overlaps(positions) || mesh.Normals.AsSpan().Overlaps(normals)
            || positions.Overlaps(normals))
            throw new ArgumentException("Final cloth scratch buffers must not alias source or each other.");
        normals.Clear();
        for(var i=0;i<count;i++)
        {
            var source=mesh.Positions[i];var uv=mesh.UV[i];var floor=ground[i];var ceiling=contactCeilings[i];
            if(!MathEx.Finite(source) || Math.Max(Math.Max(Math.Abs(source.X),Math.Abs(source.Y)),Math.Abs(source.Z))>1_000_000
                || !MathEx.Finite(uv) || !float.IsFinite(floor)
                || Math.Abs(floor)>1_000_000 || floor>source.Y+.0001f || !float.IsFinite(ceiling) || ceiling<floor)
                throw new ArgumentException("Invalid final cloth vertex constraint.");
            var local=(uv-new Vector2(.5f))*2*halfSize;
            var flutter=0f;
            if(motion && edges)
            {
                var q=Vector2.Abs(local)-halfSize+new Vector2(corner);
                var outline=circle ? local.Length()-halfSize.X
                    : Vector2.Max(q,Vector2.Zero).Length()+Math.Min(Math.Max(q.X,q.Y),0)-corner;
                var fringe=1-Smooth(((-outline)-.04f)/.12f);
                flutter=ClothContactConstraint.MaximumVisualFlourish*fringe
                    *(.5f+.5f*MathF.Sin(local.X*4+local.Y*3-seconds*2));
            }
            var y=Math.Max(floor+ClothContactConstraint.Clearance,Math.Min(source.Y+lift+flutter,ceiling));
            if(!float.IsFinite(y))throw new ArgumentException("Nonfinite final cloth position.");
            positions[i]=new(source.X,y,source.Z);
        }
        for(var i=0;i<mesh.Indices.Length;i+=3)
        {
            var a=mesh.Indices[i];var b=mesh.Indices[i+1];var c=mesh.Indices[i+2];
            if((uint)a>=(uint)count || (uint)b>=(uint)count || (uint)c>=(uint)count)
                throw new ArgumentException("Invalid final cloth topology.");
            var normal=Vector3.Cross(positions[b]-positions[a],positions[c]-positions[a]);
            if(normal.Y<0)normal=-normal;
            normals[a]+=normal;normals[b]+=normal;normals[c]+=normal;
        }
        for(var i=0;i<count;i++)
            normals[i]=MathEx.Finite(normals[i]) && normals[i].LengthSquared()>1e-12f
                ? Vector3.Normalize(normals[i]):Vector3.UnitY;
    }

    private static float Smooth(float t){t=Math.Clamp(t,0,1);return t*t*(3-2*t);}
}
