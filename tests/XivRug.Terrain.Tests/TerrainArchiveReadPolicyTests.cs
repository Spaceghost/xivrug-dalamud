using System.Numerics;
using System.Reflection;
using XivRug.Plugin;
using XivSurface.RenderedGeometry;

namespace XivRug.Terrain.Tests;

public sealed class TerrainArchiveReadPolicyTests
{
    private static readonly TerrainArchiveHeader Standard = new(128, 2, 4096, 1);

    [Fact]
    public void StandardAllocationPreflightAcceptsExactBudget()
        => Assert.True(TerrainArchiveReadPolicy.Allows(Standard, 4096, false));

    [Fact]
    public void ModelCommonBlockSlotIsNotAStandardBlockCount()
        => Assert.True(TerrainArchiveReadPolicy.Allows(new(256, 3, 8192, uint.MaxValue), 8192, true));

    [Fact]
    public void OversizedReconstructedPayloadRefuses()
        => Assert.False(TerrainArchiveReadPolicy.Allows(Standard with { RawBytes = 4097 }, 4096, false));

    [Fact]
    public void ZeroPayloadRefuses()
        => Assert.False(TerrainArchiveReadPolicy.Allows(Standard with { RawBytes = 0 }, 4096, false));

    [Fact]
    public void UnknownAndSwappedResourceTypesRefuse()
    {
        foreach (var type in new uint[] { 0, 1, 3, 4, uint.MaxValue })
            Assert.False(TerrainArchiveReadPolicy.Allows(Standard with { Type = type }, 4096, false));
        Assert.False(TerrainArchiveReadPolicy.Allows(Standard, 4096, true));
    }

    [Fact]
    public void StandardBlockTableAllocationIsBounded()
    {
        foreach (var count in new uint[] { 0, 16385, uint.MaxValue })
            Assert.False(TerrainArchiveReadPolicy.Allows(Standard with { Size = 1024 * 1024, Blocks = count }, 4096, false));
    }

    [Fact]
    public void HeaderCannotUndersizeDeclaredBlockTable()
        => Assert.False(TerrainArchiveReadPolicy.Allows(Standard with { Size = 24 }, 4096, false));

    [Fact]
    public void HeaderSizeHasLowerAndUpperBounds()
    {
        foreach (var size in new uint[] { 0, 23, 1024 * 1024 + 1, uint.MaxValue })
            Assert.False(TerrainArchiveReadPolicy.Allows(Standard with { Size = size }, 4096, false));
    }

    [Fact]
    public void RequestCannotExpandTheGlobalAllocationCap()
    {
        foreach (var cap in new[] { -1, 0, 96 * 1024 * 1024 + 1, int.MaxValue })
            Assert.False(TerrainArchiveReadPolicy.Allows(Standard, cap, false));
    }

    [Fact]
    public void PostReadAcceptsExactMetadataAndDecodedSize()
        => Assert.True(TerrainArchiveReadPolicy.Matches(Standard, Standard, 2, 4096, 4096));

    [Fact]
    public void MetadataMutationDuringReadRefuses()
    {
        foreach (var changed in new[]
        {
            Standard with { Size = 256 }, Standard with { Blocks = 2 },
            Standard with { Type = 3 }, Standard with { RawBytes = 4095 },
        })
            Assert.False(TerrainArchiveReadPolicy.Matches(Standard, changed, 2, 4096, 4096));
    }

    [Fact]
    public void DecodedTypeAndLengthsMustMatchAdmittedResource()
    {
        Assert.False(TerrainArchiveReadPolicy.Matches(Standard, Standard, 3, 4096, 4096));
        Assert.False(TerrainArchiveReadPolicy.Matches(Standard, Standard, 2, 4095, 4096));
        Assert.False(TerrainArchiveReadPolicy.Matches(Standard, Standard, 2, 4096, 4095));
        Assert.False(TerrainArchiveReadPolicy.Matches(Standard, Standard, 2, 4096, -1));
    }

    [Fact]
    public void ReportClockMustFollowCaptureWithoutRejuvenatingSource()
    {
        // Synthetic immutable candidate only. No archive/native access or support authorization.
        // Reflection keeps the production constructor internal rather than adding a test factory.
        var batch = Assert.IsType<TerrainCandidateBatch>(Activator.CreateInstance(typeof(TerrainCandidateBatch),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [128u, 1UL, 2UL, "test", new TerrainInterest(default, Vector3.One),
                Array.Empty<TerrainTriangle>(), 0d, 0], null));
        var state = new TerrainStreamState("CandidateReady", batch, false, 2, 10.001);

        Assert.Null(state.CandidateAt(128, 10)); // Scheduling time sampled before capture is too early.
        Assert.Same(batch, state.CandidateAt(128, 10.002)); // Fresh report samples AFTER capture.
        Assert.Equal(10.001, state.ObservationCapturedSeconds); // Never retimestamp source evidence.
        Assert.Null(state.CandidateAt(128, 10.3));
        Assert.False(batch.LoadedContentVerified);
        Assert.False(batch.AuthorizesWorldSupport);
    }
}
