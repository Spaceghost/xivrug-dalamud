using System.Numerics;

namespace XivCloth.Core;

/// <summary>Three independent, equally compliant XYZ constraints on one
/// particle. Position is absolute in the simulation's coordinate system.
/// Compliance follows the existing XPBD alpha/dt² convention, not a lerp rate.</summary>
public readonly record struct XpbdTarget(int Particle,Vector3 Position,float Compliance);

/// <summary>Immutable input for the fixed substeps actually produced by ONE
/// Advance call. Nothing is latched or queued: a no-substep call discards its
/// snapshot. Changing inputs should be sampled at fixed-step boundaries; this
/// type does not reconstruct wall-clock force intervals from fractional calls.
/// Indices refer to the EXACT admitted immutable definition, not a similarly
/// sized or reordered mesh. Pin-target conflicts deliberately keep the pin.
/// Distances are simulation units; accelerations are ADDITIONAL units/second²
/// on top of settings.Gravity. They are not forces requiring mass division.</summary>
public sealed class XpbdStepInputs
{
    public const int MaximumTargets=64;
    public const float MaximumAcceleration=30;
    public const float MaximumTargetDistance=1;
    public const float MaximumTargetCorrectionPerSubstep=.04f;
    private readonly Vector3[] accelerations;
    private readonly XpbdTarget[] targets;
    internal XpbdDefinition Definition { get; }
    public int ParticleCount=>Definition.VertexCount;
    /// <summary>Per-particle additional acceleration, bounded at30 separately
    /// from gravity. The combined norm can reach60 before velocity limiting.</summary>
    public ReadOnlySpan<Vector3> Accelerations=>accelerations;
    public ReadOnlySpan<XpbdTarget> Targets=>targets;
    public XpbdStepInputs(XpbdDefinition definition,ReadOnlySpan<Vector3> accelerations=default,ReadOnlySpan<XpbdTarget> targets=default)
    {
        Definition=definition??throw new ArgumentNullException(nameof(definition));
        if((accelerations.Length!=0&&accelerations.Length!=definition.VertexCount)||targets.Length>MaximumTargets)
            throw new ArgumentException("Invalid bounded input shape.");
        foreach(var a in accelerations)
            if(!Geometry.Finite(a)||a.LengthSquared()>MaximumAcceleration*MaximumAcceleration)
                throw new ArgumentException("Invalid bounded acceleration.");
        Span<bool> seen=stackalloc bool[XpbdDefinition.MaximumVertices];seen.Clear();
        foreach(var t in targets)
        {
            if((uint)t.Particle>=(uint)definition.VertexCount||seen[t.Particle]||!Geometry.Valid(t.Position)
                ||!float.IsFinite(t.Compliance)||t.Compliance is <0 or >1)
                throw new ArgumentException("Invalid or duplicate target.");
            seen[t.Particle]=true;
        }
        this.accelerations=accelerations.ToArray();this.targets=targets.ToArray();
    }
    internal bool AdmittedAt(Vector3[] positions,float[] weights)
    {
        foreach(var t in targets)
            if(weights[t.Particle]!=0&&Vector3.DistanceSquared(positions[t.Particle],t.Position)>MaximumTargetDistance*MaximumTargetDistance)
                return false;
        return true;
    }
}
