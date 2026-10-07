using System.Numerics;

namespace XivCloth.Core;

/// <summary>Per-call mutable scratch. Never installed as accepted policy state
/// until the whole call and owner commit predicate succeed.</summary>
internal sealed class PlantContactDispatch
{
    internal readonly PairedPlantPolicy Policy;
    internal readonly byte[] Compressed;
    internal readonly byte[] FootFaces;
    private readonly XpbdDefinition definition;
    private readonly FootProxyPose first,last;
    private readonly double[] soleBefore=new double[2],soleAfter=new double[2];
    private readonly FootCapsule[] start=new FootCapsule[2],end=new FootCapsule[2];
    private readonly byte[] constrained;
    internal PlantContactDispatch(PairedPlantPolicy policy,FootProxyPose before,FootProxyPose after,byte[]? retained=null)
    {
        Policy=policy;definition=policy.Definition;first=before;last=after;
        Compressed=retained is null?new byte[definition.VertexCount]:(byte[])retained.Clone();
        FootFaces=new byte[definition.Faces.Length/3];constrained=new byte[definition.VertexCount];
        BeginStep(0,1);
    }
    internal void BeginStep(double from,double to)
    {
        Array.Clear(FootFaces);
        for(var f=0;f<2;f++)
        {
            soleBefore[f]=Policy.Sole(first,last,f,from);soleAfter[f]=Policy.Sole(first,last,f,to);
            var a=first.Capsules[f];var b=last.Capsules[f];
            start[f]=new(Vector3.Lerp(a.A,b.A,(float)from),Vector3.Lerp(a.B,b.B,(float)from),Math.Max(a.Radius,b.Radius));
            end[f]=new(Vector3.Lerp(a.A,b.A,(float)to),Vector3.Lerp(a.B,b.B,(float)to),Math.Max(a.Radius,b.Radius));
        }
    }
    internal bool UpdateClosure(Vector3[] before,Vector3[] after,float clearance,ref CollisionWork work)
    {
        var ids=definition.Faces;
        for(var f=0;f<2;f++)
        {
            var body=Box3.Union(new(Vector3.Min(start[f].A,start[f].B),Vector3.Max(start[f].A,start[f].B)),
                new(Vector3.Min(end[f].A,end[f].B),Vector3.Max(end[f].A,end[f].B)))
                .Expand(MathF.BitIncrement(Math.Max(start[f].Radius,end[f].Radius)+clearance));
            for(var i=0;i<ids.Length;i+=3)
            {
                if(!work.Charge(0))return false;
                var a=ids[i];var b=ids[i+1];var c=ids[i+2];
                var box=Box3.Union(Box3.Triangle(new(before[a],before[b],before[c])),Box3.Triangle(new(after[a],after[b],after[c])));
                if(Policy.Scene.GuardQuery(box.Expand(XpbdCloth.MaximumPenetration),ref work)<0)return false;
                // XZ hull boxes deliberately overselect. Interior crossings,
                // both endpoints and moving shared vertices cannot be missed.
                if(box.Minimum.X>body.Maximum.X||box.Maximum.X<body.Minimum.X
                    ||box.Minimum.Z>body.Maximum.Z||box.Maximum.Z<body.Minimum.Z)continue;
                var bit=(byte)(1<<f);FootFaces[i/3]|=bit;
                Compressed[a]|=bit;Compressed[b]|=bit;Compressed[c]|=bit;
            }
        }
        return true;
    }
    internal float PointMargin(int vertex,int obstacle,float ordinary)
    {
        for(var f=0;f<2;f++)if((Compressed[vertex]&(1<<f))!=0&&Policy.Treads[f].Has(obstacle))return 0;
        return ordinary;
    }
    internal bool SelectedTerrain(int face,int obstacle)
    {
        var i=face*3;var bits=Compressed[definition.Faces[i]]|Compressed[definition.Faces[i+1]]|Compressed[definition.Faces[i+2]];
        for(var f=0;f<2;f++)if((bits&(1<<f))!=0&&Policy.Treads[f].Has(obstacle))return true;
        return false;
    }
    internal bool SelectedFoot(int face,int foot)=>(FootFaces[face]&(1<<foot))!=0;

    internal bool ProjectVertices(Vector3[] positions,float[] weights,float ordinary,ref CollisionWork work)
    {
        Array.Clear(constrained);
        for(var i=0;i<definition.Faces.Length;i++)constrained[definition.Faces[i]]|=FootFaces[i/3];
        for(var i=0;i<positions.Length;i++)
        {
            if(Compressed[i]==0)continue;
            if(!work.Charge(0))return false;
            var p=positions[i];var lower=float.NegativeInfinity;var recovery=float.NegativeInfinity;
            if(Policy.Scene.GuardQuery(new Box3(p,p).Expand(XpbdCloth.MaximumPenetration),ref work)<0)return false;
            var upper=double.PositiveInfinity;
            for(var f=0;f<2;f++)
            {
                if((Compressed[i]&(1<<f))!=0)
                {
                    var tread=Policy.Treads[f];if(!tread.Contains(p))return false;
                    lower=Math.Max(lower,tread.Height);recovery=Math.Max(recovery,MathF.BitIncrement(tread.Height+ordinary));
                }
                if((constrained[i]&(1<<f))!=0)upper=Math.Min(upper,soleAfter[f]);
            }
            if(upper<lower)return false;
            var ceiling=upper==double.PositiveInfinity?float.PositiveInfinity:(float)upper;
            if(ceiling>upper)ceiling=MathF.BitDecrement(ceiling);
            if(ceiling<lower)return false;
            var target=Math.Clamp(p.Y,Math.Min(recovery,ceiling),ceiling);
            if(weights[i]==0)
            {if(p.Y<lower||p.Y>ceiling)return false;continue;}
            if(Math.Abs((double)target-p.Y)>XpbdCloth.MaximumPenetration)return false;
            positions[i]=p with{Y=target};
            // Preserve the original response query padding before any other
            // pair/cache can authorize geometry at the corrected location.
            if(!Policy.Scene.Covers(new Box3(positions[i],positions[i]).Expand(XpbdCloth.MaximumPenetration)))return false;
        }
        return true;
    }

    internal bool TerrainCertificate(int face,int obstacle,MeasuredTriangle before,MeasuredTriangle after)
    {
        for(var f=0;f<2;f++)
        {
            var i=face*3;var bits=Compressed[definition.Faces[i]]|Compressed[definition.Faces[i+1]]|Compressed[definition.Faces[i+2]];
            var tread=Policy.Treads[f];
            if((bits&(1<<f))==0||!tread.Has(obstacle))continue;
            if(Above(before,tread)&&Above(after,tread))return true;
        }
        return false;
    }
    private static bool Above(MeasuredTriangle triangle,PlantTread tread)
        =>triangle.A.Y>=tread.Height&&triangle.B.Y>=tread.Height&&triangle.C.Y>=tread.Height
            &&tread.Contains(triangle.A)&&tread.Contains(triangle.B)&&tread.Contains(triangle.C);
    internal bool FootCertificate(int foot,MeasuredTriangle before,MeasuredTriangle after)
        =>Above(before,Policy.Treads[foot])&&Above(after,Policy.Treads[foot])
            &&Below(before,soleBefore[foot])&&Below(after,soleAfter[foot]);
    private static bool Below(MeasuredTriangle triangle,double ceiling)
        =>triangle.A.Y<=ceiling&&triangle.B.Y<=ceiling&&triangle.C.Y<=ceiling;

    internal void ReleaseRecovered(Vector3[] before,Vector3[] after,float ordinary)
    {
        for(var i=0;i<Compressed.Length;i++)for(var f=0;f<2;f++)
        {
            var required=MathF.BitIncrement(Policy.Treads[f].Height+ordinary);
            if(before[i].Y>=required&&after[i].Y>=required)Compressed[i]&=(byte)~(1<<f);
        }
    }
}
