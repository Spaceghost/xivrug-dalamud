namespace XivSurface.Core;

public enum ClothFootRenderGate
{
    Allowed,
    MissingFrame,
    ShortOutput,
    Suppressed,
    IncompleteTracking,
    WrongZone,
    InvalidClock,
    Expired,
    UnsupportedEnvelope,
}

/// <summary>
/// Immutable, bounded foot-mask publication for the render thread. Complete
/// measured boots must protect the whole frame; an absent/partial/old mask is
/// not permission to draw unguarded cloth. Render-only padding assumes bounded
/// endpoint motion, not an attested same-render-frame skeleton or equipment mesh.
/// </summary>
public sealed class ClothFootRenderFrame
{
    public const double MaximumAgeSeconds = 1d / 30;
    public const double MaximumAdaptiveClothAgeSeconds = .1;
    // Engineering bounds on each endpoint's TOTAL world-space motion, including
    // character translation and animation. They are not measured game limits.
    public const float HorizontalSpeedLimit = 20;
    public const float DownwardSpeedLimit = 20;
    // The whole estimated boot radius is the solid core. Feathering belongs
    // outside it, including at age zero; maximum boot + 33.3 ms travel needs
    // an outer radius of about 1.439 yalms.
    public const float MaximumRenderRadius = 1.45f;
    // Continuous cloth sweeps measured boots for its cadence-based lifetime.
    // One upward-rounded float matches the publication's radius calculation.
    public static readonly float MaximumClothRenderRadius = MathF.BitIncrement(
        ClothFootClearance.MaximumBootRadius + HorizontalSpeedLimit * (float)MaximumAdaptiveClothAgeSeconds);
    public const float MaximumDownwardPadding = .67f;
    private readonly ClothFootContact[] contacts;
    private readonly bool protectedFrame;
    public uint Zone { get; }
    public double SampledAt { get; }
    public double ClothMaximumAgeSeconds { get; }
    public bool Suppressed { get; }
    /// <summary>Identity bound by the producer's actual read transaction.
    /// Default/legacy identities do not authorize physical avatar contact.</summary>
    public ClothFootCaptureIdentity CaptureIdentity { get; }
    public ReadOnlySpan<ClothFootContact> Contacts => contacts;

    public ClothFootRenderFrame(uint zone, double sampledAt, ReadOnlySpan<ClothFootContact> contacts,
        bool suppressed = false, double clothMaximumAgeSeconds = MaximumAgeSeconds,
        ClothFootCaptureIdentity captureIdentity = default)
    {
        if (!double.IsFinite(clothMaximumAgeSeconds) || clothMaximumAgeSeconds < MaximumAgeSeconds
            || clothMaximumAgeSeconds > MaximumAdaptiveClothAgeSeconds)
            throw new ArgumentOutOfRangeException(nameof(clothMaximumAgeSeconds));
        if (captureIdentity != default && (!captureIdentity.Valid || captureIdentity.Zone != zone))
            throw new ArgumentException("Capture identity must match the frame zone.", nameof(captureIdentity));
        Zone = zone;
        SampledAt = sampledAt;
        ClothMaximumAgeSeconds = clothMaximumAgeSeconds;
        Suppressed = suppressed;
        CaptureIdentity = captureIdentity;
        this.contacts = contacts[..Math.Min(contacts.Length, ClothFootClearance.MaximumContacts)].ToArray();
        protectedFrame = HasCompleteProtection(this.contacts);
    }

    /// <summary>Build an owned immutable capture at the actual before/after
    /// native read, not by attaching today's identity to yesterday's frame.
    /// This records observed identity continuity, not GPU-pose attestation.</summary>
    public static bool TryCaptureBound(ClothFootCaptureIdentity beforeRead, ClothFootCaptureIdentity afterRead,
        double readStartedAt, double readCompletedAt, ReadOnlySpan<ClothFootContact> contacts,
        out ClothFootRenderFrame? frame)
    {
        frame = null;
        if (!beforeRead.Bound || beforeRead != afterRead || contacts.Length != ClothFootClearance.MaximumContacts
            || !double.IsFinite(readStartedAt) || readStartedAt < 0
            || !double.IsFinite(readCompletedAt) || readCompletedAt < readStartedAt
            || readCompletedAt > readStartedAt + MaximumAgeSeconds) return false;
        var candidate = new ClothFootRenderFrame(beforeRead.Zone, readStartedAt, contacts, captureIdentity: beforeRead);
        Span<ClothFootContact> checkedContacts = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        if (!candidate.TryGetClothContacts(beforeRead.Zone, readCompletedAt, checkedContacts, out _)) return false;
        frame = candidate; return true;
    }

    public bool CanRender(uint zone, double now)
    {
        Span<ClothFootContact> render = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        return TryGetRenderContacts(zone, now, render);
    }

    /// <summary>
    /// Build four render-only contacts without changing measured pressure.
    /// Dividing (boot radius + travel) by .7 puts the feather outside the whole
    /// estimated boot, rather than letting cloth partially cover its outer 30%.
    /// Lowering the sole ceiling also covers descending boots. Equal per-pair
    /// radius/height preserves the shader's continuous heel-to-toe bridge.
    /// Missing-pose fallback guards cannot establish these motion bounds and
    /// fail closed. No expansion is silently clamped or dropped at the GPU.
    /// </summary>
    public bool TryGetRenderContacts(uint zone, double now, Span<ClothFootContact> destination)
        => TryGetRenderContacts(zone, now, destination, out _);

    /// <summary>Same fail-closed draw gate, with a reason for bounded runtime diagnostics.</summary>
    public bool TryGetRenderContacts(uint zone, double now, Span<ClothFootContact> destination,
        out ClothFootRenderGate gate)
    {
        // Legacy transparency keeps its original envelope and time limit.
        if (!TryGetContacts(zone, now, destination, MaximumAgeSeconds, out gate)) return false;
        destination[..ClothFootClearance.MaximumContacts].Clear();
        gate = ClothFootRenderGate.UnsupportedEnvelope;
        var age = now - SampledAt;
        var horizontal = HorizontalSpeedLimit * age;
        var downward = DownwardSpeedLimit * age;
        if (!double.IsFinite(horizontal) || !double.IsFinite(downward) || downward > MaximumDownwardPadding) return false;
        Span<ClothFootContact> expanded = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        for (var i = 0; i < expanded.Length; i += 2)
        {
            // Legacy floor projection still uses its conservative occlusion
            // envelope. Continuous cloth instead deforms under raw contacts.
            var radius = MathF.BitIncrement((float)((Math.Max(contacts[i].Radius, contacts[i + 1].Radius) + horizontal) / .7f));
            var sole = MathF.BitDecrement((float)(Math.Min(contacts[i].FootY, contacts[i + 1].FootY) - downward));
            if (!float.IsFinite(radius) || radius > MaximumRenderRadius || !float.IsFinite(sole)) return false;
            expanded[i] = contacts[i] with { Radius = radius, FootY = sole };
            expanded[i + 1] = contacts[i + 1] with { Radius = radius, FootY = sole };
            if (!expanded[i].Valid || !expanded[i + 1].Valid) return false;
        }
        expanded.CopyTo(destination);
        gate = ClothFootRenderGate.Allowed;
        return true;
    }

    /// <summary>Measured contact constraints for continuous cloth, never an
    /// enlarged render exclusion. Keeps the same ownership/freshness checks;
    /// the final vertex projection presses cloth to its actual ground bound.</summary>
    public bool TryGetClothContacts(uint zone, double now, Span<ClothFootContact> destination,
        out ClothFootRenderGate gate)
        => TryGetContacts(zone, now, destination, ClothMaximumAgeSeconds, out gate);

    private bool TryGetContacts(uint zone, double now, Span<ClothFootContact> destination,
        double maximumAge, out ClothFootRenderGate gate)
    {
        destination[..Math.Min(destination.Length, ClothFootClearance.MaximumContacts)].Clear();
        gate = ClothFootRenderGate.ShortOutput;
        if (destination.Length < ClothFootClearance.MaximumContacts) return false;
        gate = ClothFootRenderGate.Suppressed;
        if (Suppressed) return false;
        gate = ClothFootRenderGate.IncompleteTracking;
        if (!protectedFrame) return false;
        gate = ClothFootRenderGate.WrongZone;
        if (Zone == 0 || zone != Zone) return false;
        gate = ClothFootRenderGate.InvalidClock;
        if (!double.IsFinite(SampledAt) || SampledAt < 0 || !double.IsFinite(now) || now < SampledAt) return false;
        gate = ClothFootRenderGate.Expired;
        if (now > SampledAt + maximumAge) return false;
        contacts.CopyTo(destination);
        gate = ClothFootRenderGate.Allowed;
        return true;
    }

    /// <summary>Render-time swept deformation, NOT a transparency mask. Keep
    /// measured radii for spring pressure, but flatten continuous fabric over
    /// bounded travel since sampling so moving soles do not outrun the clamp.
    /// Triangle-footprint padding is added separately by the vertex shader.</summary>
    public bool TryGetClothRenderContacts(uint zone, double now, Span<ClothFootContact> destination,
        out ClothFootRenderGate gate)
    {
        if (!TryGetClothContacts(zone, now, destination, out gate)) return false;
        Span<ClothFootContact> expanded = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        var age = now - SampledAt;
        for (var i = 0; i < expanded.Length; i += 2)
        {
            var radius = MathF.BitIncrement((float)(Math.Max(contacts[i].Radius, contacts[i + 1].Radius) + HorizontalSpeedLimit * age));
            var sole = MathF.BitDecrement((float)(Math.Min(contacts[i].FootY, contacts[i + 1].FootY) - DownwardSpeedLimit * age));
            expanded[i] = contacts[i] with { Radius = radius, FootY = sole };
            expanded[i + 1] = contacts[i + 1] with { Radius = radius, FootY = sole };
            // Large monotonic clocks can round an inclusive expiry endpoint
            // slightly outward. Never hand a radius beyond the consumer's
            // declared bound to the renderer, even when that clock gate passed.
            if (!expanded[i].Valid || !expanded[i + 1].Valid || radius > MaximumClothRenderRadius)
            {
                destination[..ClothFootClearance.MaximumContacts].Clear();
                gate = ClothFootRenderGate.UnsupportedEnvelope; return false;
            }
        }
        expanded.CopyTo(destination); return true;
    }

    private static bool HasCompleteProtection(ReadOnlySpan<ClothFootContact> contacts)
    {
        return contacts.Length == ClothFootClearance.MaximumContacts
            && CompleteBoot(contacts[0], contacts[1]) && CompleteBoot(contacts[2], contacts[3]);
    }

    private static bool CompleteBoot(ClothFootContact heel, ClothFootContact toe) => heel.Valid && toe.Valid
        && heel.Radius <= ClothFootClearance.MaximumBootRadius && toe.Radius <= ClothFootClearance.MaximumBootRadius
        && Math.Abs(heel.FootY - toe.FootY) <= .0001f && Math.Abs(heel.Radius - toe.Radius) <= .0001f
        && System.Numerics.Vector2.DistanceSquared(heel.Center, toe.Center) <= .75f * .75f;
}
