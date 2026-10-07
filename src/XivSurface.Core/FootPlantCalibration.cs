using System.Numerics;

namespace XivSurface.Core;

/// <summary>Caller-selected, actual rendered support triangles for precisely
/// these four marker XZ positions. This validates geometry, not its native source
/// or visibility: the terrain adapter still owns those proofs. No nearest-layer
/// search, interpolation across treads, or collision-ramp substitution.</summary>
public sealed class FootSupportObservation
{
    private readonly Vector3[] support;
    private readonly FloorTriangle[] triangles;
    private readonly Vector3[] normals;
    public RawFootFrame Frame { get; }
    public long SceneGeneration { get; }
    public ReadOnlySpan<Vector3> Points => support;
    public ReadOnlySpan<Vector3> Normals => normals;
    public ReadOnlySpan<FloorTriangle> Triangles => triangles;
    private FootSupportObservation(RawFootFrame frame, long generation, Vector3[] points, FloorTriangle[] triangles, Vector3[] normals)
    { Frame = frame; SceneGeneration = generation; support = points; this.triangles = triangles; this.normals = normals; }
    public static bool TryCreate(RawFootFrame frame, long sceneGeneration,
        ReadOnlySpan<FloorTriangle> markerTriangles, out FootSupportObservation? observation)
    {
        observation = null;
        if (frame is null || sceneGeneration <= 0 || markerTriangles.Length != 4) return false;
        var points = new Vector3[4];
        var normals = new Vector3[4];
        for (var i = 0; i < 4; i++)
        {
            var t = markerTriangles[i]; var p = frame.Markers[i].Position;
            if (!FootModelMath.World(t.A) || !FootModelMath.World(t.B) || !FootModelMath.World(t.C)) return false;
            var normal = Vector3.Cross(t.B - t.A, t.C - t.A);
            if (!FootModelMath.Finite(normal) || normal.LengthSquared() < 1e-8f
                || Vector3.Normalize(normal).Y < .98f) return false;
            normals[i] = Vector3.Normalize(normal);
            var area = Area(t.A, t.B, t.C.X, t.C.Z);
            if (Math.Abs(area) < 1e-8) return false;
            var a = Area(t.B, t.C, p.X, p.Z) / area;
            var c = Area(t.A, t.B, p.X, p.Z) / area;
            var b = 1 - a - c;
            if (a < 0 || b < 0 || c < 0) return false;
            points[i] = new(p.X, (float)(a * t.A.Y + b * t.B.Y + c * t.C.Y), p.Z);
        }
        observation = new(frame, sceneGeneration, points, markerTriangles.ToArray(), normals); return true;
        static double Area(Vector3 a, Vector3 b, float x, float z) =>
            ((double)b.X - a.X) * ((double)z - a.Z) - ((double)b.Z - a.Z) * ((double)x - a.X);
    }

    internal bool TryHeight(int index, Vector3 at, out float height)
    {
        height = 0; var t = triangles[index];
        var area = Area(t.A, t.B, t.C.X, t.C.Z);
        var a = Area(t.B, t.C, at.X, at.Z) / area;
        var c = Area(t.A, t.B, at.X, at.Z) / area;
        var b = 1 - a - c;
        if (a < 0 || b < 0 || c < 0) return false;
        height = (float)(a * t.A.Y + b * t.B.Y + c * t.C.Y); return true;
        static double Area(Vector3 a, Vector3 b, float x, float z) =>
            ((double)b.X - a.X) * ((double)z - a.Z) - ((double)b.Z - a.Z) * ((double)x - a.X);
    }
}

public readonly record struct CalibratedFootCapsule(Vector3 A, Vector3 B, float Radius);
public enum FootPlantState { Lifted, CompressedPlant, SupportConflict }

/// <summary>Approximate collision proxies only. CompressedPlant is NOT solver
/// admission: a sole at its support leaves no positive cloth-thickness gap.
/// MinimumSupportGap is an endpoint/plane diagnostic, not a whole-capsule or
/// cloth-face certificate; no foot/terrain/cloth is moved here.</summary>
public sealed class CalibratedFootPose
{
    private readonly CalibratedFootCapsule[] capsules;
    public RawFootFrame Source { get; }
    public FootPlantCalibration Calibration { get; }
    public FootSupportObservation Support { get; }
    public ReadOnlySpan<CalibratedFootCapsule> Capsules => capsules;
    public FootPlantState State { get; }
    public float MinimumSupportGap { get; }
    internal CalibratedFootPose(RawFootFrame source, FootPlantCalibration calibration,
        FootSupportObservation support, CalibratedFootCapsule[] capsules, FootPlantState state, float gap)
    { Source = source; Calibration = calibration; Support = support; this.capsules = capsules; State = state; MinimumSupportGap = gap; }
}

/// <summary>Small, explicit stationary-plant calibration of a proxy, NOT a
/// measurement of footwear. Caller requests this model only when standing is
/// intended; three stable raw captures are an engineering eligibility check,
/// not proof that an idle animation's shoes touch the support. Calibration is
/// independent of cloth clearance and never adds space for the solver.
/// Frozen marker-local offsets then follow actual marker lift/rotation, with
/// no player-Y cap, ground snapping, adaptation, or accumulation on failure.</summary>
public sealed class FootPlantCalibration
{
    public const int MinimumSamples = 3, MaximumSamples = 8;
    private readonly Vector3[] localAxisOffsets;
    private readonly RawFootFrame calibrationFrame;
    public RawFootIdentity Identity { get; }
    public RawFootFrame ReferenceFrame => calibrationFrame;
    public ReadOnlySpan<Vector3> MarkerLocalAxisOffsets => localAxisOffsets;
    public long LastCalibrationSequence { get; }
    public double CalibratedAt { get; }
    public long CalibrationSceneGeneration { get; }
    public float Radius { get; }
    private FootPlantCalibration(RawFootFrame last, long scene, float radius, Vector3[] offsets)
    { Identity = last.Identity; LastCalibrationSequence = last.Sequence; CalibratedAt = last.SampledAt;
        CalibrationSceneGeneration = scene; Radius = radius; localAxisOffsets = offsets; calibrationFrame = last; }

    public static bool TryCreate(ReadOnlySpan<FootSupportObservation> standingSamples,
        double now, out FootPlantCalibration? calibration)
    {
        calibration = null;
        if (standingSamples.Length is < MinimumSamples or > MaximumSamples) return false;
        var first = standingSamples[0]; var last = standingSamples[^1];
        if (first is null || last is null || !last.Frame.FreshAt(now)) return false;
        var initial = first.Frame; var final = last.Frame;
        var scale = initial.Identity.Scale;
        // A quaternion + scalar radius is not a correct nonuniform transform.
        // Retain raw samples but refuse this deliberately small calibration model.
        if (Math.Abs(scale.X - scale.Y) > 1e-5f || Math.Abs(scale.X - scale.Z) > 1e-5f) return false;
        var radius = Math.Max(.22f * scale.X, .11f);
        if (radius > .34f || final.SampledAt - initial.SampledAt is < .03 or > .25) return false;
        for (var s = 0; s < standingSamples.Length; s++)
        {
            var sample = standingSamples[s];
            if (sample is null || sample.SceneGeneration != first.SceneGeneration || sample.Frame.Identity != initial.Identity) return false;
            var frame = sample.Frame;
            if (s > 0)
            {
                var previous = standingSamples[s - 1].Frame;
                if (previous.Sequence == long.MaxValue || frame.Sequence != previous.Sequence + 1
                    || frame.SampledAt <= previous.ReadCompletedAt || frame.SampledAt - previous.SampledAt > .1) return false;
            }
            if (Vector3.Distance(frame.PlayerPosition, initial.PlayerPosition) > .01f * scale.X) return false;
            for (var i = 0; i < 4; i++)
            {
                var marker = frame.Markers[i]; var original = initial.Markers[i];
                var inset = marker.Position.Y - sample.Points[i].Y;
                if (marker.LocalScale != Vector3.One || inset < 0 || inset > .15f * scale.X
                    || Vector3.Distance(marker.Position, original.Position) > .01f * scale.X
                    || Math.Abs(Quaternion.Dot(marker.Rotation, original.Rotation)) < .999f
                    || Vector3.Dot(sample.Normals[i], first.Normals[i]) < .99999f
                    || Math.Abs(sample.Points[i].Y - first.Points[i].Y) > .002f * scale.X) return false;
            }
            // One boot cannot calibrate across a riser; different LEFT/RIGHT
            // tread heights are allowed and do not share a sole plane.
            if (Math.Abs(sample.Points[0].Y - sample.Points[1].Y) > .003f * scale.X
                || Math.Abs(sample.Points[2].Y - sample.Points[3].Y) > .003f * scale.X
                || Vector3.Dot(sample.Normals[0], sample.Normals[1]) < .99999f
                || Vector3.Dot(sample.Normals[2], sample.Normals[3]) < .99999f) return false;
        }
        var offsets = new Vector3[4];
        for (var i = 0; i < 4; i++)
        {
            var marker = final.Markers[i];
            var axis = last.Points[i] + last.Normals[i] * radius;
            offsets[i] = Vector3.Transform(axis - marker.Position, Quaternion.Conjugate(marker.Rotation));
        }
        calibration = new(final, last.SceneGeneration, radius, offsets); return true;
    }

    public bool TryEvaluate(RawFootFrame frame, FootSupportObservation support,
        double now, out CalibratedFootPose? result)
    {
        result = null;
        if (frame is null || support is null || !ReferenceEquals(frame, support.Frame)
            || frame.Identity != Identity || frame.Sequence < LastCalibrationSequence
            || frame.Sequence == LastCalibrationSequence && !ReferenceEquals(frame, calibrationFrame)
            || frame.SampledAt < CalibratedAt || !frame.FreshAt(now)) return false;
        Span<Vector3> axes = stackalloc Vector3[4];
        var minimumGap = float.PositiveInfinity;
        for (var i = 0; i < 4; i++)
        {
            if (frame.Markers[i].LocalScale != Vector3.One) return false;
            axes[i] = frame.Markers[i].Position + Vector3.Transform(localAxisOffsets[i], frame.Markers[i].Rotation);
            if (!FootModelMath.World(axes[i]) || !support.TryHeight(i, axes[i], out var ground)) return false;
            minimumGap = Math.Min(minimumGap, (axes[i].Y - ground) * support.Normals[i].Y - Radius);
        }
        var capsules = new CalibratedFootCapsule[2];
        for (var i = 0; i < 2; i++)
        {
            if (Vector3.DistanceSquared(axes[2 * i], axes[2 * i + 1]) > .75f * .75f) return false;
            capsules[i] = new(axes[2 * i], axes[2 * i + 1], Radius);
        }
        // This small band only labels numerical/support uncertainty. It NEVER
        // authorizes penetration, changes geometry, or supplies collision margin.
        var state = minimumGap < -.00001f ? FootPlantState.SupportConflict
            : minimumGap <= .00001f ? FootPlantState.CompressedPlant : FootPlantState.Lifted;
        result = new(frame, this, support, capsules, state, minimumGap); return true;
    }
}
