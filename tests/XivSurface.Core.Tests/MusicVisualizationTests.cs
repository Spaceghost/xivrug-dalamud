using System.Numerics;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class MusicVisualizationTests
{
    public static TheoryData<MusicVisualizationMode, MusicRadialDirection> Modes => new()
    {
        {MusicVisualizationMode.SpectrumCrown,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.SpectrumCrown,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.RadialRibbons,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.RadialRibbons,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.SpiralFountain,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.SpiralFountain,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.OrbitHalo,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.OrbitHalo,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.HelixCanopy,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.HelixCanopy,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.PrismBloom,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.PrismBloom,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.WaveDome,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.WaveDome,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.StarFountain,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.StarFountain,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.AuroraVeil,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.AuroraVeil,MusicRadialDirection.RimIn},
        {MusicVisualizationMode.ResonanceArches,MusicRadialDirection.CenterOut}, {MusicVisualizationMode.ResonanceArches,MusicRadialDirection.RimIn},
    };
    private static float[] Bands(float value=1)=>Enumerable.Repeat(value,32).ToArray();

    [Theory, MemberData(nameof(Modes))]
    public void ModesAreTrueBoundedTrianglesInFixedLocalMaterialSpace(MusicVisualizationMode mode,MusicRadialDirection direction)
    {
        var vertices=new MusicVertex[MusicVisualization.MaximumVertices];var bands=Bands(.3f);
        var count=MusicVisualization.Build(vertices,bands,mode,direction,2,.7f,1.3,1);
        Assert.Equal(MusicVisualization.RequiredCapacity(mode),count);Assert.Equal(0,count%3);
        Assert.Contains(vertices.Take(count),v=>v.Position.Y>.01f);
        Assert.Contains(vertices.Take(count),v=>Math.Abs(v.Position.X)>.1f);
        Assert.Contains(vertices.Take(count),v=>Math.Abs(v.Position.Z)>.1f);
        foreach(var v in vertices.Take(count))
        {
            Assert.InRange(v.Position.Y,0,.7f);Assert.True(new Vector2(v.Position.X,v.Position.Z).LengthSquared()<=4);
            Assert.InRange(v.Color.X,0,1);Assert.InRange(v.Color.Y,0,1);Assert.InRange(v.Color.Z,0,1);Assert.InRange(v.Color.W,0,1);
        }
        Assert.All(bands,v=>Assert.Equal(.3f,v));
    }

    [Theory, MemberData(nameof(Modes))]
    public void GainChangesHeightAndOpacityNotTheMaterialFootprint(MusicVisualizationMode mode,MusicRadialDirection direction)
    {
        var quiet=new MusicVertex[2304];var loud=new MusicVertex[2304];
        var count=MusicVisualization.Build(quiet,Bands(.03f),mode,direction,2,1,.7,1);
        Assert.Equal(count,MusicVisualization.Build(loud,Bands(.8f),mode,direction,2,1,.7,1));
        for(var i=0;i<count;i++)
        {Assert.Equal(quiet[i].Position.X,loud[i].Position.X);Assert.Equal(quiet[i].Position.Z,loud[i].Position.Z);Assert.True(loud[i].Position.Y>=quiet[i].Position.Y);}
    }

    [Theory, MemberData(nameof(Modes))]
    public void ZeroAndMalformedFramesDoNotWritePartialGeometry(MusicVisualizationMode mode,MusicRadialDirection direction)
    {
        var sentinel=new MusicVertex(new(99,98,97),new(1,2,3,4));var output=Enumerable.Repeat(sentinel,2304).ToArray();
        Assert.Equal(0,MusicVisualization.Build(output,Bands(0),mode,direction,2,1,1,1));
        var invalid=Bands();invalid[31]=float.NaN;
        Assert.Equal(0,MusicVisualization.Build(output,invalid,mode,direction,2,1,1,1));
        Assert.Equal(0,MusicVisualization.Build(output.AsSpan(0,MusicVisualization.RequiredCapacity(mode)-1),Bands(),mode,direction,2,1,1,1));
        Assert.Equal(0,MusicVisualization.Build(output,Bands(),mode,direction,float.PositiveInfinity,1,1,1));
        Assert.Equal(0,MusicVisualization.Build(output,Bands(),mode,direction,2,1,double.NaN,1));
        Assert.Equal(0,MusicVisualization.Build(output,Bands(),mode,direction,2,1,1,0));
        Assert.All(output,v=>Assert.Equal(sentinel,v));
    }

    [Theory, MemberData(nameof(Modes))]
    public void WrappedPhaseDoesNotPopVisibleGeometry(MusicVisualizationMode mode,MusicRadialDirection direction)
    {
        var first=new MusicVertex[2304];var second=new MusicVertex[2304];
        var count=MusicVisualization.Build(first,Bands(.4f),mode,direction,2,1,-.00001,1);
        Assert.Equal(count,MusicVisualization.Build(second,Bands(.4f),mode,direction,2,1,.00001,1));
        for(var i=0;i<count;i++)
            Assert.InRange(Vector3.Distance(first[i].Position*first[i].Color.W,second[i].Position*second[i].Color.W),0,.0002f);
    }

    [Fact]
    public void EachModeAndRadialDirectionHasDistinctGeometry()
    {
        var fingerprints=new HashSet<string>();
        foreach(var mode in Enum.GetValues<MusicVisualizationMode>())
        foreach(var direction in Enum.GetValues<MusicRadialDirection>())
        {
            var output=new MusicVertex[2304];var count=MusicVisualization.Build(output,Bands(.5f),mode,direction,2,1,.7,1);
            var hash=new HashCode();for(var i=0;i<count;i++)hash.Add(output[i]);
            Assert.True(fingerprints.Add(hash.ToHashCode().ToString()));
        }
    }

    [Fact]
    public void SingleActualBandDoesNotFabricateOtherCrownOrRibbonBands()
    {
        var bands=new float[32];bands[7]=.1f;var output=new MusicVertex[2304];
        Assert.Equal(18,MusicVisualization.Build(output,bands,MusicVisualizationMode.SpectrumCrown,MusicRadialDirection.CenterOut,2,1,.7,1));
        Assert.Equal(72,MusicVisualization.Build(output,bands,MusicVisualizationMode.RadialRibbons,MusicRadialDirection.CenterOut,2,1,.7,1));
    }

    [Fact]
    public void ExistingSerializedModeNumbersStayFixedAndNewModesAreAppended()
    {
        Assert.Equal(0,(int)MusicVisualizationMode.SpectrumCrown);
        Assert.Equal(1,(int)MusicVisualizationMode.RadialRibbons);
        Assert.Equal(2,(int)MusicVisualizationMode.SpiralFountain);
        Assert.Equal(3,(int)MusicVisualizationMode.OrbitHalo);
        Assert.Equal(4,(int)MusicVisualizationMode.HelixCanopy);
        Assert.Equal(5,(int)MusicVisualizationMode.PrismBloom);
        Assert.Equal(6,(int)MusicVisualizationMode.WaveDome);
        Assert.Equal(7,(int)MusicVisualizationMode.StarFountain);
        Assert.Equal(8,(int)MusicVisualizationMode.AuroraVeil);
        Assert.Equal(9,(int)MusicVisualizationMode.ResonanceArches);
        Assert.All(Enum.GetValues<MusicVisualizationMode>(),m=>
            Assert.InRange(MusicVisualization.RequiredCapacity(m),3,MusicVisualization.MaximumVertices));
    }

    [Theory]
    [InlineData(MusicVisualizationMode.HelixCanopy,48)]
    [InlineData(MusicVisualizationMode.PrismBloom,12)]
    [InlineData(MusicVisualizationMode.WaveDome,96)]
    [InlineData(MusicVisualizationMode.StarFountain,72)]
    [InlineData(MusicVisualizationMode.AuroraVeil,96)]
    [InlineData(MusicVisualizationMode.ResonanceArches,96)]
    public void NewModesRespondToEachActualBandWithoutInventingAnAggregateSpectrum(MusicVisualizationMode mode,int expected)
    {
        var output=new MusicVertex[2304];var bands=new float[32];
        for(var band=0;band<32;band++)
        {
            Array.Clear(bands);bands[band]=.2f;
            var count=MusicVisualization.Build(output,bands,mode,MusicRadialDirection.CenterOut,2,1,.77,1);
            Assert.Equal(expected,count);
            Assert.Contains(output.Take(count),v=>v.Position.Y>.001f && v.Color.W>0);
        }
    }

    [Theory, MemberData(nameof(Modes))]
    public void AllPhasesRespectConfiguredBoundsAndDestinationTail(MusicVisualizationMode mode,MusicRadialDirection direction)
    {
        var sentinel=new MusicVertex(new(99,98,97),new(1,2,3,4));
        var output=Enumerable.Repeat(sentinel,MusicVisualization.MaximumVertices+1).ToArray();
        var bands=Enumerable.Range(0,32).Select(i=>i%3==0?1f:(i%3==1?.00001f:0)).ToArray();
        for(var phase=0;phase<=48;phase++)
        {
            var count=MusicVisualization.Build(output,bands,mode,direction,128,32,phase*Math.Tau/48,1);
            Assert.InRange(count,3,MusicVisualization.RequiredCapacity(mode));
            foreach(var vertex in output.Take(count))
            {
                Assert.True(float.IsFinite(vertex.Position.X) && float.IsFinite(vertex.Position.Y) && float.IsFinite(vertex.Position.Z));
                Assert.InRange(vertex.Position.Y,0,32);
                Assert.True(new Vector2(vertex.Position.X,vertex.Position.Z).LengthSquared()<=128*128);
            }
            Assert.Equal(sentinel,output[MusicVisualization.MaximumVertices]);
        }
    }

    [Theory]
    [InlineData(MusicRadialDirection.CenterOut)]
    [InlineData(MusicRadialDirection.RimIn)]
    public void EveryStaggeredStarResetHasZeroVisibleDiscontinuity(MusicRadialDirection direction)
    {
        var before=new MusicVertex[2304];var after=new MusicVertex[2304];var bands=Bands(.5f);
        for(var stream=0;stream<3;stream++)
        for(var band=0;band<32;band++)
        {
            var seam=-(stream/3.0+band/32.0)*Math.Tau;
            var count=MusicVisualization.Build(before,bands,MusicVisualizationMode.StarFountain,direction,2,1,seam-.00001,1);
            Assert.Equal(count,MusicVisualization.Build(after,bands,MusicVisualizationMode.StarFountain,direction,2,1,seam+.00001,1));
            for(var i=0;i<count;i++)
                Assert.InRange(Vector3.Distance(before[i].Position*before[i].Color.W,after[i].Position*after[i].Color.W),0,.0002f);
        }
    }

    [Fact]
    public void StarDirectionReallyReversesRadialTravelAndStarsHaveVolume()
    {
        var first=new MusicVertex[2304];var second=new MusicVertex[2304];var bands=new float[32];bands[0]=1;
        foreach(var direction in Enum.GetValues<MusicRadialDirection>())
        {
            MusicVisualization.Build(first,bands,MusicVisualizationMode.StarFountain,direction,2,1,.5,1);
            MusicVisualization.Build(second,bands,MusicVisualizationMode.StarFountain,direction,2,1,.6,1);
            // First octahedron's top vertex is exactly above its radial center.
            var r1=new Vector2(first[0].Position.X,first[0].Position.Z).Length();
            var r2=new Vector2(second[0].Position.X,second[0].Position.Z).Length();
            Assert.True(direction==MusicRadialDirection.CenterOut?r2>r1:r2<r1);
            var volume=Vector3.Dot(Vector3.Cross(first[1].Position-first[0].Position,
                first[2].Position-first[0].Position),first[12].Position-first[0].Position);
            Assert.True(Math.Abs(volume)>1e-9f);
        }
    }

    [Fact]
    public void DomeCrestTravelsInTheRequestedRadialDirection()
    {
        var forward=new MusicVertex[2304];var backward=new MusicVertex[2304];var bands=Bands();
        MusicVisualization.Build(forward,bands,MusicVisualizationMode.WaveDome,MusicRadialDirection.CenterOut,2,1,Math.Tau*.25,1);
        MusicVisualization.Build(backward,bands,MusicVisualizationMode.WaveDome,MusicRadialDirection.RimIn,2,1,Math.Tau*.25,1);
        // Inner corner of radial rings two and six; remove dome envelope to
        // compare the actual opposite-direction traveling crest, not height bias.
        var outwardNear=forward[2*32*6].Position.Y/MathF.Sqrt(1-.25f*.25f);
        var outwardFar=forward[6*32*6].Position.Y/MathF.Sqrt(1-.75f*.75f);
        var inwardNear=backward[2*32*6].Position.Y/MathF.Sqrt(1-.25f*.25f);
        var inwardFar=backward[6*32*6].Position.Y/MathF.Sqrt(1-.75f*.75f);
        Assert.True(outwardNear>outwardFar+.5f);
        Assert.True(inwardFar>inwardNear+.5f);
    }

    [Fact]
    public void VeilHasTwoOrthogonalPleatedSheetsWithGroundedLowerEdges()
    {
        var output=new MusicVertex[2304];
        Assert.Equal(1536,MusicVisualization.Build(output,Bands(),MusicVisualizationMode.AuroraVeil,
            MusicRadialDirection.CenterOut,2,1,.7,1));
        for(var band=0;band<32;band++)
        {
            var at=band*24;
            Assert.Equal(0,output[at].Position.Y);
            Assert.Equal(0,output[at+1].Position.Y);
            var top=output[at+23].Position;
            Assert.True(top.Y>0);
            // The second curtain is an actual quarter-turn in material XZ,
            // not a duplicate screen-facing strip.
            for(var vertex=0;vertex<24;vertex++)
            {
                var a=output[at+vertex].Position;var b=output[768+at+vertex].Position;
                Assert.Equal(new Vector3(-a.Z,a.Y,a.X),b);
            }
        }
        var middle=16*24;
        var lower=output[middle].Position;var upper=output[middle+23].Position;
        var pleated=output[middle+11].Position;
        Assert.InRange(pleated.Y,lower.Y,upper.Y);
        Assert.True(Math.Abs(pleated.Z-(lower.Z+upper.Z)*.5f)>.001f);
    }

    [Fact]
    public void ArchesAreSixteenOpenRaisedBridgeRibsNotAClosedDome()
    {
        var output=new MusicVertex[2304];
        Assert.Equal(1536,MusicVisualization.Build(output,Bands(),MusicVisualizationMode.ResonanceArches,
            MusicRadialDirection.CenterOut,2,1,.7,1));
        for(var arch=0;arch<16;arch++)
        {
            var at=arch*96;
            Assert.Equal(0,output[at].Position.Y);
            Assert.InRange(output[at+15*6+1].Position.Y,0,.000001f);
            Assert.True(output[at+8*6].Position.Y>.01f);
            var near=(output[at].Position+output[at+5].Position)*.5f;
            var far=(output[at+15*6+1].Position+output[at+15*6+2].Position)*.5f;
            Assert.InRange((new Vector2(near.X,near.Z)+new Vector2(far.X,far.Z)).Length(),0,.000001f);
            Assert.True(Vector3.Distance(output[at].Position,output[at+5].Position)>.05f);
        }
    }

    [Theory]
    [InlineData(MusicVisualizationMode.AuroraVeil)]
    [InlineData(MusicVisualizationMode.ResonanceArches)]
    public void AddedModesHaveRadialCrestsNotOnlyOppositeRotation(MusicVisualizationMode mode)
    {
        var outward=new MusicVertex[2304];var inward=new MusicVertex[2304];
        MusicVisualization.Build(outward,Bands(),mode,MusicRadialDirection.CenterOut,2,1,Math.Tau*.25,1);
        MusicVisualization.Build(inward,Bands(),mode,MusicRadialDirection.RimIn,2,1,Math.Tau*.25,1);
        var nearIndex=6*6;var farIndex=2*6; // arch q=.25 / .75
        var nearEnvelope=MathF.Sin(MathF.PI*.375f);var farEnvelope=MathF.Sin(MathF.PI*.125f);
        if(mode==MusicVisualizationMode.AuroraVeil)
        {
            nearIndex=NearestTop(.25f);farIndex=NearestTop(.75f);
            nearEnvelope=farEnvelope=1;
        }
        Assert.True(outward[nearIndex].Position.Y/nearEnvelope>outward[farIndex].Position.Y/farEnvelope+.5f);
        Assert.True(inward[farIndex].Position.Y/farEnvelope>inward[nearIndex].Position.Y/nearEnvelope+.5f);
        for(var i=0;i<1536;i++)
        {
            Assert.Equal(outward[i].Position.X,inward[i].Position.X);
            Assert.Equal(outward[i].Position.Z,inward[i].Position.Z);
        }
        int NearestTop(float radial)=>Enumerable.Range(0,32).Select(i=>i*24+23)
            .MinBy(i=>Math.Abs(new Vector2(outward[i].Position.X,outward[i].Position.Z).Length()/2-radial));
    }

    [Theory]
    [InlineData(MusicVisualizationMode.AuroraVeil)]
    [InlineData(MusicVisualizationMode.ResonanceArches)]
    public void AddedModeCrestsAdvanceInTheRequestedRadialDirection(MusicVisualizationMode mode)
    {
        var output=new MusicVertex[2304];
        foreach(var direction in Enum.GetValues<MusicRadialDirection>())
        {
            var before=Peak(.25);var after=Peak(.375);
            Assert.True(direction==MusicRadialDirection.CenterOut?after>before+.05f:after<before-.05f);
            float Peak(double turn)
            {
                MusicVisualization.Build(output,Bands(),mode,direction,2,1,Math.Tau*turn,1);
                var best=float.NegativeInfinity;var radius=0f;
                var count=mode==MusicVisualizationMode.AuroraVeil?32:15;
                for(var sample=0;sample<count;sample++)
                {
                    var index=mode==MusicVisualizationMode.AuroraVeil?sample*24+23:(sample+1)*6;
                    var u=(sample+1)/16f;
                    var value=output[index].Position.Y/(mode==MusicVisualizationMode.AuroraVeil?1:MathF.Sin(MathF.PI*u));
                    if(value<=best)continue;
                    best=value;radius=mode==MusicVisualizationMode.AuroraVeil
                        ?new Vector2(output[index].Position.X,output[index].Position.Z).Length()/2:Math.Abs(2*u-1);
                }
                return radius;
            }
        }
    }

    [Theory]
    [InlineData(MusicVisualizationMode.AuroraVeil)]
    [InlineData(MusicVisualizationMode.ResonanceArches)]
    public void AddedModesHaveNonzeroAreaAtSmallAndLargeSupportedScales(MusicVisualizationMode mode)
    {
        var output=new MusicVertex[2304];
        foreach(var radius in new[]{.01f,2,128})
        {
            var count=MusicVisualization.Build(output,Bands(.5f),mode,MusicRadialDirection.RimIn,radius,.7f,.7,1);
            var area=0.0;
            for(var i=0;i<count;i+=3)
                area+=Vector3.Cross(output[i+1].Position-output[i].Position,output[i+2].Position-output[i].Position).Length();
            Assert.True(double.IsFinite(area)&&area>radius*.01);
        }
    }

    [Fact]
    public void BuildHasZeroWarmManagedAllocation()
    {
        var bands=Bands(.3f);var output=new MusicVertex[2304];
        for(var i=0;i<10;i++)foreach(var mode in Enum.GetValues<MusicVisualizationMode>())MusicVisualization.Build(output,bands,mode,MusicRadialDirection.CenterOut,2,1,i*.1,1);
        var modes=Enum.GetValues<MusicVisualizationMode>();var before=GC.GetAllocatedBytesForCurrentThread();var count=0;
        for(var i=0;i<100;i++)foreach(var mode in modes)count+=MusicVisualization.Build(output,bands,mode,MusicRadialDirection.CenterOut,2,1,i*.1,1);
        var allocation=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.True(count>0);Assert.Equal(0,allocation);
    }
}
