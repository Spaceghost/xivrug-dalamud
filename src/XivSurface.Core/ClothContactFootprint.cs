using System.Numerics;

namespace XivSurface.Core;

/// <summary>A conservative XZ envelope of the four measured material corners.
/// Wall compression may reverse a cell or fold one corner inside its neighbors.
/// Their convex hull contains BOTH emitted triangle diagonals without changing
/// any material contact or UV. It does not establish terrain underneath it.</summary>
internal static class ClothContactFootprint
{
    public static int Hull(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Span<Vector3> destination)
    {
        if (destination.Length < 4 || !MathEx.Finite(a) || !MathEx.Finite(b)
            || !MathEx.Finite(c) || !MathEx.Finite(d)) return 0;
        Span<Vector3> sorted = stackalloc Vector3[] { a,b,c,d };
        for (var i=1;i<4;i++)
        {
            var value=sorted[i]; var j=i;
            while(j>0 && (sorted[j-1].X>value.X || sorted[j-1].X==value.X && sorted[j-1].Z>value.Z))
            { sorted[j]=sorted[j-1]; j--; }
            sorted[j]=value;
        }
        var unique=0;
        for(var i=0;i<4;i++)
            if(unique==0 || sorted[i].X!=sorted[unique-1].X || sorted[i].Z!=sorted[unique-1].Z)
                sorted[unique++]=sorted[i];
        if(unique<3)return 0;
        Span<Vector3> hull=stackalloc Vector3[8];var count=0;
        for(var i=0;i<unique;i++)
        {
            while(count>=2 && Turn(hull[count-2],hull[count-1],sorted[i])<=0)count--;
            hull[count++]=sorted[i];
        }
        var lower=count;
        for(var i=unique-2;i>=0;i--)
        {
            while(count>lower && Turn(hull[count-2],hull[count-1],sorted[i])<=0)count--;
            hull[count++]=sorted[i];
        }
        count--;
        if(count<3)return 0;
        hull[..count].CopyTo(destination);return count;
    }

    private static double Turn(Vector3 a,Vector3 b,Vector3 c)
        => ((double)b.X-a.X)*((double)c.Z-a.Z)-((double)b.Z-a.Z)*((double)c.X-a.X);
}
