using System.Numerics;
namespace XivSurface.Core.Tests;
public class CarpetFootworkPlanTests
{
    private static ClothMesh Rug(bool folded=true)
    {
        const int size=17;var contacts=new Vector3[size*size];
        for(var z=0;z<size;z++)for(var x=0;x<size;x++)contacts[z*size+x]=new(-1+x*.125f,0,-1+z*.125f);
        var mesh=ClothSurface.Build(Vector2.Zero,Vector2.One,0,contacts,size,0,false);
        if(folded)for(var i=0;i<mesh.Positions.Length;i++)
        {var p=mesh.Positions[i];mesh.Positions[i].Y+=.12f*MathF.Exp(-40*((p.X-.625f)*(p.X-.625f)+p.Z*p.Z));}
        return mesh;
    }
    [Fact] public void WalksToActualFoldStompsAndReturnsToGameplayAnchor()
    {
        var mesh=Rug();var plan=CarpetFootworkPlan.Create(mesh,Vector3.Zero,0,5.6);
        Assert.NotNull(plan);Assert.InRange(plan.TargetCount,1,2);
        var walking=plan.Sample(.3);Assert.Equal(CarpetFootworkPhase.Walking,walking.Phase);Assert.True(walking.Offset.X>0);
        var stomp=Enumerable.Range(0,300).Select(i=>plan.Sample(i*.1)).First(s=>s.Phase==CarpetFootworkPhase.Smoothing);
        Assert.InRange(stomp.Offset.X,.35f,.85f);Assert.Equal(0,stomp.Offset.Y,5);
        Assert.Equal(Vector3.Zero,plan.Sample(plan.Duration).Offset);
        Assert.Equal(CarpetFootworkPhase.Complete,plan.Sample(plan.Duration).Phase);
        Assert.True(plan.StillSupported(mesh,walking.Offset));
    }
    [Fact] public void FlatRugDoesNotInventBunches()=>Assert.Null(CarpetFootworkPlan.Create(Rug(false),Vector3.Zero,0,5.6));
    [Fact] public void ExactUnsupportedSlitBlocksOutwardPath()
    {
        var mesh=Rug();var indices=new List<int>();
        for(var i=0;i<mesh.Indices.Length;i+=3)
        {var triangle=mesh.Indices.AsSpan(i,3);var minimum=triangle.ToArray().Min(j=>mesh.Positions[j].X);var maximum=triangle.ToArray().Max(j=>mesh.Positions[j].X);if(minimum<.25f&&maximum>.125f)continue;indices.AddRange(triangle.ToArray());}
        mesh=mesh with {Indices=indices.ToArray()};Assert.Null(CarpetFootworkPlan.Create(mesh,Vector3.Zero,0,5.6));
    }
    [Fact] public void RemovedOrRaisedCurrentSupportCancelsExcursion()
    {
        var mesh=Rug();var plan=CarpetFootworkPlan.Create(mesh,Vector3.Zero,0,5.6)!;
        Assert.False(plan.StillSupported(null,plan.Sample(.3).Offset));
        for(var i=0;i<mesh.GroundMinimum!.Length;i++)mesh.GroundMinimum[i]+=.3f;
        Assert.False(plan.StillSupported(mesh,plan.Sample(.3).Offset));
    }
    [Fact] public void DrawLeaseConvertsWorldDisplacementWithoutChangingGameplayYaw()
    {
        var lease=new CarpetVisualOffsetLease();var original=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2);
        Assert.True(lease.TryWrite(Vector3.Zero,original,Vector3.UnitZ*.5f,0,out var offset,out var rotation));
        Assert.True(Vector3.Distance(Vector3.Transform(offset,original),Vector3.UnitZ*.5f)<1e-6);
        var restored=lease.Release(offset,rotation,out var savedOffset,out var savedRotation);
        Assert.True(restored.Offset&&restored.Rotation);Assert.Equal(Vector3.Zero,savedOffset);Assert.Equal(original,savedRotation);Assert.False(lease.Held);
        Assert.Equal((false,false),lease.Release(offset,rotation,out _,out _));
    }
    [Fact] public void ForeignVisualOverrideIsPreservedWhileOtherOwnedComponentRestores()
    {
        var lease=new CarpetVisualOffsetLease();Assert.True(lease.TryWrite(Vector3.Zero,Quaternion.Identity,new(.5f,0,0),1,out var offset,out var rotation));
        var foreign=offset+Vector3.UnitY;Assert.False(lease.TryWrite(foreign,rotation,Vector3.Zero,0,out _,out _));
        var restore=lease.Release(foreign,rotation,out _,out _);Assert.False(restore.Offset);Assert.True(restore.Rotation);
        Assert.False(lease.TryWrite(foreign,Quaternion.Identity,Vector3.Zero,0,out _,out _));
    }
    [Fact] public void OverlappingDifferentStoreysAreNotAVisualWalkingRoute()
    {
        var mesh=Rug();var n=mesh.Positions.Length;
        var raised=mesh.Positions.Select(p=>p+new Vector3(0,.1f,0)).ToArray();
        mesh=mesh with { Positions=mesh.Positions.Concat(raised).ToArray(),
            GroundMinimum=mesh.GroundMinimum!.Concat(mesh.GroundMinimum!.Select(y=>y+.1f)).ToArray(),
            Indices=mesh.Indices.Concat(mesh.Indices.Select(i=>i+n)).ToArray() };
        Assert.Null(CarpetFootworkPlan.Create(mesh,Vector3.Zero,0,5.6));
    }
    [Fact] public void NativeYawOnlyOffsetsRejectAPitchedRenderRoot()
    {
        var lease=new CarpetVisualOffsetLease();
        Assert.False(lease.TryWrite(Vector3.Zero,Quaternion.CreateFromAxisAngle(Vector3.UnitX,.2f),Vector3.UnitX*.5f,0,out _,out _));
        Assert.False(lease.Held);
    }

    [Fact] public void ReplacedDrawRestoresCharacterOffsetWithoutTouchingNewModelRotation()
    {
        var lease=new CarpetVisualOffsetLease();Assert.True(lease.TryWrite(Vector3.Zero,Quaternion.Identity,new(.5f,0,0),1,out var offset,out _));
        var restore=lease.Release(offset,new Quaternion(float.NaN,0,0,0),out var original,out _);
        Assert.True(restore.Offset);Assert.False(restore.Rotation);Assert.Equal(Vector3.Zero,original);
    }

}
