using System.Numerics;

namespace XivSurface.Core.Tests;

public sealed class RollingClothFairnessTests
{
    private static readonly SupportQueryIdentity Origin=new(1,0,0,new(0,.18f,0),.35f,1.35f);
    private sealed class Queries : IClothSupportQueries,IIncrementalClothSupportQueries
    {
        public int Raycasts {get;private set;}
        public LayerQueryResult LastResult {get;private set;}
        public int TimeLeft=10000;
        public bool ExpensiveOuter,PendingCells,PendingUsesWholeFrame;
        public readonly HashSet<Vector2> Vertices=[];
        public readonly Dictionary<Vector2,int> Cells=[];
        public readonly Dictionary<Vector2,int> FrameCells=[];
        public void PrepareQuery(int allowance){}
        public bool TryVertex(SupportQueryIdentity identity,Vector2 nominal,out Vector3 contact)
        {
            Vertices.Add(nominal);Raycasts+=2;contact=new(nominal.X,0,nominal.Y);
            if(ExpensiveOuter&&Math.Max(Math.Abs(nominal.X),Math.Abs(nominal.Y))>.8f)
            {TimeLeft=0;LastResult=LayerQueryResult.Unknown;return false;}
            TimeLeft--;LastResult=LayerQueryResult.Success;return true;
        }
        public bool TryCell(SupportQueryIdentity identity,Vector3 a,Vector3 b,Vector3 c,Vector3 d,out float ceiling)
        {
            Raycasts++;TimeLeft=PendingUsesWholeFrame?0:TimeLeft-1;ceiling=0;
            var at=new Vector2((a.X+b.X+c.X+d.X)/4,(a.Z+b.Z+c.Z+d.Z)/4);
            Cells[at]=Cells.GetValueOrDefault(at)+1;FrameCells[at]=FrameCells.GetValueOrDefault(at)+1;
            LastResult=PendingCells?LayerQueryResult.Pending:LayerQueryResult.Success;return !PendingCells;
        }
        public bool Deadline()=>TimeLeft>0;
    }

    [Fact] public void ReadyCellsProgressBeforeExpensiveUnknownOuterVerticesConsumeDeadline()
    {
        var cache=new RollingClothSupport();var q=new Queries{ExpensiveOuter=true};
        for(var frame=0;frame<90;frame++)
        {
            q.TimeLeft=40;cache.Update(Origin,Vector2.Zero,frame/30d,100,q,q.Deadline);
        }
        Assert.NotEmpty(q.Cells);
        Assert.True(cache.TrySupportedSnapshot(Vector2.Zero,89/30d,out _));
        Assert.False(cache.TrySnapshot(Vector2.Zero,89/30d,out _));
    }

    [Fact] public void APendingCellIsNotRetriedTwiceInTheSameUpdate()
    {
        var cache=new RollingClothSupport(.4f,.4f,0);var q=new Queries{PendingCells=true};
        for(var frame=0;frame<20;frame++)
        {
            q.FrameCells.Clear();q.TimeLeft=10000;
            cache.Update(Origin,Vector2.Zero,frame/30d,100,q,q.Deadline);
            Assert.All(q.FrameCells.Values,calls=>Assert.Equal(1,calls));
        }
        Assert.False(cache.TrySnapshot(Vector2.Zero,19/30d,out _));
    }

    [Fact] public void OnePendingProofCannotMonopolizeEveryFramesCellOpportunity()
    {
        var cache=new RollingClothSupport();var q=new Queries{PendingCells=true,PendingUsesWholeFrame=true};
        for(var frame=0;frame<45;frame++)
        {
            q.TimeLeft=10000;cache.Update(Origin,Vector2.Zero,frame/30d,100,q,q.Deadline);
        }
        Assert.True(q.Cells.Count>=4,$"Only {q.Cells.Count} distinct ready cells received a full proof opportunity.");
        Assert.False(cache.TrySnapshot(Vector2.Zero,44/30d,out _));
    }
    [Fact] public void SustainedPendingCellWorkStillLeavesVertexDiscoveryOpportunity()
    {
        var cache=new RollingClothSupport();var q=new Queries{PendingCells=true,PendingUsesWholeFrame=true};
        for(var frame=0;frame<35;frame++)
        {q.TimeLeft=10000;cache.Update(Origin,Vector2.Zero,frame/30d,100,q,q.Deadline);}
        Assert.True(q.Vertices.Count>=121,$"Only {q.Vertices.Count} vertices were discovered under sustained pending proofs.");
        Assert.False(cache.TrySnapshot(Vector2.Zero,34/30d,out _));
    }

}
