using System.Text.Json;
using XivSurface.Core;

namespace XivSurface.Core.Tests;

public sealed class MusicSpectrumConsumerTests
{
    private static string Frame(long sequence=1,long stream=1,double age=0,bool playing=true,bool fresh=true,float value=.2f,int count=32,float minimum=40,float maximum=16000)
        =>JsonSerializer.Serialize(new {version=1,streamId=stream,sequence,ageSeconds=age,positionSeconds=1,playing,fresh,
            spectrum=new {minimumHz=minimum,maximumHz=maximum,bands=Enumerable.Repeat(value,count).ToArray()}});

    [Fact] public void ExactOptionalContractUsesRealAll32Values()
    {
        var consumer=new MusicSpectrumConsumer();Assert.True(consumer.Update(Frame(),10));
        Assert.Equal(32,consumer.Sample(10).Length);Assert.All(consumer.Sample(10).ToArray(),v=>Assert.Equal(.2f,v));
        Assert.Equal(40,consumer.MinimumHz);Assert.Equal(16000,consumer.MaximumHz);Assert.Equal(10.2,consumer.ExpiresAt,8);
    }
    [Fact] public void MissingLegacySpectrumNeverFabricatesBinsFromThreeAggregateLevels()
    {
        var consumer=new MusicSpectrumConsumer();Assert.True(consumer.Update(Frame(),1));
        Assert.False(consumer.Update("{\"version\":1,\"streamId\":1,\"sequence\":2,\"ageSeconds\":0,\"positionSeconds\":1,\"playing\":true,\"fresh\":true,\"low\":1,\"mid\":1,\"high\":1}",1.02));
        Assert.True(consumer.Sample(1.02).IsEmpty);Assert.Equal(0,consumer.ExpiresAt);
    }
    [Theory] [InlineData(0)] [InlineData(31)] [InlineData(33)]
    public void WrongBandCountRefuses(int count)
    {var c=new MusicSpectrumConsumer();Assert.False(c.Update(Frame(count:count),1));Assert.True(c.Sample(1).IsEmpty);}
    [Theory] [InlineData(-.1f)] [InlineData(1.1f)]
    public void NonUnitBandRefuses(float value)
    {var c=new MusicSpectrumConsumer();Assert.False(c.Update(Frame(value:value),1));}
    [Theory] [InlineData(39,16000)] [InlineData(40,16001)] [InlineData(40,40)]
    public void InvalidFrequencyDomainRefuses(float minimum,float maximum)
    {var c=new MusicSpectrumConsumer();Assert.False(c.Update(Frame(minimum:minimum,maximum:maximum),1));}
    [Fact] public void SampleExpiresWithoutFurtherIpcAndDuplicateSequenceCannotRejuvenate()
    {
        var c=new MusicSpectrumConsumer();Assert.True(c.Update(Frame(age:.1),10));var deadline=c.ExpiresAt;
        Assert.True(c.Update(Frame(age:0),10.09));Assert.Equal(deadline,c.ExpiresAt);
        Assert.True(c.Sample(10.101).IsEmpty);Assert.False(c.Available);
        Assert.False(c.Update(Frame(age:0),10.102)); // the same expired sequence cannot restart
        Assert.True(c.Update(Frame(sequence:2),10.12));Assert.Equal(10.32,c.ExpiresAt,8);
    }
    [Fact] public void StreamChangesClearOldEnergiesAndRegressingCursorRefuses()
    {
        var c=new MusicSpectrumConsumer();Assert.True(c.Update(Frame(sequence:10,value:1),1));
        Assert.False(c.Update(Frame(sequence:9),1.02));Assert.True(c.Sample(1.02).IsEmpty);
        Assert.True(c.Update(Frame(sequence:1,stream:2,value:.01f),1.04));Assert.All(c.Sample(1.04).ToArray(),v=>Assert.Equal(.01f,v));
    }
    [Fact] public void PauseSilenceMalformedAndStaleClearImmediately()
    {
        foreach(var bad in new[]{Frame(playing:false),Frame(fresh:false),Frame(age:.20001),Frame(value:0),"{}","null","not json"})
        {var c=new MusicSpectrumConsumer();c.Update(Frame(sequence:1),1);Assert.False(c.Update(bad.Replace("\"sequence\":1","\"sequence\":2"),1.02));Assert.True(c.Sample(1.02).IsEmpty);}
    }
    [Fact] public void SameStreamProducerRestartRecoversOnNextSequenceWithoutOldEnergy()
    {
        var c=new MusicSpectrumConsumer();Assert.True(c.Update(Frame(sequence:900000,value:1),1));
        Assert.False(c.Update(Frame(sequence:1,value:.03f),1.02));Assert.True(c.Sample(1.02).IsEmpty);Assert.Equal(0,c.ExpiresAt);
        Assert.True(c.Update(Frame(sequence:2,value:.03f),1.04));Assert.All(c.Sample(1.04).ToArray(),v=>Assert.Equal(.03f,v));
    }
    [Fact] public void FrameAttackAndReleaseAreBoundedAndPollCadenceIndependent()
    {
        var a=new MusicSpectrumConsumer();var b=new MusicSpectrumConsumer();a.Update(Frame(value:.1f),1);b.Update(Frame(value:.1f),1);
        a.Update(Frame(sequence:2,value:.8f),1.02);b.Update(Frame(sequence:2,value:.8f),1.02);
        for(var i=1;i<=6;i++)a.Sample(1.02+i*.01);
        var av=a.Sample(1.08).ToArray();var bv=b.Sample(1.08).ToArray();
        for(var i=0;i<32;i++){Assert.InRange(av[i],.1f,.8f);Assert.Equal(av[i],bv[i],5);}
        Assert.True(a.Update(Frame(sequence:3,value:.02f),1.10));Assert.InRange(a.Sample(1.10)[0],.02f,av[0]);
    }
    [Fact] public void BadClockAndOversizedJsonFailClosed()
    {
        var c=new MusicSpectrumConsumer();c.Update(Frame(),1);Assert.True(c.Sample(.9).IsEmpty);
        Assert.False(c.Update(Frame(),double.NaN));Assert.False(c.Update(new string(' ',8193),2));
        Assert.False(c.Update(Frame(age:-.001),3));Assert.True(c.Sample(3).IsEmpty);
    }
}
