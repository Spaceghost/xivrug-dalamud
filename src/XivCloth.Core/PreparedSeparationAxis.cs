namespace XivCloth.Core;

/// <summary>Outward interval support for a fixed arbitrary axis. Separation on
/// ANY axis proves finite-face distance; uncertain boundaries remain unproved.
/// Reuses unchanged fixed-axis support bounds for response rejection and the
/// existing continuous proof. Dynamic axes and temporal subdivision stay intact.</summary>
internal readonly struct PreparedSeparationAxis
{
    private readonly D3 direction;
    private readonly double minimum,maximum,normUpper;
    private readonly bool valid;
    internal PreparedSeparationAxis(D3 direction,D3 a,D3 b,D3 c)
    {
        this.direction=direction;
        var normSquared=Range.Point(direction.X)*direction.X+Range.Point(direction.Y)*direction.Y+Range.Point(direction.Z)*direction.Z;
        valid=double.IsFinite(direction.Squared)&&direction.Squared>=1e-28;
        normUpper=valid?Math.BitIncrement(Math.Sqrt(normSquared.Hi)):double.PositiveInfinity;
        var pa=new R3(a).Dot(direction);var pb=new R3(b).Dot(direction);var pc=new R3(c).Dot(direction);
        minimum=Math.Min(pa.Lo,Math.Min(pb.Lo,pc.Lo));maximum=Math.Max(pa.Hi,Math.Max(pb.Hi,pc.Hi));
    }
    internal bool ProvesSeparated(MeasuredTriangle face,double distance)
    {
        if(!valid||!double.IsFinite(distance)||distance<=0)return false;
        var a=new R3(new D3(face.A)).Dot(direction);var b=new R3(new D3(face.B)).Dot(direction);var c=new R3(new D3(face.C)).Dot(direction);
        var low=Math.Min(a.Lo,Math.Min(b.Lo,c.Lo));var high=Math.Max(a.Hi,Math.Max(b.Hi,c.Hi));
        var margin=Math.BitIncrement(distance*normUpper);
        return Math.BitDecrement(low-maximum)>margin||Math.BitDecrement(minimum-high)>margin;
    }
    internal bool ProvesSeparated(ReadOnlySpan<R3> hull,double distance)
    {
        if(!valid||!double.IsFinite(distance)||distance<=0)return false;
        var low=double.PositiveInfinity;var high=double.NegativeInfinity;
        foreach(var p in hull){var projection=p.Dot(direction);low=Math.Min(low,projection.Lo);high=Math.Max(high,projection.Hi);}
        var margin=Math.BitIncrement(distance*normUpper);
        return Math.BitDecrement(low-maximum)>margin||Math.BitDecrement(minimum-high)>margin;
    }
}
