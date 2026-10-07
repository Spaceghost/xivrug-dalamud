using XivCloth.Core;
using XivSurface.Core;

namespace XivRug.Prototype;

/// <summary>Value-only seam for the existing real framework foot capture.
/// The immutable frame must already bind its actual read's owner/model identity.
/// Caller identities are revalidation expectations, NEVER provenance that can
/// relabel an old/unbound frame. Do not retimestamp old markers.
/// The frame's existing transaction/freshness gate remains in effect.
/// No native pointers are read, animation controlled, or shoe geometry inferred.</summary>
public static class FootMarkerSnapshotAdapter
{
    public static bool TryCapture(ClothFootCaptureIdentity beforeRead,ClothFootCaptureIdentity afterRead,
        long modelGeneration,long sequence,ClothFootRenderFrame? frame,double readCompletedAt,out FootProxyPose? pose)
    {
        pose=null;
        if(!beforeRead.Bound||beforeRead!=afterRead||frame==null||frame.Zone!=beforeRead.Zone||modelGeneration<=0
            ||beforeRead.ModelGeneration!=modelGeneration||frame.CaptureIdentity!=beforeRead)return false;
        // This model has a deliberately narrower motion horizon than any
        // adaptive cloth-render allowance. Never silently inherit a larger age.
        if(!double.IsFinite(frame.SampledAt)||!double.IsFinite(readCompletedAt)||readCompletedAt<frame.SampledAt
            ||readCompletedAt>frame.SampledAt+FootProxyPose.MaximumAgeSeconds)return false;
        Span<ClothFootContact> contacts=stackalloc ClothFootContact[4];
        if(!frame.TryGetClothContacts(beforeRead.Zone,readCompletedAt,contacts,out _))return false;
        Span<FootMarkerEnvelope> markers=stackalloc FootMarkerEnvelope[4];
        for(var i=0;i<4;i++)markers[i]=new(contacts[i].Center,contacts[i].FootY,contacts[i].Radius);
        return FootProxyPose.TryCreate(new(beforeRead.Zone,beforeRead.PlayerId,unchecked((ulong)(nuint)beforeRead.Address),modelGeneration),
            sequence,frame.SampledAt,markers,out pose);
    }
}
