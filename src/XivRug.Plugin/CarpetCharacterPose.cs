using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Lumina.Excel.Sheets;
using XivSurface.Core;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace XivRug.Plugin;

/// <summary>
/// Framework-thread-only, local visual poses. Owns BaseOverride temporarily; never changes character mode,
/// gameplay position/rotation, movement speed, input, action state, or the networked emote controller.
/// Short excursions lease only DrawOffset and the separate render-object rotation.
/// </summary>
internal sealed unsafe class CarpetCharacterPose(
    IDataManager data, IObjectTable objects, ICondition conditions, IClientState client, IPluginLog log) : IDisposable
{
    // 884 is the authored low "sit_yankee" resting squat. 882 is a generic
    // squat movement and 5803 is the repeating Squats exercise emote.
    private const ushort Idle = 3, Walk = 13, Stomp = 8179, Squat = 884, SquatExercise = 5803;
    private static readonly ConditionFlag[] Interruptions =
    [
        ConditionFlag.InCombat, ConditionFlag.Casting, ConditionFlag.Casting87, ConditionFlag.Emoting,
        ConditionFlag.Jumping, ConditionFlag.Jumping61, ConditionFlag.Mounted, ConditionFlag.RidingPillion,
        ConditionFlag.Mounting, ConditionFlag.Mounting71, ConditionFlag.MountOrOrnamentTransition,
        ConditionFlag.InFlight, ConditionFlag.Swimming, ConditionFlag.Diving, ConditionFlag.BeingMoved,
        ConditionFlag.Occupied, ConditionFlag.Occupied30, ConditionFlag.Occupied33, ConditionFlag.Occupied38,
        ConditionFlag.Occupied39, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.OccupiedSummoningBell,
        ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51, ConditionFlag.LoggingOut, ConditionFlag.Unconscious,
        ConditionFlag.Crafting, ConditionFlag.Gathering, ConditionFlag.Fishing, ConditionFlag.Performing,
        ConditionFlag.TradeOpen, ConditionFlag.CarryingObject, ConditionFlag.CarryingItem,
        ConditionFlag.Transformed, ConditionFlag.RolePlaying, ConditionFlag.UsingFashionAccessory,
        ConditionFlag.UsingHousingFunctions, ConditionFlag.EditingPortrait, ConditionFlag.EditingStrategyBoard,
    ];
    private readonly CarpetIdleBehavior behavior = new();
    private readonly CarpetVisualOffsetLease visualOffset = new();
    private CarpetFootworkPlan? footworkPlan;
    private CarpetFootworkSample footworkSample;
    private bool footworkPlanned, walkAvailable;
    private float ownedBaseSpeed;
    private readonly List<ClothPressure> strokes = [];
    private nint ownerAddress, drawAddress;
    private ulong ownerId;
    private uint territory;
    private ushort ownedTimeline, savedOverride;
    private uint ownedSlot;
    private double acquiredAt, retryAfter;
    private bool resolved, idleAvailable, stompAvailable, squatAvailable, disposed;
    private double stompDuration = 5.6;
    private nint footSkeleton;
    private int leftToe = -1, rightToe = -1;
    private float previousLeft = float.NaN, previousRight = float.NaN;
    private string diagnosticReason = "initial", diagnosticKey = "", characterRoot = "unknown";
    private double nextDiagnosticAt, nextHeartbeat;
    private int idleResets;
    private ushort restTimeline;
    private bool ownsBaseSpeed, squatHeld, squatHoldFailed;
    private float savedBaseSpeed = 1, squatHighHip, squatLowHip, lastHip = float.NaN;
    private double lowestHipAt, lastHipAt;
    private nint pelvisSkeleton;
    private int pelvisBone = -1;
    private bool pendingRelease;

    public string Status { get; private set; } = "Character rug actions are off.";
    public CarpetIdleState State => behavior.State;
    public double IdleSeconds => behavior.IdleSeconds;
    public ClothPressure[] FootPressure { get; private set; } = [];
    public ClothPressure[] SmoothedPatches { get; private set; } = [];

    public void Update(bool idleActions, bool rideVisual, double nowSeconds, bool squatAfterFootwork = false, ClothMesh? cloth = null)
    {
        if (disposed) return;
        var previousIdle = IdleSeconds;
        diagnosticReason = "ready";
        try { UpdateCore(idleActions, rideVisual, nowSeconds, squatAfterFootwork, cloth); }
        catch (Exception error)
        {
            Release(false); ResetBehavior(); retryAfter = nowSeconds + 30;
            diagnosticReason = "animation-error";
            Status = "Character rug actions paused after an animation error.";
            log.Warning(error, "XivRug released its local character pose after an error");
        }
        if (previousIdle > 1 && IdleSeconds < .1) idleResets++;
        WriteDiagnostic(nowSeconds, idleActions, rideVisual);
    }

    private void UpdateCore(bool idleActions, bool rideVisual, double now, bool squatAfterFootwork, ClothMesh? cloth)
    {
        FootPressure = [];
        var player = objects.LocalPlayer;
        var character = player is null ? null : (Character*)player.Address;
        if (player is null || character == null || !client.IsLoggedIn)
        {
            // Restore if the same object is still present in the object table.
            // Otherwise retain the lease without touching the old pointer, so a
            // temporarily missing LocalPlayer can be thawed when it returns.
            Release(false);
            pendingRelease = ownedTimeline != 0 || visualOffset.Held;
            if (!pendingRelease) ForgetOwner();
            ResetBehavior(); diagnosticReason = "no-player"; Status = "Waiting for your character."; return;
        }
        if (pendingRelease)
        {
            Release(false); ReleaseVisualOffset(); ForgetOwner(); pendingRelease = false; ResetBehavior();
        }
        if (ownerAddress != 0 && (ownerAddress != player.Address || ownerId != player.GameObjectId || territory != client.TerritoryType))
        {
            Release(false); ReleaseVisualOffset(); ForgetOwner(); ResetBehavior();
        }
        if (ownerAddress == 0)
        {
            ownerAddress = player.Address; ownerId = player.GameObjectId; territory = client.TerritoryType;
        }
        var blockReason = BlockReason(character);
        var safe = blockReason is null;
        if ((!idleActions && !rideVisual) || !safe || now < retryAfter)
        {
            Release(safe); ResetBehavior();
            diagnosticReason = !idleActions && !rideVisual ? "disabled-or-no-cloth" : blockReason ?? "retry-delay";
            Status = !idleActions && !rideVisual ? "Character rug actions are off." : now < retryAfter
                ? "Waiting for the interrupted character animation to finish." : "Character rug actions yield to the game.";
            return;
        }
        if (ownedTimeline != 0 && character->Timeline.BaseOverride != ownedTimeline)
        {
            // Another mod or the game took ownership. Do not restore over its value or fight it.
            diagnosticReason = $"override-replaced:{ownedTimeline}->{character->Timeline.BaseOverride}";
            Release(false); ResetBehavior(); retryAfter = now + 15;
            Status = "Another character animation has priority."; return;
        }
        if (ownedTimeline == 0 && character->Timeline.BaseOverride != 0)
        {
            ResetBehavior(); diagnosticReason = "foreign-override"; Status = "Another character pose has priority."; return;
        }
        if (ownedTimeline == 0 && character->Timeline.TimelineSequencer.GetSlotSpeed(0) <= 0)
        {
            ResetBehavior(); diagnosticReason = "foreign-paused-animation"; Status = "Another character animation pause has priority."; return;
        }
        var draw = character->GetCharacterBase();
        if (draw == null || !character->IsReadyToDraw() || draw->GetModelType() != CharacterBase.ModelType.Human)
        {
            Release(false); ResetBehavior(); diagnosticReason = "model-not-ready"; Status = "Waiting for a normal character model."; return;
        }
        if ((nint)draw != drawAddress)
        {
            Release(true); ReleaseVisualOffset(); drawAddress = (nint)draw; resolved = false; footSkeleton = pelvisSkeleton = 0; pelvisBone = -1; ResetBehavior();
        }
        if (!resolved) ResolveClips(draw);
        var state = behavior.Update(idleActions, rideVisual, true, player.Position, player.Rotation, now, footworkPlan?.Duration ?? stompDuration);
        if (state == CarpetIdleState.Footwork && !footworkPlanned)
        {
            footworkPlanned = true;
            if (walkAvailable && stompAvailable)
                footworkPlan = CarpetFootworkPlan.Create(cloth, player.Position, player.Rotation, stompDuration);
            if (footworkPlan is { } planned)
                log.Information("XivRug visual footwork planned {Targets} measured folds, {Seconds:F2}s; gameplay position is unchanged", planned.TargetCount, planned.Duration);
        }
        if (state == CarpetIdleState.Footwork && footworkPlan is { } plan)
        {
            footworkSample = plan.Sample(behavior.IdleSeconds - CarpetIdleBehavior.IdleDelay);
            if (!plan.StillSupported(cloth, footworkSample.Offset) || !WriteVisualOffset(character, draw, footworkSample))
            {
                Release(true); ResetBehavior(); retryAfter = now + 15;
                diagnosticReason = "visual-path-or-offset-changed";
                Status = "The rug changed; returning your visual pose to the game."; return;
            }
        }
        else ReleaseVisualOffset();
        if (state is CarpetIdleState.Waiting or CarpetIdleState.Riding)
        {
            strokes.Clear(); SmoothedPatches = []; previousLeft = previousRight = float.NaN;
            squatHoldFailed = false; footworkPlanned = false; footworkPlan = null;
        }
        var desired = state switch
        {
            CarpetIdleState.Footwork when footworkPlan is not null && footworkSample.Phase == CarpetFootworkPhase.Walking => Walk,
            CarpetIdleState.Footwork when stompAvailable => Stomp,
            CarpetIdleState.Resting when squatAfterFootwork && squatAvailable && !squatHoldFailed => restTimeline,
            CarpetIdleState.Riding when idleAvailable => Idle,
            _ => (ushort)0,
        };
        // Sheet ActionTimeline.Slot is not a native sequencer index: slot 2 in
        // the sequencer is facial. BaseOverride plays in native base slot 0.
        // Evaluate the next phase first so the natural end of the stomp cannot
        // reset the idle timer instead of advancing into the resting squat.
        if (ownedTimeline != 0 && desired == ownedTimeline && now - acquiredAt > .6 &&
            character->Timeline.TimelineSequencer.GetSlotTimeline(ownedSlot) != ownedTimeline &&
            !(ownedTimeline == Stomp && now - acquiredAt >= stompDuration - .15))
        {
            diagnosticReason = $"base-interrupted:{ownedTimeline}->{character->Timeline.TimelineSequencer.GetSlotTimeline(ownedSlot)}";
            Release(false); ResetBehavior(); retryAfter = now + 15;
            Status = "Character animation interrupted; waiting before retrying."; return;
        }
        if (desired == 0) Release(true);
        else if (desired != ownedTimeline) Apply(character, desired, now);
        if (state == CarpetIdleState.Footwork && ownedTimeline is Stomp or Walk &&
            character->Timeline.TimelineSequencer.GetSlotTimeline(ownedSlot) == ownedTimeline && now - acquiredAt > .12)
            ReadFootPressure(draw, player.Position + (footworkPlan is null ? Vector3.Zero : footworkSample.Offset));
        if (state == CarpetIdleState.Resting && ownedTimeline == SquatExercise && !UpdateSquatHold(character, draw, now)) return;
        Status = state switch
        {
            CarpetIdleState.Footwork when footworkPlan is not null => footworkSample.Phase == CarpetFootworkPhase.Walking
                ? "Walking to a rug fold · visual movement only" : "Smoothing this fold with your feet · local animation",
            CarpetIdleState.Footwork => stompAvailable ? "Straightening with your feet · local animation" : "This model has no verified stomp clip.",
            CarpetIdleState.Resting when squatAfterFootwork && squatHoldFailed => "Resting · the deep squat could not be measured safely.",
            CarpetIdleState.Resting when squatAfterFootwork => !squatAvailable ? "Resting · a compatible deep squat clip is unavailable."
                : restTimeline == SquatExercise && !squatHeld ? "Settling into a deep squat · local animation"
                : "Resting in a deep squat · local animation",
            CarpetIdleState.Resting => "Resting on your rug.",
            CarpetIdleState.Riding => idleAvailable ? "Riding pose · ordinary game movement and jumping remain active" : "A compatible standing pose is unavailable.",
            _ => $"Idle {behavior.IdleSeconds:F0}/15 seconds · ready to straighten the rug",
        };
    }

    private bool IsSafe(Character* character) => BlockReason(character) is null;

    private string? BlockReason(Character* character)
    {
        if (!client.IsLoggedIn) return "logged-out";
        if (client.IsGPosing) return "gpose";
        if (character->Mode != CharacterModes.Normal) return $"mode:{character->Mode}";
        if (character->MoveController.MovementState != MovementStateOptions.Normal) return "not-grounded-movement";
        if (character->IsCasting) return "casting";
        if (character->InCombat) return "combat";
        if (character->IsWeaponDrawn) return "weapon-drawn";
        if (character->IsJumping()) return "jumping";
        if (character->IsMounted()) return "mounted";
        if (character->MoveController.IsSwimming) return "swimming";
        if (character->IsDead()) return "dead";
        foreach (var flag in Interruptions) if (conditions[flag]) return $"condition:{flag}";
        return null;
    }

    private void ResolveClips(CharacterBase* draw)
    {
        resolved = true;
        characterRoot = draw->ResolveRootPath().TrimEnd('/');
        idleAvailable = VerifyClip(draw, Idle, "normal/idle", "resident/idle", "cbnm_id0");
        walkAvailable = VerifyClip(draw, Walk, "normal/walk", "resident/move_a", "cbnm_01f_lp0");
        stompAvailable = VerifyClip(draw, Stomp, "emote/act_emot44", "emote/act_emot44", "cbem_act_emot44");
        squatAvailable = VerifyClip(draw, Squat, "event_base/event_base_sit_yankee", "event_base/event_base_sit_yankee", "cbfm_base_sit_yankee");
        restTimeline = squatAvailable ? Squat : (ushort)0;
        if (!squatAvailable && VerifyClip(draw, SquatExercise, "emote/loop_emot10_loop", "emote/loop_emot10_loop", "cbem_loop_emot10_2lp"))
        {
            squatAvailable = true; restTimeline = SquatExercise;
        }
        if (data.GetFile("chara/action/emote/act_emot44.tmb") is { Data: { Length: >= 28 } bytes } &&
            bytes.AsSpan(12, 4).SequenceEqual("TMDH"u8))
            stompDuration = Math.Clamp(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(24, 2)) / 30d, 1, 12);
        log.Information("XivRug character clips: root={Root}, idle={Idle}, walk={Walk}, stomp={Stomp}, squat={Squat}, restTimeline={Rest}, footwork={Seconds}s", characterRoot, idleAvailable, walkAvailable, stompAvailable, squatAvailable, restTimeline, stompDuration);
    }

    private bool VerifyClip(CharacterBase* draw, ushort timeline, string key, string pack, string clip)
    {
        var row = data.GetExcelSheet<ActionTimeline>().GetRowOrDefault(timeline);
        if (row is null || row.Value.Slot >= 14 || row.Value.Key.ExtractText() != key || data.GetFile("chara/action/" + key + ".tmb") == null)
            return false;
        var path = $"{characterRoot}/animation/a{Math.Max(1, (int)draw->AnimationVariant):D4}/bt_common/{pack}.pap";
        if (ContainsClip(path, clip))
        {
            log.Information("XivRug clip {Timeline}: {Clip} verified at {Path}; sheet slot metadata={SheetSlot}, native base slot=0", timeline, clip, path, row.Value.Slot);
            return true;
        }
        // Human.ResolvePapPath is a dummy/facial resource helper, not body
        // animation inheritance (verified against the installed native code).
        // Body poses are accepted only from this actor's authored PAP here.
        log.Information("XivRug clip {Timeline}: no authored body clip at {Path}; cross-race packs are not forced", timeline, path);
        return false;
    }

    private bool ContainsClip(string path, string clip)
    {
        var bytes = data.GetFile(path)?.Data;
        if (bytes is null || bytes.Length < 26 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20706170) return false;
        var count = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(8));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(14));
        if (count < 0 || count > 4096 || offset < 26 || (long)offset + count * 40 > bytes.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var name = bytes.AsSpan(offset + i * 40, 32); var end = name.IndexOf((byte)0);
            if (Encoding.ASCII.GetString(end < 0 ? name : name[..end]) == clip) return true;
        }
        return false;
    }

    private void Apply(Character* character, ushort timeline, double now)
    {
        Release(true);
        savedOverride = character->Timeline.BaseOverride;
        ownedSlot = 0;
        ownedTimeline = timeline; acquiredAt = now;
        savedBaseSpeed = character->Timeline.TimelineSequencer.GetSlotSpeed(0);
        if (!float.IsFinite(savedBaseSpeed) || savedBaseSpeed <= 0) savedBaseSpeed = 1;
        squatHeld = false; lastHip = float.NaN; lastHipAt = double.NegativeInfinity;
        squatHighHip = float.NegativeInfinity; squatLowHip = float.PositiveInfinity; lowestHipAt = now;
        var draw = character->GetCharacterBase();
        if (timeline == SquatExercise && draw != null && TryPelvisHeight(draw, out var standingHip))
            squatHighHip = squatLowHip = standingHip;
        character->Timeline.BaseOverride = timeline;
        character->Timeline.TimelineSequencer.PlayTimeline(timeline);
        if (timeline == Walk)
        {
            ownedBaseSpeed = Math.Clamp(CarpetFootworkPlan.WalkingSpeed / 1.8f, .25f, 1);
            character->Timeline.TimelineSequencer.SetSlotSpeed(0, ownedBaseSpeed);
            ownsBaseSpeed = true;
        }
    }

    private void WriteDiagnostic(double now, bool idleActions, bool rideVisual)
    {
        if (!double.IsFinite(now) || now < nextDiagnosticAt) return;
        var player = objects.LocalPlayer;
        var character = player is not null && player.Address == ownerAddress && player.GameObjectId == ownerId
            ? (Character*)player.Address : null;
        var actualOverride = character == null ? 0 : character->Timeline.BaseOverride;
        var baseId = character == null ? 0 : character->Timeline.TimelineSequencer.GetSlotTimeline(0);
        var upperId = character == null ? 0 : character->Timeline.TimelineSequencer.GetSlotTimeline(1);
        var facialId = character == null ? 0 : character->Timeline.TimelineSequencer.GetSlotTimeline(2);
        var speed = character == null ? 1 : character->Timeline.TimelineSequencer.GetSlotSpeed(0);
        var key = $"{diagnosticReason}/{State}/{ownedTimeline}/{actualOverride}/{baseId}/{idleActions}/{rideVisual}/{squatHeld}";
        if (key == diagnosticKey && (!(idleActions || rideVisual) || now < nextHeartbeat)) return;
        diagnosticKey = key; nextDiagnosticAt = now + 1; nextHeartbeat = now + 20;
        log.Information("XivRug pose: reason={Reason}, state={State}, idle={Idle:F2}s, resets={Resets}, wanted={Owned}, override={Override}, slots(base/upper/face)={Base}/{Upper}/{Face}, clips={Clips}, feet={Feet}, patches={Patches}, root={Root}, baseSpeed={Speed}, squatHeld={Held}, hipHigh/low={High}/{Low}",
            diagnosticReason, State, IdleSeconds, idleResets, ownedTimeline, actualOverride, baseId, upperId, facialId,
            $"{idleAvailable}/{stompAvailable}/{squatAvailable}", FootPressure.Length, SmoothedPatches.Length, characterRoot, speed, squatHeld, squatHighHip, squatLowHip);
    }

    private void Release(bool resumeStanding)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? player = objects.LocalPlayer;
        if (ownedTimeline != 0 && (player is null || player.Address != ownerAddress || player.GameObjectId != ownerId))
            player = objects.SearchById(ownerId);
        if (ownedTimeline != 0 && (player is null || player.Address != ownerAddress || player.GameObjectId != ownerId))
            return; // keep an unresolved lease until the object returns or is replaced
        if (ownedTimeline != 0 && player is not null)
        {
            var character = (Character*)player.Address;
            // The game can replace the timeline before this framework callback.
            // Restore our zero speed even then, so a walking/jumping animation
            // never inherits the resting pause. Preserve any nonzero replacement.
            if (ownsBaseSpeed && character->Timeline.TimelineSequencer.GetSlotSpeed(0) == ownedBaseSpeed)
            {
                character->Timeline.TimelineSequencer.SetSlotSpeed(0, savedBaseSpeed);
                log.Information("XivRug restored owned base animation speed to {Speed}; current base timeline={Timeline}", savedBaseSpeed, character->Timeline.TimelineSequencer.GetSlotTimeline(0));
            }
            if (character->Timeline.BaseOverride == ownedTimeline)
            {
                character->Timeline.BaseOverride = savedOverride;
                // Only resume a standing pose when the game is still idle. Never replay an old attack/emote,
                // or inject idle during a jump, cast, cutscene, or another user's animation.
                if (resumeStanding && IsSafe(character) && idleAvailable &&
                    character->Timeline.TimelineSequencer.GetSlotTimeline(ownedSlot) == ownedTimeline)
                    character->Timeline.TimelineSequencer.PlayTimeline(savedOverride != 0 ? savedOverride : Idle);
            }
        }
        ForgetOverride();
    }

    private void ForgetOverride() { ownedTimeline = savedOverride = 0; ownsBaseSpeed = squatHeld = false; }
    private void ForgetOwner()
    {
        visualOffset.Reset(); footworkPlan = null; footworkPlanned = false;
        ForgetOverride(); ownerAddress = drawAddress = footSkeleton = pelvisSkeleton = 0; pelvisBone = -1; ownerId = 0; resolved = false; pendingRelease = false;
    }
    private void ResetBehavior()
    {
        ReleaseVisualOffset(); footworkPlan = null; footworkPlanned = false;
        behavior.Reset(); strokes.Clear(); FootPressure = []; SmoothedPatches = [];
        previousLeft = previousRight = float.NaN; squatHoldFailed = false;
    }

    private bool WriteVisualOffset(Character* character, CharacterBase* draw, CarpetFootworkSample sample)
    {
        if (!visualOffset.TryWrite((Vector3)character->DrawOffset, (Quaternion)draw->Rotation,
            sample.Offset, sample.Yaw, out var offset, out var rotation)) return false;
        // Matched native SetDrawOffset writes 0xE0..E8, then updates only the
        // draw object's position from gameplay Position + rotated local offset.
        character->SetDrawOffset(offset.X, offset.Y, offset.Z);
        draw->Rotation = rotation; draw->IsTransformChanged = true;
        return true;
    }

    private void ReleaseVisualOffset()
    {
        if (!visualOffset.Held) return;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? player = objects.LocalPlayer;
        if (player is null || player.Address != ownerAddress || player.GameObjectId != ownerId)
            player = objects.SearchById(ownerId);
        if (player is null || player.Address != ownerAddress || player.GameObjectId != ownerId) return;
        var character = (Character*)player.Address; var draw = character->GetCharacterBase();
        // DrawOffset belongs to Character and survives a model replacement.
        // Restore it even if CharacterBase changed; never apply the old model's
        // rotation to a replacement. The native setter safely skips null draws.
        var sameDraw = draw != null && (nint)draw == drawAddress;
        var currentRotation = sameDraw ? (Quaternion)draw->Rotation : new Quaternion(float.NaN,0,0,0);
        var restore = visualOffset.Release((Vector3)character->DrawOffset, currentRotation, out var offset, out var rotation);
        if (restore.Offset) character->SetDrawOffset(offset.X, offset.Y, offset.Z);
        if (restore.Rotation && sameDraw) { draw->Rotation = rotation; draw->IsTransformChanged = true; }
    }

    private bool UpdateSquatHold(Character* character, CharacterBase* draw, double now)
    {
        if (squatHeld)
        {
            if (character->Timeline.TimelineSequencer.GetSlotTimeline(0) == SquatExercise &&
                character->Timeline.TimelineSequencer.GetSlotSpeed(0) == 0) return true;
            // Do not fight a later animation-speed edit by the game or another plugin.
            diagnosticReason = "rest-animation-or-speed-replaced"; Release(false); ResetBehavior(); retryAfter = now + 15;
            Status = "Another character animation has priority."; return false;
        }
        if (character->Timeline.TimelineSequencer.GetSlotTimeline(0) == SquatExercise && TryPelvisHeight(draw, out var hip))
        {
            squatHighHip = Math.Max(squatHighHip, hip);
            if (hip < squatLowHip - .0005f) { squatLowHip = hip; lowestHipAt = now; }
            var drop = squatHighHip - squatLowHip;
            var sufficientlyLow = drop >= Math.Max(.035f, MathF.Abs(squatHighHip) * .16f);
            var freshSample = now - lastHipAt <= .15;
            var closeToMinimum = hip - squatLowHip <= Math.Max(.006f, drop * .06f);
            var justRising = freshSample && closeToMinimum && float.IsFinite(lastHip) && hip > lastHip + .0002f && hip - squatLowHip > .002f;
            var bottomPause = freshSample && now - lowestHipAt > .18 && hip - squatLowHip < .004f;
            lastHip = hip; lastHipAt = now;
            if (now - acquiredAt > .3 && sufficientlyLow && (justRising || bottomPause))
            {
                // Capture the value actually replaced, not a speed from the
                // start of the descent several frames earlier.
                var currentSpeed = character->Timeline.TimelineSequencer.GetSlotSpeed(0);
                if (!float.IsFinite(currentSpeed) || currentSpeed <= 0)
                {
                    diagnosticReason = "foreign-squat-pause"; Release(false); ResetBehavior(); retryAfter = now + 15;
                    Status = "Another character animation pause has priority."; return false;
                }
                savedBaseSpeed = currentSpeed;
                character->Timeline.TimelineSequencer.SetSlotSpeed(0, 0);
                ownedBaseSpeed = 0;
                ownsBaseSpeed = squatHeld = true;
                log.Information("XivRug held native squat {Timeline} at measured pelvis minimum: hip={Hip}, high={High}, low={Low}, drop={Drop}, elapsed={Seconds}s", SquatExercise, hip, squatHighHip, squatLowHip, drop, now - acquiredAt);
                return true;
            }
        }
        if (now - acquiredAt <= 8) return true;
        diagnosticReason = "squat-minimum-unavailable"; Release(true); squatHoldFailed = true;
        Status = "Resting · the deep squat could not be measured safely.";
        return false;
    }

    private bool TryPelvisHeight(CharacterBase* draw, out float height)
    {
        height = 0;
        var skeleton = draw->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletonCount > 64 || skeleton->PartialSkeletons == null) return false;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->ModelInSync == 0) return false;
        var bones = pose->Skeleton->Bones;
        if (bones.Length <= 0 || bones.Length > 1024 || pose->ModelPose.Length != bones.Length) return false;
        if (pelvisSkeleton != (nint)pose->Skeleton)
        {
            pelvisSkeleton = (nint)pose->Skeleton; pelvisBone = -1;
            for (var i = 0; i < bones.Length; i++) if (bones[i].Name.String == "j_kosi") { pelvisBone = i; break; }
        }
        if (pelvisBone < 0 || pelvisBone >= pose->ModelPose.Length) return false;
        var p = pose->ModelPose[pelvisBone].Translation;
        var position = Vector3.Transform(new Vector3(p.X, p.Y, p.Z) * (Vector3)skeleton->Transform.Scale, (Quaternion)skeleton->Transform.Rotation);
        height = position.Y;
        return float.IsFinite(height) && height > -.2f && height < 5;
    }

    private void ReadFootPressure(CharacterBase* draw, Vector3 player)
    {
        var skeleton = draw->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletonCount > 64 || skeleton->PartialSkeletons == null) return;
        var pose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (pose == null || pose->Skeleton == null || pose->ModelInSync == 0) return;
        var bones = pose->Skeleton->Bones;
        if (bones.Length <= 0 || bones.Length > 1024 || pose->ModelPose.Length != bones.Length) return;
        if (footSkeleton != (nint)pose->Skeleton)
        {
            footSkeleton = (nint)pose->Skeleton; leftToe = rightToe = -1;
            for (var i = 0; i < bones.Length; i++)
            {
                var name = bones[i].Name.String;
                if (name == "j_asi_e_l") leftToe = i;
                else if (name == "j_asi_e_r") rightToe = i;
            }
        }
        var scale = (Vector3)skeleton->Transform.Scale;
        var rotation = (Quaternion)skeleton->Transform.Rotation;
        var origin = (Vector3)skeleton->Transform.Position;
        if (!Finite(scale) || !Finite(origin) || !float.IsFinite(rotation.LengthSquared())) return;
        var contacts = new List<ClothPressure>(2);
        Sample(leftToe, ref previousLeft); Sample(rightToe, ref previousRight);
        FootPressure = contacts.ToArray();
        if (contacts.Count > 0) SmoothedPatches = strokes.ToArray();

        void Sample(int index, ref float previousHeight)
        {
            if (index < 0 || index >= pose->ModelPose.Length) return;
            var p = pose->ModelPose[index].Translation;
            var world = Vector3.Transform(new Vector3(p.X, p.Y, p.Z) * scale, rotation) + origin;
            if (!Finite(world) || Vector2.DistanceSquared(new(world.X, world.Z), new(player.X, player.Z)) > 4) return;
            var height = world.Y - player.Y;
            // Read the actual animated toe contact, rejecting raised feet and stale/malformed poses.
            var descendingOrPlanted = float.IsFinite(previousHeight) && height <= previousHeight + .008f;
            previousHeight = height;
            if (!descendingOrPlanted || height < -.08f || height > .16f) return;
            var pressure = new ClothPressure(new(world.X, world.Z), .24f, .85f);
            contacts.Add(pressure);
            if (strokes.All(old => Vector2.DistanceSquared(old.Center, pressure.Center) > .0036f))
            {
                if (strokes.Count == 64) strokes.RemoveAt(0);
                strokes.Add(pressure);
            }
        }
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public void Dispose()
    {
        if (disposed) return;
        Release(true); ResetBehavior(); ForgetOwner(); disposed = true;
    }
}
