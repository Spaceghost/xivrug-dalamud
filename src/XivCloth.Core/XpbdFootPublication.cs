namespace XivCloth.Core;

/// <summary>Bounded read-only admission result. Failure never carries a frame.
/// Counters include every capsule/face attempt and dyadic interval node.</summary>
public readonly record struct XpbdFootCapture(XpbdStatus Status,XpbdFrame? Frame,int TrianglePairs,int SweepNodes)
{
    public int BroadphaseNodes { get; init; }
    public int WorkUsed=>TrianglePairs+SweepNodes+BroadphaseNodes;
}

/// <summary>Immutable binding to the EXACT accepted simulation observation and
/// inspected stationary capsule pose. This is not GPU-pose attestation, motion
/// reconstruction, or permission to substitute a fresh identity onto old XYZ.
/// Caller must invalidate its publication on reset/scene/owner change and pass
/// its current exact observation references to CanPresent before drawing.
/// Clock checking is external: no ambient time, native memory or mutable solver
/// is consulted by this immutable record.</summary>
public sealed class XpbdFootPublicationBinding
{
    public FootProxyPose AcceptedFeet { get; }
    public FootProxyPose InspectedFeet { get; }
    public long SceneGeneration { get; }
    public double InspectedAt { get; }
    public double ExpiresAt { get; }
    public float Clearance { get; }
    /// <summary>Non-null means an explicit local zero-thickness policy was
    /// certified. Clearance remains the margin of OTHER pairs, not a uniform
    /// promise. Ordinary CanPresent deliberately refuses this binding.</summary>
    public PairedPlantPolicy? PlantPolicy { get; }
    public PairedPlantObservation? AcceptedPlant { get; }
    public PairedPlantObservation? InspectedPlant { get; }
    internal XpbdFootPublicationBinding(FootProxyPose accepted,FootProxyPose inspected,long generation,double now,float clearance)
        :this(accepted,inspected,generation,now,clearance,null,null,null) { }
    internal XpbdFootPublicationBinding(FootProxyPose accepted,FootProxyPose inspected,long generation,double now,float clearance,
        PairedPlantPolicy? plant,PairedPlantObservation? acceptedPlant,PairedPlantObservation? inspectedPlant)
    {
        AcceptedFeet=accepted;InspectedFeet=inspected;SceneGeneration=generation;InspectedAt=now;Clearance=clearance;
        ExpiresAt=Math.Min(accepted.SampledAt+FootProxyPose.MaximumAgeSeconds,inspected.SampledAt+FootProxyPose.MaximumAgeSeconds);
        PlantPolicy=plant;AcceptedPlant=acceptedPlant;InspectedPlant=inspectedPlant;
    }
    public bool CanPresent(FootProxyPose? currentAccepted,FootProxyPose? currentInspected,long currentSceneGeneration,double now)
        =>PlantPolicy is null&&ReferenceEquals(AcceptedFeet,currentAccepted)&&ReferenceEquals(InspectedFeet,currentInspected)
            &&SceneGeneration==currentSceneGeneration&&double.IsFinite(now)&&now>=InspectedAt&&now<=ExpiresAt;
    public bool CanPresentPaired(PairedPlantObservation? currentAccepted,PairedPlantObservation? currentInspected,
        MeasuredTriangleScene currentScene,double now)
        =>PlantPolicy is not null&&ReferenceEquals(PlantPolicy.Scene,currentScene)
            &&ReferenceEquals(AcceptedPlant,currentAccepted)&&ReferenceEquals(InspectedPlant,currentInspected)
            &&SceneGeneration==currentScene.Generation&&double.IsFinite(now)&&now>=InspectedAt&&now<=ExpiresAt;
}
