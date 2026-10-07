using System.Numerics;
using XivCloth.Core;
using XivRug.Rendering;
using XivSurface.Core;
using XivRug.Prototype;

namespace XivCloth.Owner.Tests;

public sealed class OwnerTests
{
    private static readonly FootProxyIdentity Identity = new(129, 7, 9, 1);
    private static readonly CollisionCoverage Volume = new(new(-3, -1, -3), new(3, 2, 3));
    private static readonly MeasuredTriangle[] Floor = [new(new(-3, 0, -3), new(-3, 0, 3), new(3, 0, -3)),
        new(new(3, 0, -3), new(-3, 0, 3), new(3, 0, 3))];
    private sealed class Clock { public double Now = 10; public Action? OnRead; public double Read() { OnRead?.Invoke(); return Now; } }
    private static FootProxyPose Feet(long sequence, double time, float sole = .2f, FootProxyIdentity? identity = null)
    {
        FootMarkerEnvelope[] markers = [new(new(-.1f, 0), sole, .1f), new(new(.1f, 0), sole, .1f),
            new(new(-.1f, .4f), sole, .1f), new(new(.1f, .4f), sole, .1f)];
        Assert.True(FootProxyPose.TryCreate(identity ?? Identity, sequence, time, markers, out var feet)); return feet!;
    }
    private static PhysicalClothRuntime Make(Clock clock, bool circle = true, float height = .04f, XpbdSettings? settings = null)
    {
        var pattern = circle ? ClothRestPattern.Circle(2, 9, height: height)
            : ClothRestPattern.RoundedRectangle(2, 1.6f, .35f, 9, 9, height: height);
        return new(pattern, Vector3.Zero, new(Vector2.Zero, new(1, circle ? 1 : .8f), circle, circle ? 0 : .35f), clock.Read, settings);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ActualEarlyLateIntervalsPublishWholeMaterialExactly(bool circle)
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor); var lease = world.Capture(129, Volume, clock.Now);
        var owner = Make(clock, circle); var initial = owner.Inspect();
        Assert.Equal(XpbdStatus.Ready, owner.AdmitScene(lease).Status);
        var first = Feet(1, clock.Now);
        Assert.Equal(PhysicalOwnerStatus.AwaitingHistory, owner.Observe(lease, first).Status);
        for (var seq = 2; seq <= 9; seq++)
        {
            clock.Now += seq % 2 == 0 ? .025 : .004; // Genuine alternating early/late observations.
            var actual = Feet(seq, clock.Now);
            Assert.Equal(PhysicalOwnerStatus.Ready, owner.Observe(lease, actual).Status);
            Assert.Same(actual, owner.AcceptedObservation);
            Assert.True(owner.TryGet(129, out var packet)); var final = owner.Inspect();
            Assert.Equal(initial.Indices.ToArray(), packet!.Pose.Indices.ToArray());
            Assert.Equal(initial.UV.ToArray(), packet.Pose.UV.ToArray());
            Assert.Equal(81, packet.Pose.Positions.Length);
            Assert.Equal(final.Positions.ToArray(), packet.Pose.Positions.ToArray());
            var upload = new IndexedClothVertex[packet.Pose.Indices.Length]; packet.Pose.WriteTriangleList(upload);
            for (var i = 0; i < upload.Length; i++) Assert.Equal(final.Positions[final.Indices[i]], upload[i].Position);
        }
        Assert.True(owner.Inspect().Positions[40].Y < initial.Positions[40].Y);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MissingOwnerSequenceAndSceneCannotKeepOldPublication(int kind)
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor); var lease = world.Capture(129, Volume, clock.Now);
        var owner = Make(clock); Assert.Equal(XpbdStatus.Ready, owner.AdmitScene(lease).Status);
        owner.Observe(lease, Feet(1, clock.Now)); clock.Now += .01;
        var second = Feet(2, clock.Now); Assert.Equal(PhysicalOwnerStatus.Ready, owner.Observe(lease, second).Status);
        var before = owner.Inspect(); clock.Now += .01;
        if (kind == 3) world.Invalidate();
        var next = kind == 0 ? null : Feet(kind == 1 ? 4 : 3, clock.Now, identity: kind == 2 ? Identity with { Actor = 8 } : Identity);
        Assert.NotEqual(PhysicalOwnerStatus.Ready, owner.Observe(lease, next).Status);
        Assert.False(owner.TryGet(129, out _)); Assert.Same(second, owner.AcceptedObservation);
        Assert.Equal(before.Positions.ToArray(), owner.Inspect().Positions.ToArray());
    }
    [Fact]
    public void RealPlantedZeroGapRemainsAnExplicitRefusal()
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor); var lease = world.Capture(129, Volume, clock.Now);
        var owner = Make(clock, height: .004f); Assert.Equal(XpbdStatus.Ready, owner.AdmitScene(lease).Status);
        owner.Observe(lease, Feet(1, clock.Now, 0)); clock.Now += .01;
        var before = owner.Inspect(); var result = owner.Observe(lease, Feet(2, clock.Now, 0));
        Assert.Equal(PhysicalOwnerStatus.SimulationRefused, result.Status);
        Assert.Null(owner.AcceptedObservation); Assert.False(owner.TryGet(129, out _));
        Assert.Equal(before.Positions.ToArray(), owner.Inspect().Positions.ToArray());
    }
    [Fact]
    public void RevocationDuringWorkRefusesBeforeCommitAndRetainsExactHistory()
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor); var lease = world.Capture(129, Volume, clock.Now);
        var owner = Make(clock); owner.AdmitScene(lease); owner.Observe(lease, Feet(1, clock.Now)); clock.Now += .01;
        var second = Feet(2, clock.Now); owner.Observe(lease, second); var before = owner.Inspect();
        clock.Now += .01; var count = 0;
        clock.OnRead = () => { if (++count == 2) world.Invalidate(); };
        Assert.Equal(PhysicalOwnerStatus.SimulationRefused, owner.Observe(lease, Feet(3, clock.Now)).Status);
        Assert.Equal(before.Positions.ToArray(), owner.Inspect().Positions.ToArray()); Assert.Same(second, owner.AcceptedObservation);
        Assert.False(owner.TryGet(129, out _));
    }
    [Fact]
    public void FootAndSceneExpiryAreIndependentlyCheckedAtRetrieval()
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor); var lease = world.Capture(129, Volume, clock.Now);
        var owner = Make(clock); owner.AdmitScene(lease); owner.Observe(lease, Feet(1, clock.Now)); clock.Now += .01;
        owner.Observe(lease, Feet(2, clock.Now)); Assert.True(owner.TryGet(129, out _));
        clock.Now += 1d / 30 + .0001; Assert.False(owner.TryGet(129, out _));
        clock.Now = lease.ExpiresAt + .0001; Assert.False(owner.TryGet(129, out _));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RevokedOrExpiredSceneAfterAcquisitionCannotSubmit(bool expire)
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor);
        var lease = world.Capture(129, Volume, clock.Now, .02); var owner = Make(clock);
        owner.AdmitScene(lease); owner.Observe(lease, Feet(1, clock.Now)); clock.Now += .01;
        owner.Observe(lease, Feet(2, clock.Now)); Assert.True(owner.TryGet(129, out var packet));
        Assert.True(owner.CanSubmit(packet,129));
        if (expire) clock.Now = lease.ExpiresAt + .00001; else world.Invalidate();
        Assert.False(owner.CanSubmit(packet,129)); Assert.False(owner.TryGet(129,out _));
    }
    [Fact]
    public void EqualForeignPacketCannotSubmitAndClockReentryCannotMutateOwner()
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Floor);
        var lease = world.Capture(129, Volume, clock.Now); var a=Make(clock); var b=Make(clock);
        a.AdmitScene(lease); b.AdmitScene(lease);
        var first=Feet(1,clock.Now);a.Observe(lease,first);b.Observe(lease,first);clock.Now+=.01;
        var second=Feet(2,clock.Now);a.Observe(lease,second);b.Observe(lease,second);
        Assert.True(a.TryGet(129,out var packet));Assert.False(b.CanSubmit(packet,129));
        var before=a.Inspect().Positions.ToArray();clock.Now+=.01;clock.OnRead=a.Invalidate;
        Assert.Equal(PhysicalOwnerStatus.SceneUnavailable,a.Observe(lease,Feet(3,clock.Now)).Status);
        clock.OnRead=null;
        Assert.Equal(before,a.Inspect().Positions.ToArray());Assert.Same(second,a.AcceptedObservation);
        Assert.False(a.CanSubmit(packet,129));
    }
    [Fact]
    public void NoNearestFaceTrimmingAndNoMaterialShrinking()
    {
        Assert.Throws<ArgumentException>(() => new FixtureCollisionWorld(Enumerable.Repeat(Floor[0], 129).ToArray()));
        var pattern = ClothRestPattern.Circle(2, 9);
        Assert.Throws<ArgumentException>(() => new PhysicalClothRuntime(pattern, Vector3.Zero,
            new(Vector2.Zero, new(.5f), true, 0), () => 10));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CompleteTreadsAndRisersKeepFullMaterialAcrossLeaseRefresh(bool circle)
    {
        var clock = new Clock(); var world = new FixtureCollisionWorld(Stairs());
        var lease = world.Capture(129, Volume, clock.Now);
        var pattern = circle ? ClothRestPattern.Circle(2, 9, height: .65f, pinnedVertices: [8,80])
            : ClothRestPattern.RoundedRectangle(2, 1.6f, .35f, 9, 9, height: .65f, pinnedVertices: [8,80]);
        var owner = new PhysicalClothRuntime(pattern, Vector3.Zero,
            new(Vector2.Zero, new(1, circle ? 1 : .8f), circle, circle ? 0 : .35f), clock.Read);
        var initial = owner.Inspect();
        Assert.Equal(128, lease.Scene.Triangles.Length);
        Assert.Equal(XpbdStatus.Ready, owner.AdmitScene(lease).Status);
        owner.Observe(lease, Feet(1, clock.Now, .9f));
        for (var frame = 1; frame <= 180; frame++)
        {
            clock.Now = 10 + frame / 60d;
            if (lease.ExpiresAt - clock.Now < .02)
            {
                var previous = owner.Inspect().Positions.ToArray();
                lease = world.Capture(129, Volume, clock.Now);
                Assert.Equal(XpbdStatus.Ready, owner.AdmitScene(lease).Status);
                Assert.Equal(previous, owner.Inspect().Positions.ToArray());
            }
            var step = owner.Observe(lease, Feet(frame + 1, clock.Now, .9f));
            Assert.True(step.Status == PhysicalOwnerStatus.Ready, $"frame={frame} status={step.Status} simulation={step.Step}");
            Assert.True(owner.TryGet(129, out var packet));
            Assert.Equal(81, packet!.Pose.Positions.Length);
            Assert.Equal(initial.Indices.ToArray(), packet.Pose.Indices.ToArray());
            Assert.Equal(initial.UV.ToArray(), packet.Pose.UV.ToArray());
        }
        var final = owner.Inspect().Positions.ToArray();
        Assert.Equal(initial.Positions[8], final[8]); Assert.Equal(initial.Positions[80], final[80]);
        Assert.True(final.Min(p => p.Y) < .35f && final.Max(p => p.Y) - final.Min(p => p.Y) > .25f);
        var contacts = 0;
        foreach (var p in final)
        {
            var floor = p.X < -.5f ? 0 : p.X < 0 ? .15f : p.X < .5f ? .3f : .45f;
            Assert.True(p.Y >= floor - .00001f);
            if (p.Y - floor < .025f) contacts++;
        }
        Assert.True(contacts >= 3);
    }

    [Theory]
    [InlineData(.3f, true)] [InlineData(0, false)]
    public void ActualRawCalibrationAdapterFeedsOwnerWithoutInventingSoleGap(float lift, bool accepted)
    {
        var clock = new Clock { Now = 1.06 };
        Assert.True(FootPlantCalibration.TryCreate([Support(Raw(1, 1)), Support(Raw(2, 1.02)),
            Support(Raw(3, 1.04))], 1.041, out var calibration));
        var adapter = new CalibratedFootSnapshotAdapter(); var owner = Make(clock, height: .004f);
        var lease = new FixtureCollisionWorld(Floor).Capture(3, Volume, clock.Now);
        Assert.Equal(XpbdStatus.Ready, owner.AdmitScene(lease).Status);
        FootProxyPose Capture(long sequence, double time)
        {
            var raw = Raw(sequence, time, lift);
            Assert.True(calibration!.TryEvaluate(raw, Support(raw), raw.ReadCompletedAt, out var evaluated));
            Assert.True(adapter.TryCapture(evaluated, raw, raw.ReadCompletedAt, out var pose));
            Assert.Same(raw, pose!.CaptureBinding); return pose;
        }
        var first = Capture(4, clock.Now); clock.Now += .0001;
        Assert.Equal(PhysicalOwnerStatus.AwaitingHistory, owner.Observe(lease, first).Status);
        clock.Now = 1.08; var second = Capture(5, clock.Now); clock.Now += .0001;
        var before = owner.Inspect().Positions.ToArray(); var result = owner.Observe(lease, second);
        if (accepted)
        {
            Assert.Equal(PhysicalOwnerStatus.Ready, result.Status);
            Assert.Same(second, owner.AcceptedObservation); Assert.True(owner.TryGet(3, out _));
        }
        else
        {
            Assert.Equal(PhysicalOwnerStatus.SimulationRefused, result.Status);
            Assert.Equal(before, owner.Inspect().Positions.ToArray()); Assert.False(owner.TryGet(3, out _));
        }
    }

    private static RawFootFrame Raw(long sequence, double time, float lift = 0)
    {
        RawFootIdentity identity = new(new(3, 12, 34, 2), 56, 78, Vector3.One, 1);
        RawFootMarker[] markers = [new(new(-.15f,.04f+lift,-.1f),Quaternion.Identity),
            new(new(-.15f,.06f+lift,.1f),Quaternion.Identity), new(new(.15f,.04f+lift,-.1f),Quaternion.Identity),
            new(new(.15f,.06f+lift,.1f),Quaternion.Identity)];
        Assert.True(RawFootFrame.TryCapture(identity, identity, sequence, time, time+.0001,
            Vector3.Zero, markers, out var raw)); return raw!;
    }
    private static FootSupportObservation Support(RawFootFrame raw)
    {
        FloorTriangle floor = new(new(-10,0,-10),new(-10,0,20),new(20,0,-10));
        Assert.True(FootSupportObservation.TryCreate(raw,1,[floor,floor,floor,floor],out var support)); return support!;
    }
    private static MeasuredTriangle[] Stairs()
    {
        var faces = new List<MeasuredTriangle>(128);
        void Face(Vector3 a, Vector3 b, Vector3 c) => faces.Add(new(a,b,c));
        for (var step=0; step<4; step++) for(var strip=0;strip<8;strip++)
        {
            var x=-1+step*.5f; var z=-1.5f+strip*.375f; var y=step*.15f;
            var endX=step==3?2:x+.5f;
            var a=new Vector3(x,y,z); var b=new Vector3(x,y,z+.375f);
            var c=new Vector3(endX,y,z); var d=new Vector3(endX,y,z+.375f);
            Face(a,b,c); Face(c,b,d);
            if(step<3) { var e=c+new Vector3(0,.15f,0); var f=d+new Vector3(0,.15f,0); Face(c,d,e); Face(e,d,f); }
            if(step==0) { var e=new Vector3(-2,0,z); var f=new Vector3(-2,0,z+.375f); Face(e,f,a); Face(a,f,b); }
        }
        return faces.ToArray();
    }
}
