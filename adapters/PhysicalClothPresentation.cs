using System.Numerics;
using XivCloth.Core;
using XivSurface.Core;

namespace XivRug.Rendering;

/// <summary>Rest-material chart, not a transform applied to solved vertices.</summary>
internal readonly record struct PhysicalClothChart(Vector2 Center, Vector2 HalfSize, bool Circle, float Corner)
{
    public bool Valid => Finite(Center) && Finite(HalfSize)
        && Math.Abs(Center.X) <= IndexedClothPose.MaximumCoordinate && Math.Abs(Center.Y) <= IndexedClothPose.MaximumCoordinate
        && HalfSize.X > 0 && HalfSize.Y > 0 && HalfSize.X <= 100 && HalfSize.Y <= 100
        && (!Circle || HalfSize.X == HalfSize.Y)
        && float.IsFinite(Corner) && Corner >= 0 && Corner <= Math.Min(HalfSize.X, HalfSize.Y);
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}

/// <summary>One framework observation. Reference identity revokes all work from
/// a previous observation, even if owner, scene and proxy values are equal.</summary>
internal sealed record PhysicalClothObservation
{
    public FootProxyPose AcceptedFeet { get; }
    public FootProxyPose InspectedFeet { get; }
    public long SceneGeneration { get; }
    internal PairedPlantObservation? AcceptedPlant { get; init; }
    internal PairedPlantObservation? InspectedPlant { get; init; }
    internal MeasuredTriangleScene? PlantScene { get; init; }
    internal PhysicalClothObservation(FootProxyPose accepted, FootProxyPose inspected, long sceneGeneration)
    { AcceptedFeet = accepted; InspectedFeet = inspected; SceneGeneration = sceneGeneration; }
}

internal sealed record PhysicalClothDrawFrame(IndexedClothPose Pose, PhysicalClothChart Chart,
    XpbdFootPublicationBinding Binding);

/// <summary>Framework-to-render publication boundary. The owning compositor
/// serializes EVERY call and the entire GPU submission with its render lock.
/// BeginObservation/Invalidate must run before simulation, owner/scene changes,
/// resets and native capture failures. This does not sample game memory, prove
/// footwear geometry or establish that a terrain source is authoritative.</summary>
internal sealed class PhysicalClothPresentation
{
    private PhysicalClothObservation? observation;
    private PhysicalClothDrawFrame? published;
    private double lastCheckedAt = double.NegativeInfinity;

    public bool IsCurrent(PhysicalClothObservation? expected) => expected is not null && ReferenceEquals(expected, observation);

    public PhysicalClothObservation? BeginObservation(FootProxyPose? accepted, FootProxyPose? inspected,
        long sceneGeneration, double now)
    {
        Invalidate();
        if (!CheckTime(now) || sceneGeneration <= 0 || accepted is null || inspected is null
            || accepted.Identity != inspected.Identity || !accepted.FreshAt(now) || !inspected.FreshAt(now)) return null;
        observation = new(accepted, inspected, sceneGeneration);
        return observation;
    }

    public PhysicalClothObservation? BeginPairedObservation(PairedPlantObservation accepted,PairedPlantObservation inspected,
        MeasuredTriangleScene scene,double now)
    {
        Invalidate();
        if(accepted is null||inspected is null||!ReferenceEquals(accepted.Policy,inspected.Policy)
            ||!ReferenceEquals(accepted.Policy.Scene,scene))return null;
        var candidate=BeginObservation(accepted.Actual,inspected.Actual,scene.Generation,now);
        if(candidate is null)return null;
        observation=candidate with{AcceptedPlant=accepted,InspectedPlant=inspected,PlantScene=scene};return observation;
    }

    public bool TryPublish(PhysicalClothObservation? expected, XpbdFootCapture capture,
        PhysicalClothChart chart, double now)
    {
        // An obsolete worker cannot revoke a newer successful observation.
        if (expected is null || !IsCurrent(expected)) return false;
        published = null;
        if (!CheckTime(now) || !chart.Valid || capture.Status != XpbdStatus.Ready
            || capture.Frame is not { FootBinding: { } binding } frame || frame.EndpointInterpolationAllowed
            || frame.SceneGeneration != expected.SceneGeneration
            || !BindingCurrent(binding,expected,now)) return false;
        try
        {
            // All copying/normals are done on the publisher, never the native
            // draw callback. No heightfield conversion or visual displacement.
            published = new(new(frame.Positions, frame.UV, frame.Indices), chart, binding);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public bool TryGet(uint zone, double now, out PhysicalClothDrawFrame? frame)
    {
        frame = null;
        if (!CheckTime(now)) { Invalidate(); return false; }
        if (observation is not { } active) return false;
        if (active.InspectedFeet.Identity.Zone != zone || !active.AcceptedFeet.FreshAt(now) || !active.InspectedFeet.FreshAt(now))
        { Invalidate(); return false; }
        // A native draw can arrive while the framework prepares a publication.
        // Hide that draw without cancelling the still-current observation.
        if (published is not { } candidate) return false;
        if (!BindingCurrent(candidate.Binding,active,now))
        { Invalidate(); return false; }
        frame = candidate;
        return true;
    }

    public void Invalidate() { observation = null; published = null; }

    /// <summary>Recheck AFTER GPU preparation, immediately before each CPU draw
    /// submission. Does not attest when queued GPU work reaches the display.</summary>
    public bool CanSubmit(PhysicalClothDrawFrame? expected, uint zone, double now)
        => expected is not null && ReferenceEquals(expected, published)
            && TryGet(zone, now, out var current) && ReferenceEquals(expected, current);

    private bool CheckTime(double now)
    {
        if (!double.IsFinite(now) || now < 0 || now < lastCheckedAt) return false;
        lastCheckedAt = now;
        return true;
    }
    private static bool BindingCurrent(XpbdFootPublicationBinding binding,PhysicalClothObservation current,double now)
        =>current.PlantScene is {} scene
            ?binding.CanPresentPaired(current.AcceptedPlant,current.InspectedPlant,scene,now)
            :binding.CanPresent(current.AcceptedFeet,current.InspectedFeet,current.SceneGeneration,now);
}
