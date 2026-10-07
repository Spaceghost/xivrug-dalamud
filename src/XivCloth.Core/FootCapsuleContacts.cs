using System.Numerics;

namespace XivCloth.Core;

internal static class FootCapsuleContacts
{
    internal static bool Project(Vector3[] positions,Vector3[] previous,int[] indices,float[] weights,
        ReadOnlySpan<FootCapsule> feet,float thickness,ref CollisionWork work,PlantContactDispatch? plant=null)
    {
        for(var f=0;f<feet.Length;f++)for(var i=0;i<indices.Length;i+=3)
        {
            if(!work.Charge(0))return false;
            var foot=feet[f];
            if(plant?.SelectedFoot(i/3,f)==true)continue;
            var a=indices[i];var b=indices[i+1];var c=indices[i+2];var face=new MeasuredTriangle(positions[a],positions[b],positions[c]);
            var axisBox=new Box3(Vector3.Min(foot.A,foot.B),Vector3.Max(foot.A,foot.B)).Expand(MathF.BitIncrement(foot.Radius+thickness));
            if(!Box3.Triangle(face).Intersects(axisBox))continue;
            var axis=new MeasuredTriangle(foot.A,foot.B,foot.B); // segment witness, deliberately degenerate
            var witness=TriangleContacts.Closest(face,axis);var target=foot.Radius+thickness;
            if(witness.Squared>=target*target)continue;
            var distance=Math.Sqrt(witness.Squared);var penetration=target-distance;
            if(penetration>XpbdCloth.MaximumPenetration)return false;
            D3 normal;
            if(distance>1e-10)normal=(witness.Cloth-witness.Obstacle)/distance;
            else
            {
                var old=TriangleContacts.Closest(new(previous[a],previous[b],previous[c]),axis);
                if(old.Squared<=1e-20)return false;
                normal=(old.Cloth-old.Obstacle)/Math.Sqrt(old.Squared);
            }
            var bary=witness.Barycentric;var denominator=weights[a]*bary.X*bary.X+weights[b]*bary.Y*bary.Y+weights[c]*bary.Z*bary.Z;
            if(denominator<1e-20)return false;
            var delta=penetration/denominator;
            positions[a]+=(normal*(delta*weights[a]*bary.X)).Float;
            positions[b]+=(normal*(delta*weights[b]*bary.Y)).Float;
            positions[c]+=(normal*(delta*weights[c]*bary.Z)).Float;
        }
        return true;
    }
    internal static SweepVerdict Sweep(Vector3[] before,Vector3[] after,int[] indices,ReadOnlySpan<FootCapsule> feetBefore,
        ReadOnlySpan<FootCapsule> feetAfter,float clearance,ref CollisionWork work,PlantContactDispatch? plant=null)
    {
        for(var f=0;f<feetBefore.Length;f++)for(var i=0;i<indices.Length;i+=3)
        {
            if(!work.Charge(0))return SweepVerdict.BudgetExceeded;
            var a=indices[i];var b=indices[i+1];var c=indices[i+2];
            var first=new MeasuredTriangle(before[a],before[b],before[c]);var last=new MeasuredTriangle(after[a],after[b],after[c]);
            if(plant is not null)
            {
                var guard=plant.Policy.Scene.GuardQuery(Box3.Union(Box3.Triangle(first),Box3.Triangle(last)).Expand(clearance),ref work);
                if(guard<0)return guard==-2?SweepVerdict.Unproven:SweepVerdict.BudgetExceeded;
            }
            if(plant?.SelectedFoot(i/3,f)==true)
            {
                if(!plant.FootCertificate(f,first,last))return SweepVerdict.Unproven;
                continue;
            }
            var footA=feetBefore[f];var footB=feetAfter[f];
            var bodyBox=Box3.Union(new(Vector3.Min(footA.A,footA.B),Vector3.Max(footA.A,footA.B)),
                new(Vector3.Min(footB.A,footB.B),Vector3.Max(footB.A,footB.B))).Expand(MathF.BitIncrement(Math.Max(footA.Radius,footB.Radius)+clearance));
            if(!Box3.Union(Box3.Triangle(first),Box3.Triangle(last)).Intersects(bodyBox))continue;
            var result=CapsuleSweep.Check(first,last,footA,footB,clearance,ref work);if(result!=SweepVerdict.Clear)return result;
        }
        return SweepVerdict.Clear;
    }
}
