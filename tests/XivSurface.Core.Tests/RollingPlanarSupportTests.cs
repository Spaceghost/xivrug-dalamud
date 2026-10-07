using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class RollingPlanarSupportTests
{
    private static readonly SupportQueryIdentity Identity = new(1,1,1,new(0,.18f,0),.35f,1.35f);
    private sealed class Queries : IClothSupportQueries,IPlanarClothSupportQueries
    {
        public bool Proven = true, UnknownCell;
        public float Offset;
        public bool LastCellPlanar { get; private set; }
        public bool TryVertex(SupportQueryIdentity identity,Vector2 nominal,out Vector3 contact)
        { contact = new(nominal.X,nominal.X*.2f+Offset,nominal.Y); return true; }
        public bool TryCell(SupportQueryIdentity identity,Vector3 a,Vector3 b,Vector3 c,Vector3 d,out float ceiling)
        { LastCellPlanar = Proven; ceiling = (a.Y+b.Y+c.Y+d.Y)/4; return !UnknownCell; }
    }
    private static void Warm(RollingClothSupport cache,Queries queries,int start=0,int end=90)
    { for(var i=start;i<end;i++) cache.Update(Identity,Vector2.Zero,i/30d,100,queries); }

    [Fact]
    public void ProvenPlaneRetainsLocalSlopeThroughPublicationBuildAndRefinement()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache,queries);
        Assert.True(cache.TrySnapshot(Vector2.Zero,89/30d,out var snapshot));
        Assert.NotNull(snapshot!.PlanarCells); Assert.All(snapshot.PlanarCells,Assert.True);
        var corners = snapshot.Contacts;
        Assert.Equal((corners[0].Y+corners[1].Y+corners[snapshot.Width].Y+corners[snapshot.Width+1].Y)/4,snapshot.CellCeilings[0]);
        var mesh = ClothSurface.Build(snapshot.Center,snapshot.Half,0,snapshot.Contacts,snapshot.Width,0,false,
            snapshot.CellCeilings,diagonalParity:snapshot.DiagonalParity,planarCells:snapshot.PlanarCells);
        foreach(var position in mesh.Positions) Assert.Equal(position.X*.2f+ClothSurface.Clearance,position.Y,5);
        var fine = SupportVisualRefinement.Refine(mesh,3);
        Assert.NotNull(fine.GroundMinimum);
        for(var i=0;i<fine.Positions.Length;i++) Assert.Equal(fine.Positions[i].X*.2f,fine.GroundMinimum[i],5);
        snapshot.PlanarCells[0] = false;
        Assert.True(cache.TrySnapshot(Vector2.Zero,89/30d,out var cloned)); Assert.True(cloned!.PlanarCells![0]);
    }

    [Fact]
    public void VertexRevisionAndNewCellProofReplaceTheOldFlagTogether()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache,queries);
        queries.Offset = .5f; queries.Proven = false;
        Assert.False(cache.Update(Identity,Vector2.Zero,4,2,queries).CurrentCovered);
        Assert.False(cache.TrySnapshot(Vector2.Zero,4,out _));
        Warm(cache,queries,121,180);
        Assert.True(cache.TrySnapshot(Vector2.Zero,179/30d,out var snapshot));
        Assert.All(snapshot!.PlanarCells!,v=>Assert.False(v));
        var maximum = Math.Max(snapshot.Contacts[0].Y,snapshot.Contacts[1].Y);
        Assert.Equal(maximum,snapshot.CellCeilings[0]);
    }

    [Fact]
    public void UnknownRefreshAndExpiredContactsCannotPublishStalePlanarPermission()
    {
        var cache = new RollingClothSupport(); var queries = new Queries(); Warm(cache,queries);
        queries.UnknownCell = true;
        for(var i=121;i<160;i++) cache.Update(Identity,Vector2.Zero,i/30d,100,queries);
        Assert.False(cache.TrySnapshot(Vector2.Zero,159/30d,out _));
        queries.UnknownCell = false; Warm(cache,queries,160,220);
        Assert.True(cache.TrySnapshot(Vector2.Zero,219/30d,out _));
        Assert.False(cache.Update(Identity,Vector2.Zero,10,100,queries,()=>false).CurrentCovered);
        Assert.False(cache.TrySnapshot(Vector2.Zero,10,out _));
    }
}
