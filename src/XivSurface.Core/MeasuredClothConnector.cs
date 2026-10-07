using System.Numerics;

namespace XivSurface.Core;

/// <summary>Explicit support-query scope. Measured cloth may follow admitted
/// upward bevels; default layers and player-floor reads remain walkable-only.</summary>
public enum LayerSupportScope { WalkableOnly, MeasuredCloth }

internal static class ClothConnectorGeometry
{
    // This is a geometry role, NOT a replacement walkability threshold.
    internal static bool Eligible(LayerTriangle face) => face.Valid && !face.Walkable
        && face.Normal.Y > 0 && Math.Abs((double)Vector3.Cross(face.B-face.A,face.C-face.A).Y) >= 1e-8;

    internal static bool Height(LayerTriangle face, Vector2 at, bool connector, out float y)
    {
        if (!connector) return face.TryHeight(at,out y);
        y=0;
        if(!Eligible(face)||!MathEx.Finite(at))return false;
        var n=Vector3.Cross(face.B-face.A,face.C-face.A);
        y=face.A.Y-(n.X*(at.X-face.A.X)+n.Z*(at.Y-face.A.Z))/n.Y;
        return float.IsFinite(y);
    }
    internal static bool Contains(LayerTriangle face, Vector3 point, bool connector)
    {
        if(!connector)return face.Contains(point);
        if(!MathEx.Finite(point)||!Height(face,new(point.X,point.Z),true,out var y)
            ||Math.Abs(y-point.Y)>LocalFloorLayer.SeamTolerance)return false;
        if(point.X<Math.Min(face.A.X,Math.Min(face.B.X,face.C.X))-LocalFloorLayer.SeamTolerance
            ||point.X>Math.Max(face.A.X,Math.Max(face.B.X,face.C.X))+LocalFloorLayer.SeamTolerance
            ||point.Z<Math.Min(face.A.Z,Math.Min(face.B.Z,face.C.Z))-LocalFloorLayer.SeamTolerance
            ||point.Z>Math.Max(face.A.Z,Math.Max(face.B.Z,face.C.Z))+LocalFloorLayer.SeamTolerance)return false;
        var ab=new Vector2(face.B.X-face.A.X,face.B.Z-face.A.Z);
        var ac=new Vector2(face.C.X-face.A.X,face.C.Z-face.A.Z);
        var ap=new Vector2(point.X-face.A.X,point.Z-face.A.Z);
        var determinant=Cross(ab,ac);if(Math.Abs(determinant)<1e-8)return false;
        var u=Cross(ap,ac)/determinant;var v=Cross(ab,ap)/determinant;
        return u>=-1e-5&&v>=-1e-5&&u+v<=1.00001;
    }
    private static double Cross(Vector2 a,Vector2 b)=>(double)a.X*b.Y-(double)a.Y*b.X;
}

public sealed partial class LocalFloorLayer
{
    private readonly LayerSupportScope supportScope;
    private readonly HashSet<LayerTriangle> clothConnectors=[];
    private readonly Func<LayerTriangle,bool>? connectorPredicate;
    public LocalFloorLayer() : this(LayerSupportScope.WalkableOnly) { }
    public LocalFloorLayer(LayerSupportScope scope)
    {
        if(scope is not (LayerSupportScope.WalkableOnly or LayerSupportScope.MeasuredCloth))
            throw new ArgumentOutOfRangeException(nameof(scope));
        supportScope=scope;
        if(scope==LayerSupportScope.MeasuredCloth)connectorPredicate=IsConnector;
    }
    private bool IsConnector(LayerTriangle face)=>supportScope==LayerSupportScope.MeasuredCloth&&clothConnectors.Contains(face);
    private bool SurfaceHeight(LayerTriangle face,Vector2 at,out float y)=>ClothConnectorGeometry.Height(face,at,IsConnector(face),out y);
    private bool SurfaceContains(LayerTriangle face,Vector3 point)=>ClothConnectorGeometry.Contains(face,point,IsConnector(face));
    private float SurfaceDistance(LayerTriangle face,Vector2 point)
    {
        if(!IsConnector(face))return face.DistanceXZ(point);
        if(SurfaceHeight(face,point,out var y)&&SurfaceContains(face,new(point.X,y,point.Y)))return 0;
        return Math.Min(Distance(face.A,face.B),Math.Min(Distance(face.B,face.C),Distance(face.C,face.A)));
        float Distance(Vector3 from,Vector3 to)
        {
            var a=new Vector2(from.X,from.Z);var d=new Vector2(to.X-from.X,to.Z-from.Z);
            var t=d.LengthSquared()>1e-12f?Math.Clamp(Vector2.Dot(point-a,d)/d.LengthSquared(),0,1):0;
            return Vector2.Distance(point,a+d*t);
        }
    }

    /// <summary>Scope-local validity of a copied raw collision hit, including
    /// a possible cloth connector. Does NOT prove admission, connectivity,
    /// freshness, walkability or native clearance; Accept/AcceptMeasuredClothConnector
    /// and the complete path/cell proofs remain mandatory. Player/foot reads
    /// must continue to require LayerFloorHit.Valid.</summary>
    public bool IsMeasuredSupport(LayerFloorHit rawHit)=>rawHit.Valid
        ||supportScope==LayerSupportScope.MeasuredCloth
            &&ClothConnectorGeometry.Eligible(rawHit.Triangle)
            &&ClothConnectorGeometry.Contains(rawHit.Triangle,rawHit.Position,true);

    /// <summary>Actual plane evaluation only for a currently admitted, fresh
    /// face in this scope. A predicted height guides a query, never substitutes
    /// for its actual hit or changes the existing local probe band.</summary>
    public bool TryMeasuredHeight(LayerTriangle face,Vector2 at,out float height)
    {
        height=0;
        return connected.Contains(face)&&observed.TryGetValue(face,out var time)
            &&now>=time&&now-time<=2&&SurfaceHeight(face,at,out height);
    }

    /// <summary>Explicit copied actual hit only; never a player seed or near-feet
    /// floor. The probe must be inside this layer's unchanged local band and
    /// ray extent (a narrowed retry is allowed, never a widened ray). The hit must
    /// lie on a finite upward, positive-XZ face, across an actual shared portal
    /// on the locally reached origin-to-probe corridor. Success only records
    /// geometry: every returned elevated path segment still needs native
    /// clearance, full material-cell proof and independent foot protection.</summary>
    public bool AcceptMeasuredClothConnector(ClothFloorProbe probe,LayerFloorHit rawHit)
    {
        if(supportScope!=LayerSupportScope.MeasuredCloth||!probe.Valid
            ||curbFaces.Contains(rawHit.Triangle)
            ||!ClothConnectorGeometry.Eligible(rawHit.Triangle)
            ||!ClothConnectorGeometry.Contains(rawHit.Triangle,rawHit.Position,true)
            ||!MathEx.Finite(rawHit.Position)
            ||Vector2.DistanceSquared(probe.Position,new(rawHit.Position.X,rawHit.Position.Z))>.02f*.02f
            ||rawHit.Position.Y<probe.MinimumY||rawHit.Position.Y>probe.MaximumY
            ||!TryProbe(probe.Position,out var expectedProbe)
            ||probe.MinimumY<expectedProbe.MinimumY||probe.MaximumY>expectedProbe.MaximumY
            ||probe.StartY>expectedProbe.StartY
            ||probe.StartY-probe.Length<expectedProbe.StartY-expectedProbe.Length
            ||!TryNearest(probe.Position,out var witness,out _)
            ||!SurfaceHeight(witness,probe.Position,out var expectedY))return false;
        if(SurfaceContains(witness,new(probe.Position.X,expectedY,probe.Position.Y))
            &&Math.Abs(rawHit.Position.Y-expectedY)>SeamTolerance)return false;
        if(witness==rawHit.Triangle&&IsConnector(witness))
        {observed[witness]=now;return true;}
        if(connected.Contains(rawHit.Triangle)||connected.Count>=MaximumTriangles
            ||portalCount+2>MaximumDirectedPortals
            ||!TryPortal(witness,rawHit.Triangle,out var a,out var b)
            ||Vector2.DistanceSquared(new(a.X,a.Z),new(b.X,b.Z))<=1e-10f)return false;
        var origin=new Vector2(rootPoint.X,rootPoint.Z);
        if(!Interval(witness,origin,probe.Position,out var enter,out var exit)
            ||!PortalCrossing(origin,probe.Position,a,b,0,out var crossing)
            ||crossing<enter-1e-5f||crossing>exit+1e-5f
            ||!Interval(rawHit.Triangle,origin,probe.Position,out var nextEnter,out var nextExit)
            ||nextEnter>crossing+1e-5f||nextExit<=crossing)return false;
        if(!Add(witness,rawHit.Triangle,[]))return false;
        clothConnectors.Add(rawHit.Triangle);return true;
    }
}
