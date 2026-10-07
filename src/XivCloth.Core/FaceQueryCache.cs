namespace XivCloth.Core;

/// <summary>Bounded geometry-only BVH cache. A cached result is a superset for
/// one outward-rounded envelope, tied to the exact immutable scene object.
/// Every current query is contained and re-filtered, never accepted by a stale
/// collision verdict. Reuse is limited to a single Advance/Rebind call, so a
/// refused call cannot alter the next retry's counted work.</summary>
internal sealed class FaceQueryCache
{
    private const float Padding=.025f;
    private readonly Box3[] envelopes;
    private readonly int[] counts,ids;
    private MeasuredTriangleScene? owner;
    internal FaceQueryCache(int faces)
    {
        if(faces is <1 or >XpbdDefinition.MaximumIndices/3)throw new ArgumentOutOfRangeException(nameof(faces));
        envelopes=new Box3[faces];counts=new int[faces];
        ids=new int[checked(faces*MeasuredTriangleScene.MaximumTriangles)];
        Array.Fill(counts,-1);
    }
    internal void BeginCall()=>owner=null;
    internal int Query(int face,MeasuredTriangleScene scene,Box3 box,Span<int> destination,ref CollisionWork work)
    {
        if((uint)face>=(uint)counts.Length)throw new ArgumentOutOfRangeException(nameof(face));
        var guard=scene.GuardQuery(box,ref work);
        if(guard<0)return guard;
        if(scene.Triangles.IsEmpty)return 0;
        if(!work.Charge(2))return -1;
        if(!ReferenceEquals(owner,scene))
        {
            // Identity reset is bounded and explicitly charged, including an
            // attempted switch that cannot afford to initialize the cache.
            for(var i=0;i<counts.Length;i++)
                if(!work.Charge(2))return -1;
            Array.Fill(counts,-1);owner=scene;
        }
        var stored=ids.AsSpan(face*MeasuredTriangleScene.MaximumTriangles,MeasuredTriangleScene.MaximumTriangles);
        if(counts[face]<0||!Contains(envelopes[face],box))
        {
            counts[face]=-1; // Exhausted/refused refills are never reusable.
            var envelope=box.Expand(Padding);
            var count=scene.Query(envelope,stored,ref work);
            if(count<0)return count;
            envelopes[face]=envelope;counts[face]=count;
        }
        var found=0;
        foreach(var id in stored[..counts[face]])
        {
            if(!work.Charge(2))return -1;
            if(!scene.Bounds(id).Intersects(box))continue;
            if(found>=destination.Length)return -1;
            destination[found++]=id;
        }
        // The stored BVH result is already sorted, so its filtered subsequence
        // has precisely the same order as an uncached query of this box.
        return found;
    }
    private static bool Contains(Box3 envelope,Box3 box)=>
        envelope.Minimum.X<=box.Minimum.X&&envelope.Minimum.Y<=box.Minimum.Y&&envelope.Minimum.Z<=box.Minimum.Z
        &&envelope.Maximum.X>=box.Maximum.X&&envelope.Maximum.Y>=box.Maximum.Y&&envelope.Maximum.Z>=box.Maximum.Z;
}
