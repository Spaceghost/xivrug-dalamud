namespace XivCloth.Core;

// Frozen pre-optimization clip implementation for exact expression parity.
internal static class FrozenProjectedMinimum
{
    private readonly record struct ClipVertex(D3 Position,D3 Barycentric);

    // Clip the cloth face by the three planes extruded from the finite support
    // edges. Unlike corner tests this finds a buried face's INTERIOR overlap.
    // This is explicitly one-sided support semantics, not solid-volume inference
    // for arbitrary model soups. Two-sided finite sheets do not use this rule.
    internal static bool Check(MeasuredTriangle cloth,in PreparedTriangle support,out double minimum,out D3 bary)
    {
        Span<ClipVertex> first=stackalloc ClipVertex[8];Span<ClipVertex> second=stackalloc ClipVertex[8];
        first[0]=new(new(cloth.A),new(1,0,0));first[1]=new(new(cloth.B),new(0,1,0));first[2]=new(new(cloth.C),new(0,0,1));
        var normal=support.Normal;
        var count=3;
        for(var edge=0;edge<3;edge++)
        {
            var origin=support.Vertex(edge);var inward=support.Inward(edge);var next=0;
            for(var i=0;i<count;i++)
            {
                var a=first[i];var b=first[(i+1)%count];
                var da=D3.Dot(a.Position-origin,inward)+1e-9;var db=D3.Dot(b.Position-origin,inward)+1e-9;
                if(da>=0)second[next++]=a;
                if((da>=0)!=(db>=0))
                {
                    var t=da/(da-db);second[next++]=new(D3.Lerp(a.Position,b.Position,t),D3.Lerp(a.Barycentric,b.Barycentric,t));
                }
            }
            var swap=first;first=second;second=swap;count=next;if(count==0)break;
        }
        minimum=double.PositiveInfinity;bary=default;
        foreach(var vertex in first[..count])
        {
            var distance=D3.Dot(vertex.Position-support.A,normal);
            if(distance<minimum){minimum=distance;bary=vertex.Barycentric;}
        }
        return count>0;
    }

}
