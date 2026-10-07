using System.Numerics;

namespace XivSurface.Core;

/// <summary>A read-only, world-space boot footprint. FootY is a conservative sole ceiling, not a bone centre.</summary>
public readonly record struct ClothFootContact(Vector2 Center, float FootY, float Radius)
{
    public bool Valid => MathEx.Finite(Center) && float.IsFinite(FootY) && float.IsFinite(Radius)
        && Math.Abs(Center.X) <= 1_000_000 && Math.Abs(Center.Y) <= 1_000_000 && Math.Abs(FootY) <= 1_000_000
        && Radius is >= .02f and <= ClothFootClearance.FallbackRadius;

    /// <summary>Full protection in the inner 70%, then a smooth feather outside the boot core.</summary>
    public float Influence(Vector2 point)
    {
        if (!Valid || !MathEx.Finite(point)) return 0;
        var distance = Vector2.Distance(point, Center) / Radius;
        if (distance <= .7f) return 1;
        if (distance >= 1) return 0;
        var t = Math.Clamp((1 - distance) / .3f, 0, 1);
        return t * t * (3 - 2 * t);
    }
}

/// <summary>Unlifted mesh height; flourish multiplier; local render exclusion when floor/sole bounds conflict.</summary>
public readonly record struct ClothFootClearanceSample(float Height, float FlourishScale, float ExclusionWeight);

/// <summary>
/// A floor-safe contact constraint evaluated independently of emote/idle pressure.
/// Never pushes cloth through its measured floor or changes X/Z. If a floor bound
/// leaves no space below the foot, the renderer must exclude that local overlapping
/// fragment; lowering through the floor is not an acceptable substitute.
/// </summary>
public static class ClothFootClearance
{
    public const float SoleGap = .012f;
    public const int MaximumContacts = 4;
    // These limits also bound the missing-pose render guard. A root-level,
    // boot-sized guard cannot protect a lower stair or an extended stride.
    public const float MaximumJointDistance = 2;
    public const float MaximumJointHeight = 1.5f;
    public const float MaximumScale = 4;
    public const float NominalBootRadius = .22f;
    public const float MaximumBootRadius = .34f;
    public const float SoleInset = .045f;
    public const float FallbackRadius = 3.35f; // inner 70% covers 2 + .34 yalms
    public const float FallbackSoleInset = MaximumJointHeight + SoleInset * MaximumScale;

    /// <summary>
    /// Rendering-only guard for a missing pose. Measured heel/toe pairs keep
    /// their slots; a vacant pair gets the full plausible current-player
    /// envelope admitted by TryBoot, including a lower foot on stairs.
    /// This is not a measured foot contact and must never generate cloth pressure.
    /// No old joint positions are retained across movement, models or territories.
    /// </summary>
    public static ClothFootContact[] WithFallback(ReadOnlySpan<ClothFootContact> measured, Vector3? player)
    {
        var result = new ClothFootContact[MaximumContacts];
        measured[..Math.Min(measured.Length, MaximumContacts)].CopyTo(result);
        if (player is not { } position || !MathEx.Finite(position)
            || Math.Abs(position.X) > 1_000_000 || Math.Abs(position.Y) > 1_000_000 || Math.Abs(position.Z) > 1_000_000)
            return result;
        // Pair validity is all-or-nothing: never join one measured foot to a
        // guessed second endpoint. Preserve complete boots and guard the rest.
        var guarded = false;
        for (var i = 0; i < MaximumContacts; i += 2)
        {
            if (result[i].Valid && result[i + 1].Valid) continue;
            result[i] = guarded ? default : new(new(position.X, position.Z), position.Y - FallbackSoleInset, FallbackRadius);
            result[i + 1] = default;
            guarded = true;
        }
        return result;
    }

    /// <summary>GPU float4: centre X, sole ceiling Y, centre Z, radius. W=0 disables invalid/absent entries.</summary>
    public static Vector4 Pack(ReadOnlySpan<ClothFootContact> contacts, int index)
    {
        if (index < 0 || index >= contacts.Length || index >= MaximumContacts || !contacts[index].Valid) return default;
        var foot = contacts[index];
        return new(foot.Center.X, foot.FootY, foot.Center.Y, foot.Radius);
    }

    public static ClothFootClearanceSample Evaluate(Vector3 clothPosition, float floorMinimumY,
        ReadOnlySpan<ClothFootContact> contacts, float visualLift = 0)
    {
        if (!MathEx.Finite(clothPosition) || !float.IsFinite(floorMinimumY))
            throw new ArgumentException("Cloth and measured floor bounds must be finite.");
        if (!float.IsFinite(visualLift) || visualLift is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(visualLift));
        var original = Math.Max(clothPosition.Y, floorMinimumY);
        var height = original; var coverage = 0f;
        var bounded = contacts[..Math.Min(contacts.Length, MaximumContacts)];
        foreach (var foot in bounded)
        {
            Constrain(foot);
        }
        for (var i = 0; i + 1 < bounded.Length; i += 2)
            if (TryBridge(new(clothPosition.X, clothPosition.Z), bounded[i], bounded[i + 1], out var bridge))
                Constrain(bridge);
        var presented = new Vector3(clothPosition.X, height + visualLift, clothPosition.Z);
        return new(height, 1 - coverage, Exclusion(presented, contacts));

        void Constrain(ClothFootContact foot)
        {
            var influence = foot.Influence(new(clothPosition.X, clothPosition.Z));
            if (influence <= 0) return;
            coverage = Math.Max(coverage, influence);
            var ceiling = foot.FootY - SoleGap - visualLift;
            // Every candidate starts from the same height; overlapping contacts
            // are order-independent rather than repeatedly multiplying pressure.
            var candidate = original + (Math.Min(original, ceiling) - original) * influence;
            height = Math.Min(height, Math.Max(floorMinimumY, candidate));
        }
    }

    /// <summary>Renderer parity: apply after Finish.y, using world X/Z and the same foot ceiling.</summary>
    public static float Exclusion(Vector3 presentedWorld, ReadOnlySpan<ClothFootContact> contacts)
    {
        if (!MathEx.Finite(presentedWorld)) return 1; // malformed rendered geometry fails closed
        var exclusion = 0f;
        var bounded = contacts[..Math.Min(contacts.Length, MaximumContacts)];
        foreach (var foot in bounded)
            if (foot.Valid && presentedWorld.Y > foot.FootY - SoleGap + .0001f)
                exclusion = Math.Max(exclusion, foot.Influence(new(presentedWorld.X, presentedWorld.Z)));
        // A boot occupies the space between heel and toe, not just two discs.
        // Pairing is explicit by slot; never bridge left and right feet.
        for (var i = 0; i + 1 < bounded.Length; i += 2)
            if (TryBridge(new(presentedWorld.X, presentedWorld.Z), bounded[i], bounded[i + 1], out var bridge)
                && presentedWorld.Y > bridge.FootY - SoleGap + .0001f)
                exclusion = Math.Max(exclusion, bridge.Influence(new(presentedWorld.X, presentedWorld.Z)));
        return exclusion;
    }

    private static bool TryBridge(Vector2 point, ClothFootContact a, ClothFootContact b, out ClothFootContact bridge)
    {
        bridge = default;
        if (!a.Valid || !b.Valid || Math.Abs(a.FootY - b.FootY) > .0001f || Math.Abs(a.Radius - b.Radius) > .0001f)
            return false;
        var segment = b.Center - a.Center;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared > .75f * .75f) return false;
        var t = lengthSquared > .000001f ? Math.Clamp(Vector2.Dot(point - a.Center, segment) / lengthSquared, 0, 1) : 0;
        bridge = new(a.Center + segment * t, Math.Min(a.FootY, b.FootY), Math.Min(a.Radius, b.Radius));
        return true;
    }

    /// <summary>
    /// Use the largest positive skeleton axis for the whole estimated boot
    /// envelope. This deliberately overestimates nonuniformly scaled feet:
    /// yaw/tilt must not turn an ignored tall or wide axis into exposed footwear.
    /// Mirrored/degenerate transforms have no established sole model here.
    /// </summary>
    public static bool TryBoot(Vector3 foot, Vector3 toe, Vector3 player, Vector3 scale,
        out ClothFootContact heelContact, out ClothFootContact toeContact)
    {
        heelContact = toeContact = default;
        if (!MathEx.Finite(scale) || scale.X is < .2f or > MaximumScale
            || scale.Y is < .2f or > MaximumScale || scale.Z is < .2f or > MaximumScale) return false;
        return TryBoot(foot, toe, player, Math.Max(scale.X, Math.Max(scale.Y, scale.Z)),
            out heelContact, out toeContact);
    }

    /// <summary>
    /// Converts the actual animated foot/toe joints into two overlapping boot
    /// capsule endpoints. Joint positions do not describe equipment meshes, so use a modest
    /// conservative sole inset and protect low/planted toes at character foot Y.
    /// Raised feet retain their actual height instead of pressing a fictitious
    /// contact down onto the floor. Nothing here writes a character transform.
    /// </summary>
    public static bool TryBoot(Vector3 foot, Vector3 toe, Vector3 player, float scale,
        out ClothFootContact heelContact, out ClothFootContact toeContact)
    {
        heelContact = toeContact = default;
        if (!MathEx.Finite(foot) || !MathEx.Finite(toe) || !MathEx.Finite(player) || !float.IsFinite(scale)
            || scale is < .2f or > MaximumScale
            || HeadingDistance(foot, player) > MaximumJointDistance || HeadingDistance(toe, player) > MaximumJointDistance
            || Math.Abs(foot.Y - player.Y) > MaximumJointHeight || Math.Abs(toe.Y - player.Y) > MaximumJointHeight
            || Vector3.Distance(foot, toe) is < .015f or > .75f) return false;
        // Never clamp an oversized estimated boot down to the GPU's measured
        // footprint budget. Missing protection makes the draw gate hide the
        // rug; drawing a smaller mask would quietly expose the boot's edges.
        var radius = Math.Max(NominalBootRadius * scale, .11f);
        if (radius > MaximumBootRadius) return false;
        var sole = Math.Min(toe.Y, foot.Y) - SoleInset * scale;
        if (Math.Min(toe.Y, foot.Y) - player.Y <= .18f * scale)
            sole = Math.Min(sole, player.Y + .004f);
        // Keep the soft feather outside the nominal boot core, including a
        // modest allowance for footwear wider than the animated joints.
        heelContact = new(new(foot.X, foot.Z), sole, radius);
        toeContact = new(new(toe.X, toe.Z), sole, radius);
        return heelContact.Valid && toeContact.Valid;
    }

    private static float HeadingDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
