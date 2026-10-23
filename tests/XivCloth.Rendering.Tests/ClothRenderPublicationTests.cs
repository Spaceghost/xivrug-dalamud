using System.Numerics;
using XivRug.Rendering;
using XivSurface.Core;

namespace XivCloth.Rendering.Tests;

public sealed class ClothRenderPublicationTests
{
    private static ClothDrawPublication Packet(int sequence)
    {
        var center = new Vector2(sequence, -sequence);
        var mesh = ClothSurface.Build(center, Vector2.One, 0, new float[9], 3, 0, false);
        return new(new(center, Vector2.One, 0, mesh, 9, sequence), 128, sequence,
            center * 2, sequence * 10, sequence * .1f);
    }

    [Fact]
    public void MeshProjectionAndLiftAreOnePacketAndPreviousReaderKeepsItsOwnValues()
    {
        var publication = new ClothRenderPublication();
        var first = Packet(1); var second = Packet(2);
        Assert.True(publication.Complete(publication.BeginUpdate(), first));
        var reader = publication.Current;
        Assert.True(publication.Complete(publication.BeginUpdate(), second));
        Assert.Same(first, reader); Assert.Same(first.Cloth.Mesh, reader!.Cloth.Mesh);
        Assert.Equal(new Vector2(2, -2), reader.MapCenter); Assert.Equal(10, reader.MapScale); Assert.Equal(.1f, reader.Lift);
        Assert.Same(second, publication.Current);
        Assert.False(publication.IsCurrent(reader, 128, 1));
        Assert.True(publication.IsCurrent(second, 128, 2));
    }

    [Fact]
    public void ValidPreviousPacketRemainsAvailableWhileNextSampleIsPreparedButFailureRevokesIt()
    {
        var publication = new ClothRenderPublication(); var packet = Packet(1);
        Assert.True(publication.Complete(publication.BeginUpdate(), packet));
        var next = publication.BeginUpdate();
        Assert.Same(packet, publication.Current);
        Assert.True(publication.IsCurrent(packet, 128, 1));
        Assert.True(publication.Complete(next, null));
        Assert.Null(publication.Current); Assert.False(publication.IsCurrent(packet, 128, 1));
        Assert.False(publication.Complete(next, packet));
        Assert.Null(publication.Current);
    }

    [Fact]
    public void FaultResetAndDisposeInvalidationCannotBeUndoneByOutstandingWork()
    {
        var publication = new ClothRenderPublication(); var old = Packet(1);
        Assert.True(publication.Complete(publication.BeginUpdate(), old));
        var pending = publication.BeginUpdate();
        publication.Invalidate();
        Assert.Null(publication.Current);
        Assert.False(publication.IsCurrent(old, 128, 1));
        Assert.False(publication.Complete(pending, old));
        Assert.Null(publication.Current);
        var recovered = Packet(2);
        Assert.True(publication.Complete(publication.BeginUpdate(), recovered));
        Assert.True(publication.IsCurrent(recovered, 128, 2));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SuspendedOldProducerCannotPublishOrEraseAReplacement(bool failing)
    {
        var publication = new ClothRenderPublication(); var old = Packet(1); var replacement = Packet(2);
        using var started = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var worker = Task.Factory.StartNew(() =>
        {
            var ticket = publication.BeginUpdate(); started.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return publication.Complete(ticket, failing ? null : old);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            publication.Invalidate();
            Assert.True(publication.Complete(publication.BeginUpdate(), replacement));
        }
        finally { resume.Set(); }
        Assert.False(await worker.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(replacement, publication.Current);
    }

    [Fact]
    public void DefaultRepeatedWrongZoneAndWrongEpochCannotAuthorizeSubmission()
    {
        var publication = new ClothRenderPublication(); var packet = Packet(1);
        Assert.False(publication.Complete(0, packet));
        var ticket = publication.BeginUpdate();
        Assert.True(publication.Complete(ticket, packet));
        Assert.False(publication.Complete(ticket, Packet(2)));
        Assert.False(publication.IsCurrent(null, 128, 1));
        Assert.False(publication.IsCurrent(packet, 129, 1));
        Assert.False(publication.IsCurrent(packet, 128, 2));
        Assert.True(publication.IsCurrent(packet, 128, 1));
    }

    [Fact]
    public void RenderReadsAndIdentityChecksAllocateNothing()
    {
        var publication = new ClothRenderPublication(); var packet = Packet(1);
        Assert.True(publication.Complete(publication.BeginUpdate(), packet));
        for (var i = 0; i < 100; i++) publication.IsCurrent(publication.Current, 128, 1);
        var before = GC.GetAllocatedBytesForCurrentThread(); var accepted = 0;
        for (var i = 0; i < 10000; i++) if (publication.IsCurrent(publication.Current, 128, 1)) accepted++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10000, accepted); Assert.Equal(0, allocated);
    }
}
