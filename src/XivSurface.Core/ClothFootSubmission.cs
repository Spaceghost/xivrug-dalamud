namespace XivSurface.Core;

/// <summary>A bounded fallback draw transaction. Its contacts cover preparation
/// time as well as sample age. The caller checks CanSubmit immediately before
/// EVERY GPU draw, using the current publication and monotonic clock. This is
/// not native-pose attestation; finalized native frames use their own lease.</summary>
public readonly struct ClothFootSubmission
{
    // Cold resource creation may exceed this bound and skip its first draw.
    // No old foot clock is renewed to make a slow preparation pass.
    public const double MaximumPreparationSeconds = .008;
    private readonly ClothFootRenderFrame? frame;
    private readonly uint zone;
    private readonly double preparedAt;
    public double CoveredUntil { get; }

    private ClothFootSubmission(ClothFootRenderFrame frame, uint zone, double preparedAt, double coveredUntil)
    { this.frame = frame; this.zone = zone; this.preparedAt = preparedAt; CoveredUntil = coveredUntil; }

    public static bool TryPrepare(ClothFootRenderFrame? frame, uint zone, double now, bool cloth,
        Span<ClothFootContact> contacts, out ClothFootSubmission submission, out ClothFootRenderGate gate)
    {
        submission = default;
        contacts.Clear();
        gate = ClothFootRenderGate.MissingFrame;
        if (frame is null) return false;
        gate = ClothFootRenderGate.InvalidClock;
        if (!double.IsFinite(now) || now < frame.SampledAt) return false;
        var lifetime = cloth ? frame.ClothMaximumAgeSeconds : ClothFootRenderFrame.MaximumAgeSeconds;
        var until = Math.Min(now + MaximumPreparationSeconds, frame.SampledAt + lifetime);
        gate = ClothFootRenderGate.Expired;
        if (until < now) return false;
        var allowed = cloth
            ? frame.TryGetClothRenderContacts(zone, until, contacts, out gate)
            : frame.TryGetRenderContacts(zone, until, contacts, out gate);
        if (!allowed) return false;
        submission = new(frame, zone, now, until);
        return true;
    }

    public bool CanSubmit(ClothFootRenderFrame? current, uint currentZone, double now)
        => frame is not null && ReferenceEquals(frame, current) && zone == currentZone
            && double.IsFinite(now) && now >= preparedAt && now <= CoveredUntil;
}
