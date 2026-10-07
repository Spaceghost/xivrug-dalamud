using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using XivSurface.Core;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace XivRug.Plugin;

/// <summary>
/// Framework-thread read-only foot observer. Separate from CarpetCharacterPose:
/// walking/standing contacts do not depend on any plugin-owned animation, emote,
/// descent history or idle feature. Publishes value snapshots, never native pointers.
/// </summary>
internal sealed unsafe class CharacterFootContacts(IObjectTable objects, IClientState client,
    ICondition conditions, IPluginLog log)
{
    private nint owner, skeletonAddress;
    private ulong ownerId;
    private uint territory;
    private int boneCount, leftFoot = -1, leftToe = -1, rightFoot = -1, rightToe = -1;
    private long cachedModelGeneration;
    private readonly ClothFootModelTracker modelTracker = new();
    private bool warned, suppressGuard;
    private string? loggedStatus;
    private int loggedCount = -1;
    private long lastDiagnostic;
    public ClothFootContact[] Current { get; private set; } = [];
    public ClothFootContact[] RenderContacts { get; private set; } = [];
    // Physical contact consumes this exact immutable actual-read transaction,
    // never a later current-owner label around cached Current/RenderContacts.
    public ClothFootRenderFrame? BoundFrame { get; private set; }
    // Separate raw model for physical calibration. Legacy render ceilings below
    // remain unchanged and are never relabeled as measured shoe geometry.
    public RawFootFrame? RawFrame { get; private set; }
    private RawFootMarker[] rawMarkers = [];
    private Vector3 rawPlayer;
    private long rawSequence, rawGeneration = 1;
    public string Status { get; private set; } = "Waiting for feet.";

    // Framework thread only. Both sides of a late endpoint capture must still
    // refer to this exact local character and a permitted scene.
    public ClothFootCaptureIdentity CaptureIdentity(bool enabled)
    {
        if (!enabled || !client.IsLoggedIn || client.TerritoryType == 0
            || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.LoggingOut] || conditions[ConditionFlag.OccupiedInCutSceneEvent]) return Unbound();
        var player = objects.LocalPlayer;
        if (player is null || player.Address == 0 || player.GameObjectId == 0) return Unbound();
        var character = (Character*)player.Address;
        if (character->IsMounted()) return Unbound();
        var actor = new ClothFootCaptureIdentity(client.TerritoryType, player.GameObjectId, player.Address);
        if (!character->IsReadyToDraw()) return Unbound(actor);
        var draw = character->GetCharacterBase();
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human) return Unbound(actor);
        var skeleton = draw->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount is 0 or > 64 || skeleton->PartialSkeletons == null)
            return Unbound(actor);
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->Skeleton->Bones.Length is <= 0 or > 1024
            || pose->ModelPose.Length != pose->Skeleton->Bones.Length) return Unbound(actor);
        var observed = modelTracker.Observe(new(actor, (nint)draw, (nint)skeleton, (nint)pose->Skeleton, pose->Skeleton->Bones.Length));
        if (BoundFrame is not null && BoundFrame.CaptureIdentity != observed) BoundFrame = null;
        if (RawFrame is not null && RawFrame.Identity.Actor != observed) InvalidateRaw();
        return observed;
    }

    private ClothFootCaptureIdentity Unbound(ClothFootCaptureIdentity actor = default)
    { modelTracker.Invalidate(); BoundFrame = null; InvalidateRaw(); return actor; }

    public void Update(bool enabled)
    {
        Current = [];
        RenderContacts = [];
        BoundFrame = null;
        RawFrame = null;
        Status = "Waiting for feet.";
        suppressGuard = false;
        Vector3? fallbackPosition = null;
        try
        {
            if (!enabled || !client.IsLoggedIn || conditions[ConditionFlag.BetweenAreas]
                || conditions[ConditionFlag.BetweenAreas51] || conditions[ConditionFlag.LoggingOut]
                || conditions[ConditionFlag.OccupiedInCutSceneEvent])
            { Reset(); return; }
            fallbackPosition = objects.LocalPlayer?.Position;
            var started = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var before = CaptureIdentity(enabled);
            var rawBefore = CaptureRawIdentity(before);
            Read();
            var after = CaptureIdentity(enabled);
            var rawAfter = CaptureRawIdentity(after);
            var completed = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (ClothFootRenderFrame.TryCaptureBound(before, after, started, completed, Current, out var bound))
                BoundFrame = bound;
            if (rawSequence < long.MaxValue && RawFootFrame.TryCapture(rawBefore, rawAfter, rawSequence + 1,
                started, completed, rawPlayer, rawMarkers, out var raw))
            { rawSequence++; RawFrame = raw; }
            else InvalidateRaw();
        }
        catch (Exception error)
        {
            Reset(); Status = "Foot clearance is waiting for a valid character pose.";
            if (!warned) { warned = true; log.Warning(error, "XivRug read-only foot sampling failed; stale contacts discarded"); }
        }
        finally
        {
            // Current remains measured-only for pressure. The rendering guard
            // is rebuilt from today's player position, never a stale pose.
            RenderContacts = ClothFootClearance.WithFallback(Current, suppressGuard ? null : fallbackPosition);
            if (!suppressGuard && fallbackPosition is not null && Current.Count(contact => contact.Valid) < ClothFootClearance.MaximumContacts)
                Status += " Incomplete tracking; rug rendering remains suppressed.";
            ReportState();
        }
    }

    private void ReportState()
    {
        // Only state/count changes are interesting; walking changes world Y
        // every frame. Coalesce transient pose readiness changes to at most
        // one diagnostic per two seconds, retaining the latest pending state.
        if (loggedStatus == Status && loggedCount == Current.Length) return;
        var now = Stopwatch.GetTimestamp();
        if (loggedStatus is not null && Stopwatch.GetElapsedTime(lastDiagnostic, now).TotalSeconds < 2) return;
        loggedStatus = Status; loggedCount = Current.Length; lastDiagnostic = now;
        if (Current.Length == ClothFootClearance.MaximumContacts)
            log.Information("XivRug foot clearance: {Count} contacts; {State}; sole world Y = [{LeftHeel:F3}, {LeftToe:F3}, {RightHeel:F3}, {RightToe:F3}]",
                Current.Length, Status, Current[0].FootY, Current[1].FootY, Current[2].FootY, Current[3].FootY);
        else
            log.Information("XivRug foot clearance: {Count} contacts; {State}", Current.Length, Status);
    }

    private void Reset(bool invalidateCapture = true)
    {
        Current = []; owner = skeletonAddress = 0; ownerId = 0; territory = 0; boneCount = 0;
        cachedModelGeneration = 0; BoundFrame = null;
        if (invalidateCapture) modelTracker.Invalidate();
        if (invalidateCapture) InvalidateRaw();
        leftFoot = leftToe = rightFoot = rightToe = -1; Status = "Waiting for feet.";
    }

    private void Read()
    {
        rawMarkers = [];
        var player = objects.LocalPlayer;
        if (player is null) { Reset(); return; }
        if (owner != player.Address || ownerId != player.GameObjectId || territory != client.TerritoryType)
        {
            Reset(false); owner = player.Address; ownerId = player.GameObjectId; territory = client.TerritoryType;
        }
        var character = (Character*)player.Address;
        if (character == null)
        { Status = "Waiting for the character draw model."; return; }
        // Mount/model rebuilds may temporarily make the draw model unready.
        // Check rider suppression first so that state cannot enable a fallback.
        if (character->IsMounted()) { suppressGuard = true; Status = "Rug hidden while mounted: no verified rider-foot clearance."; return; }
        if (!character->IsReadyToDraw())
        { Status = "Waiting for the character draw model."; return; }
        var draw = character->GetCharacterBase();
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human)
        { Status = "Waiting for a humanoid draw model."; return; }
        var skeleton = draw->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount is 0 or > 64 || skeleton->PartialSkeletons == null)
        { Status = "Waiting for a valid character skeleton."; return; }
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) { Status = "Waiting for a character pose."; return; }
        if (pose->ModelInSync == 0) { Status = "Waiting for synchronized model-space foot bones."; return; }
        var bones = pose->Skeleton->Bones;
        if (bones.Length is <= 0 or > 1024 || pose->ModelPose.Length != bones.Length)
        { Status = "Waiting for a complete model-space skeleton."; return; }
        if (skeletonAddress != (nint)pose->Skeleton || boneCount != bones.Length
            || cachedModelGeneration != modelTracker.Current.ModelGeneration)
        {
            skeletonAddress = (nint)pose->Skeleton; boneCount = bones.Length;
            cachedModelGeneration = modelTracker.Current.ModelGeneration;
            leftFoot = leftToe = rightFoot = rightToe = -1;
            for (var i = 0; i < bones.Length; i++)
            {
                // Canonical humanoid foot/toe names, also used by Brio's
                // AnamnesisBoneNameConverter (Foot/Toes left and right).
                switch (bones[i].Name.String)
                {
                    case "j_asi_d_l": leftFoot = i; break;
                    case "j_asi_e_l": leftToe = i; break;
                    case "j_asi_d_r": rightFoot = i; break;
                    case "j_asi_e_r": rightToe = i; break;
                }
            }
        }
        var scale = (Vector3)skeleton->Transform.Scale;
        var rotation = (Quaternion)skeleton->Transform.Rotation;
        var origin = (Vector3)skeleton->Transform.Position;
        var rotationLength = rotation.LengthSquared();
        if (!Finite(scale) || !Finite(origin) || !float.IsFinite(rotationLength) || rotationLength is < .25f or > 4)
        { Status = "Waiting for a valid skeleton world transform."; return; }
        rotation = Quaternion.Normalize(rotation);
        Span<ClothFootContact> contacts = stackalloc ClothFootContact[ClothFootClearance.MaximumContacts];
        var count = 0;
        rawMarkers = new RawFootMarker[4]; rawPlayer = player.Position;
        ReadBoot(leftFoot, leftToe, 0, contacts, ref count); ReadBoot(rightFoot, rightToe, 2, contacts, ref count);
        Current = contacts[..count].ToArray();
        Status = count == 4 ? "Both feet tracked · conservative read-only boot clearance"
            : count == 2 ? "Rug hidden: waiting for complete tracking of both feet."
            : "Rug hidden: waiting for valid foot/toe joints and supported model scale.";
        if (count > 0) warned = false;

        void ReadBoot(int footIndex, int toeIndex, int rawSlot, Span<ClothFootContact> into, ref int written)
        {
            if (footIndex < 0 || toeIndex < 0 || footIndex >= bones.Length || toeIndex >= bones.Length) return;
            var a = pose->ModelPose[footIndex].Translation; var b = pose->ModelPose[toeIndex].Translation;
            var foot = Vector3.Transform(new Vector3(a.X, a.Y, a.Z) * scale, rotation) + origin;
            var toe = Vector3.Transform(new Vector3(b.X, b.Y, b.Z) * scale, rotation) + origin;
            var qa = pose->ModelPose[footIndex].Rotation; var qb = pose->ModelPose[toeIndex].Rotation;
            var sa = pose->ModelPose[footIndex].Scale; var sb = pose->ModelPose[toeIndex].Scale;
            rawMarkers[rawSlot] = new(foot, Quaternion.Concatenate(new(qa.X, qa.Y, qa.Z, qa.W), rotation))
                { LocalScale = new(sa.X, sa.Y, sa.Z) };
            rawMarkers[rawSlot + 1] = new(toe, Quaternion.Concatenate(new(qb.X, qb.Y, qb.Z, qb.W), rotation))
                { LocalScale = new(sb.X, sb.Y, sb.Z) };
            if (!ClothFootClearance.TryBoot(foot, toe, player.Position, scale, out var heel, out var tip)) return;
            into[written++] = heel; into[written++] = tip;
        }
    }

    private RawFootIdentity CaptureRawIdentity(ClothFootCaptureIdentity actor)
    {
        if (!actor.Bound || rawGeneration <= 0) return default;
        var character = (Character*)actor.Address;
        var draw = character->GetCharacterBase();
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human
            || draw->Skeleton == null || draw->Models == null || draw->SlotCount is < 5 or > 64) return default;
        // Human equipment slot4 is feet. Loaded resource identity participates
        // even when a gear change leaves the skeleton addresses unchanged.
        var model = draw->Models[4];
        if (model == null || model->ModelResourceHandle == null || model->ModelResourceHandle->LoadState != 7) return default;
        return new(actor, (nint)model, (nint)model->ModelResourceHandle,
            (Vector3)draw->Skeleton->Transform.Scale, rawGeneration,
            model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask);
    }

    private void InvalidateRaw()
    {
        RawFrame = null; rawMarkers = [];
        rawGeneration = rawGeneration is > 0 and < long.MaxValue ? rawGeneration + 1 : 0;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
