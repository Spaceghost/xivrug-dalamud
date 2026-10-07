using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivSurface.Core;

namespace XivRug.Plugin;

/// <summary>
/// Independent depth-reconstructed rug with selectable composition stage.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/rug";
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IObjectTable objects;
    private readonly Configuration config;
    private readonly LiveRug liveRug;
    private readonly CarpetCharacterPose characterPose;
    private readonly CharacterFootContacts footContacts;
    private readonly NativeFootFrameCapture? finalizedFeet;
    private readonly NativeShadowAtlasCapture? shadowAtlas;
    private readonly StaticRenderInventory floorInventory;
    private readonly TerrainRuntimeProbe terrainProbe;
    private readonly ClothReplayDiagnostic clothReplay;
    private int floorInventoryRequest;
    private double nextFloorInventory;
    private readonly ClothFootRenderPublisher renderFootPublisher = new();
    private double nextFootRefreshTiming, maximumPreRefreshAge, maximumFootRefreshDuration;
    private int footRefreshAccepted, footRefreshRejected;
    private bool footRefreshWarned;
    private readonly ICondition conditions;
    private readonly IPluginLog log;
    private readonly RugMotionProbe motionProbe = new();
    private int motionProbeRequest;
    private readonly ICallGateSubscriber<bool> ready;
    private readonly ICallGateSubscriber<string> getRoute;
    private volatile NavigationRoute? route;
    private double nextRoutePoll;
    private uint routeTerritory;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private volatile string status = "Navmesh probe disabled.";
    private double nextProbe;
    private uint lastTerritory;
    private bool open;
    private bool disposed;
    private ICallGateProvider<int>? ipcVersion;
    private ICallGateProvider<string>? ipcStatus;
    private string ipcSnapshot = "{}";
    private double nextIpcSnapshot;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IFramework framework,
        IClientState client, IObjectTable objects, IPluginLog log, ICondition conditions,
        IGameInteropProvider interop, IDataManager data, ITextureProvider textures, ISigScanner scanner)
    {
        this.pi = pi;
        this.commands = commands;
        this.framework = framework;
        this.client = client;
        this.objects = objects;
        this.conditions = conditions;
        this.log = log;
        config = pi.GetPluginConfig() as Configuration ?? new();
        config.Sanitize();
        ready = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        getRoute = pi.GetIpcSubscriber<string>("XivWayfinder.v1.GetRoute");
        pointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        liveRug = new LiveRug(pi, log, interop, data, textures);
        characterPose = new(data,objects,conditions,client,log);
        footContacts = new(objects, client, conditions, log);
        floorInventory = new(framework, client);
        terrainProbe = new(floorInventory, data, log);
        clothReplay = new(pi.GetPluginConfigDirectory());
        var registered = false;
        try
        {
            try { finalizedFeet = new(framework, objects, client, conditions, log, interop, scanner); }
            catch (Exception error) { log.Warning(error, "XivRug finalized-foot boundary unavailable; ordinary fresh contacts remain required"); }
            liveRug.FinalizedFeet = finalizedFeet;
            if (pi.IsDev)
            {
                try { shadowAtlas = new(interop, scanner, log, () => pi.UiBuilder.DeviceHandle); }
                catch (Exception error) { log.Warning(error, "XivRug shadow atlas observation unavailable"); }
            }
            registered = commands.AddHandler(Command, new CommandInfo(OnCommand)
                { HelpMessage = "Open rug settings; /rug idle|ride|catch on|off changes character/carpet actions; /rug music on|off toggles XivPiano cloth response; /rug follow <radius> <seconds> sets drag; /rug native on|off selects native-UI composition; /rug motionprobe on|off runs a bounded visual-only diagnostic." });
            if (!registered) throw new InvalidOperationException("The /rug command is already registered.");
            pi.UiBuilder.Draw += DrawSettings;
            pi.UiBuilder.OpenConfigUi += Open;
            pi.UiBuilder.OpenMainUi += Open;
            ipcVersion = pi.GetIpcProvider<int>("XivRug.v1.ApiVersion");
            ipcStatus = pi.GetIpcProvider<string>("XivRug.v1.GetStatus");
            ipcVersion.RegisterFunc(() => 1);
            ipcStatus.RegisterFunc(() => Volatile.Read(ref ipcSnapshot));
            PublishIpcStatus();
            framework.Update += Update;
        }
        catch
        {
            framework.Update -= Update;
            pi.UiBuilder.Draw -= DrawSettings;
            pi.UiBuilder.OpenConfigUi -= Open;
            pi.UiBuilder.OpenMainUi -= Open;
            ipcVersion?.UnregisterFunc(); ipcStatus?.UnregisterFunc();
            if (registered) commands.RemoveHandler(Command);
            shadowAtlas?.Dispose();
            finalizedFeet?.Dispose();
            characterPose.Dispose();
            terrainProbe.Dispose();
            clothReplay.Dispose();
            liveRug.Dispose();
            throw;
        }
    }

    private void Open() => open = true;
    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Equals("inspectcloth", StringComparison.OrdinalIgnoreCase))
        {
            if (pi.IsDev) clothReplay.Request();
            return;
        }
        if (parts.Length == 1 && parts[0].Equals("inspectterrain", StringComparison.OrdinalIgnoreCase))
        {
            if (pi.IsDev) terrainProbe.Request();
            return;
        }
        if (parts.Length == 1 && parts[0].Equals("inspectfloor", StringComparison.OrdinalIgnoreCase))
        {
            // Development-only, read-only request. Native descriptors are
            // copied on Framework.Update, never on the command caller thread.
            if (pi.IsDev) Interlocked.Exchange(ref floorInventoryRequest, 1);
            return;
        }
        if (parts.Length == 2 && parts[0].Equals("motionprobe", StringComparison.OrdinalIgnoreCase)
            && (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            // Request only. Scene checks and the bounded visual target run on
            // Framework.Update; no player movement, cast or saved setting.
            Interlocked.Exchange(ref motionProbeRequest, parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) ? 1 : -1);
            return;
        }
        if(parts.Length==2 && (parts[1]=="on" || parts[1]=="off"))
        {
            var value=parts[1]=="on";
            if(parts[0]=="ride") { config.RideVisual=value; pi.SavePluginConfig(config); return; }
            if(parts[0]=="idle") { config.IdleFootwork=value; pi.SavePluginConfig(config); return; }
            // Compatibility with old commands: never revive the retired pose.
            if(parts[0]=="squat") { config.SquatWhenIdle=false; pi.SavePluginConfig(config); return; }
            if(parts[0]=="catch") { config.JumpCatch=value; pi.SavePluginConfig(config); return; }
            if(parts[0]=="music") { config.PianoResponse=value; pi.SavePluginConfig(config); return; }
        }
        if (parts.Length == 3 && parts[0].Equals("follow", StringComparison.OrdinalIgnoreCase)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var radius)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var response)
            && float.IsFinite(radius) && radius >= 0 && radius <= 8
            && float.IsFinite(response) && response >= 0.05f && response <= 2)
        {
            config.DragRadius = radius;
            config.DragResponse = response;
            config.Sanitize();
            pi.SavePluginConfig(config);
            return;
        }
        if (parts.Length == 2 && parts[0].Equals("native", StringComparison.OrdinalIgnoreCase)
            && (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            // A setting change only: no movement, teleport, input automation,
            // or direct renderer call on the command handler's thread.
            config.NativeUiComposition = parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
            pi.SavePluginConfig(config);
            return;
        }
        Open();
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        finalizedFeet?.BeginFramework(config.Enabled && config.ClothSurface && config.NativeUiComposition);
        var nativeUpdateCompleted = false;
        try
        {
        shadowAtlas?.FindShaders();
        if (clothReplay.Poll(out var replayMessage)) log.Information("XivRug collision replay: {Result}", replayMessage);
        if (clothReplay.TakeRequest())
        {
            if (pi.IsDev && config.Enabled && config.ClothSurface && client.IsLoggedIn && objects.LocalPlayer is not null
                && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51]
                && !conditions[ConditionFlag.InCombat] && !conditions[ConditionFlag.OccupiedInCutSceneEvent]
                && !conditions[ConditionFlag.LoggingOut] && liveRug.BeginClothReplay(client.TerritoryType))
                log.Information("XivRug collision replay: observing existing queries for at most 10 seconds.");
            else { clothReplay.Refuse(); log.Information("XivRug collision replay unavailable in the current scene."); }
        }
        liveRug.InvalidatePhysicalCloth();
        var completed = false;
        var physicsSampledAt = double.NaN;
        try
        {
            var identity = footContacts.CaptureIdentity(config.Enabled);
            liveRug.FootFrame = renderFootPublisher.BeginUpdate(identity);
            CaptureFloorInventory();
            terrainProbe.Update(pi.IsDev && client.IsLoggedIn
                && !conditions[ConditionFlag.BetweenAreas] && !conditions[ConditionFlag.BetweenAreas51]
                && !conditions[ConditionFlag.InCombat] && !conditions[ConditionFlag.OccupiedInCutSceneEvent]
                && !conditions[ConditionFlag.LoggingOut], client.TerritoryType, objects.LocalPlayer?.Position);
            UpdateWorld(out physicsSampledAt);
            completed = true;
        }
        finally
        {
            // Includes diagnostic early returns, but NOT a failed update. A
            // failed body cannot keep obsolete cloth authorized with new feet.
            RefreshRenderFeet(completed, physicsSampledAt);
            if (liveRug.FinishClothReplay(client.TerritoryType, out var capturedReplay) && capturedReplay is not null)
                clothReplay.Submit(capturedReplay);
        }
        nativeUpdateCompleted = completed && liveRug.FootFrame is not null;
        }
        finally { finalizedFeet?.CompleteFramework(nativeUpdateCompleted); PublishIpcStatus(); }
    }

    private void PublishIpcStatus()
    {
        if (disposed || clock.Elapsed.TotalSeconds < nextIpcSnapshot) return;
        nextIpcSnapshot = clock.Elapsed.TotalSeconds + .2;
        var snapshot = JsonSerializer.Serialize(new
        {
            version = 1, observedAt = DateTimeOffset.UtcNow, loggedIn = client.IsLoggedIn,
            enabled = config.Enabled, settingsOpen = open, territoryId = client.TerritoryType,
            cloth = config.ClothSurface, nativeComposition = config.NativeUiComposition,
            renderer = liveRug.Status, composition = liveRug.CompositionStatus, map = liveRug.MapStatus,
            music = liveRug.MusicStatus, navmesh = status, hasSupportedCloth = liveRug.HasCloth,
            experimental = true, movementControl = false, recoveryCommand = "/rug",
        });
        Volatile.Write(ref ipcSnapshot, snapshot);
    }

    private void CaptureFloorInventory()
    {
        if (Interlocked.Exchange(ref floorInventoryRequest, 0) == 0 || !pi.IsDev) return;
        var now = clock.Elapsed.TotalSeconds;
        if (now < nextFloorInventory) return;
        nextFloorInventory = now + 20;
        var player = objects.LocalPlayer;
        if (!client.IsLoggedIn || player is null || conditions[ConditionFlag.BetweenAreas]
            || conditions[ConditionFlag.BetweenAreas51] || conditions[ConditionFlag.InCombat]
            || conditions[ConditionFlag.OccupiedInCutSceneEvent] || conditions[ConditionFlag.LoggingOut])
        { log.Information("XivRug static floor inventory unavailable in the current scene."); return; }
        try
        {
            var captured = floorInventory.Capture(client.TerritoryType, player.Position, 6);
            // Bounded copied descriptors only: no pointers, file contents,
            // terrain mutation, camera/player input or physics activation.
            // Capture occurs BEFORE feet are sampled so it cannot relabel an
            // older foot pose as fresh after diagnostic work.
            log.Information("XivRug static floor inventory: {Snapshot}", JsonSerializer.Serialize(captured,
                new JsonSerializerOptions { IncludeFields = true }));
        }
        catch (Exception ex)
        { log.Warning("XivRug static floor inventory failed: {Kind}", ex.GetType().Name); }
    }

    private void UpdateWorld(out double physicsSampledAt)
    {
        physicsSampledAt = double.NaN;
        PollRoute();
        var canSample = client.IsLoggedIn && !conditions[ConditionFlag.BetweenAreas]
            && !conditions[ConditionFlag.BetweenAreas51] && !conditions[ConditionFlag.OccupiedInCutSceneEvent]
            && !conditions[ConditionFlag.LoggingOut];
        characterPose.Update(canSample && config.Enabled && liveRug.HasCloth && config.IdleFootwork,canSample && config.Enabled && liveRug.HasCloth && config.RideVisual,clock.Elapsed.TotalSeconds,false,liveRug.SupportedCloth);
        physicsSampledAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var beforePhysics = footContacts.CaptureIdentity(canSample && config.Enabled);
        footContacts.Update(beforePhysics.Valid);
        var afterPhysics = footContacts.CaptureIdentity(config.Enabled);
        var physicsCompletedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var physicsFeet = renderFootPublisher.PublishEarlyCapture(beforePhysics, afterPhysics,
            physicsSampledAt, physicsCompletedAt, footContacts.RenderContacts);
        // Preserve the baseline's genuinely fresh early render fallback if a
        // native callback overlaps terrain work; the later read replaces it.
        liveRug.FootFrame = physicsFeet;
        liveRug.FootPressure = characterPose.SmoothedPatches.Concat(footContacts.Current.Select(
            foot => new ClothPressure(foot.Center, foot.Radius * 1.2f, 1, foot.FootY))).ToArray();
        var actualPlayer = objects.LocalPlayer?.Position;
        var probeAllowed = canSample && config.Enabled && !conditions[ConditionFlag.InCombat] && actualPlayer is not null;
        var wasProbing = motionProbe.Active;
        var probeRequest = Interlocked.Exchange(ref motionProbeRequest, 0);
        if (probeRequest < 0) motionProbe.Stop();
        if (probeRequest > 0)
        {
            var half = config.Shape == FootprintShape.Circle ? config.Radius : Math.Min(config.HalfWidth, config.HalfLength);
            if (probeAllowed && motionProbe.Start(client.TerritoryType, actualPlayer!.Value, clock.Elapsed.TotalSeconds, half))
                log.Information("XivRug motion probe started: visual rug target only, maximum15 seconds; real player/feet/map unchanged.");
            else log.Information("XivRug motion probe unavailable in the current scene.");
        }
        Vector2? probeTarget = null;
        if (motionProbe.TryTarget(client.TerritoryType, actualPlayer ?? default, clock.Elapsed.TotalSeconds, probeAllowed, out var target))
            probeTarget = target;
        if (wasProbing && !motionProbe.Active)
            log.Information("XivRug motion probe ended; following the actual character again.");
        liveRug.Update(client.TerritoryType, canSample ? actualPlayer : null, config, route, probeTarget, physicsFeet,
            conditions[ConditionFlag.Jumping] || conditions[ConditionFlag.Jumping61]);
        if (!config.ProbeNavmesh) { status = "Navmesh probe disabled."; return; }
        var player = objects.LocalPlayer;
        if (!client.IsLoggedIn || player is null) { status = "Waiting for a character."; return; }
        var now = clock.Elapsed.TotalSeconds;
        if (client.TerritoryType != lastTerritory)
        {
            lastTerritory = client.TerritoryType;
            nextProbe = 0;
            status = "Zone changed; previous floor sample discarded.";
        }
        if (now < nextProbe) return;
        nextProbe = now + 1; // diagnostic only, at most one floor query per second
        try
        {
            if (!ready.InvokeFunc()) { status = "vnavmesh has no ready mesh."; return; }
            var p = player.Position;
            var floor = pointOnFloor.InvokeFunc(p + new Vector3(0, 0.25f, 0), false, 0.5f);
            status = floor is { } f && float.IsFinite(f.X) && float.IsFinite(f.Y) && float.IsFinite(f.Z)
                ? $"Floor sample: {f.X:F2}, {f.Y:F2}, {f.Z:F2}. Diagnostic only; not a validated floor mask."
                : "No floor sample at the character.";
        }
        catch (Exception)
        {
            // Plugin absent, unloaded, or query failure: discard, never retain a
            // stale floor or turn a failed query into an on-top rectangle.
            status = "vnavmesh unavailable or query failed. No surface will be drawn.";
            nextProbe = now + 5;
        }
    }

    private void RefreshRenderFeet(bool updateSucceeded, double physicsSampledAt)
    {
        // A real late capture may reveal a different owner/model or missing
        // tracking without throwing. Revoke physical publication BEFORE that
        // read, then require the runtime owner to re-admit against its result.
        liveRug.InvalidatePhysicalCloth();
        if (!updateSucceeded || disposed)
        {
            renderFootPublisher.Invalidate();
            liveRug.FootFrame = null;
            return;
        }
        try
        {
            // Log the preceding window BEFORE the fresh read, so a logger
            // stall cannot be hidden by assigning an artificially recent time.
            if (clock.Elapsed.TotalSeconds >= nextFootRefreshTiming)
            {
                nextFootRefreshTiming = clock.Elapsed.TotalSeconds + 20;
                if (footRefreshAccepted + footRefreshRejected != 0)
                    log.Information("XivRug render foot refresh: published={Published}, rejected={Rejected}, max early-sample age before reread={WorkAgeMs:F2}ms, max reread duration={ReadMs:F2}ms; legacy expiry={ExpiryMs:F2}ms, cloth age cap={ClothCapMs:F2}ms",
                        footRefreshAccepted, footRefreshRejected, maximumPreRefreshAge * 1000,
                        maximumFootRefreshDuration * 1000, ClothFootRenderFrame.MaximumAgeSeconds * 1000,
                        ClothFootRenderFrame.MaximumAdaptiveClothAgeSeconds * 1000);
                footRefreshAccepted = footRefreshRejected = 0;
                maximumPreRefreshAge = maximumFootRefreshDuration = 0;
            }
            // This is a SECOND real framework-thread skeleton read. The old
            // contacts are never reused or retimestamped for rendering.
            var started = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var before = footContacts.CaptureIdentity(config.Enabled);
            footContacts.Update(before.Valid);
            var after = footContacts.CaptureIdentity(config.Enabled);
            var finished = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            liveRug.FootFrame = renderFootPublisher.CompleteUpdate(true, before, after,
                started, finished, footContacts.RenderContacts);
            if (liveRug.FootFrame is not null) footRefreshAccepted++; else footRefreshRejected++;
            if (double.IsFinite(physicsSampledAt) && started >= physicsSampledAt)
                maximumPreRefreshAge = Math.Max(maximumPreRefreshAge, started - physicsSampledAt);
            maximumFootRefreshDuration = Math.Max(maximumFootRefreshDuration, finished - started);
            footRefreshWarned = false;
        }
        catch (Exception error)
        {
            liveRug.InvalidatePhysicalCloth();
            renderFootPublisher.Invalidate();
            liveRug.FootFrame = null;
            footRefreshRejected++;
            if (!footRefreshWarned)
            {
                footRefreshWarned = true;
                log.Warning(error, "XivRug late foot capture failed; render authorization discarded");
            }
        }
    }

    private void DrawSettings()
    {
        liveRug.Queue();
        if (!open) return;
        ImGui.SetNextWindowSize(new Vector2(600, 370), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("XivRug development", ref open)) { ImGui.End(); return; }
        try
        {
            ImGui.TextWrapped(liveRug.Status);
            ImGui.TextWrapped(liveRug.CompositionStatus);
            var enabled = config.Enabled;
            if (ImGui.Checkbox("Show rug", ref enabled)) { config.Enabled = enabled; pi.SavePluginConfig(config); }
            ImGui.SameLine();
            if (ImGui.SmallButton("Retry renderer")) liveRug.Retry();
            var nativeComposition = config.NativeUiComposition;
            if (ImGui.Checkbox("Compose before native game UI (recommended; experimental)", ref nativeComposition))
            {
                config.NativeUiComposition = nativeComposition;
                pi.SavePluginConfig(config);
            }
            if (nativeComposition)
                ImGui.TextWrapped("Draws before native UI. If unavailable, the rug stays hidden; it never falls back on top of the HUD. Broader scene and device testing is still in progress.");
            else
                ImGui.TextWrapped("Legacy diagnostic mode: the rug can overlap native game windows and HUD. Enable native composition for normal use.");
            ImGui.Separator();
            if (route is { } navigation)
            {
                ImGui.TextWrapped(navigation.Action);
                ImGui.TextWrapped(navigation.Next);
                ImGui.TextUnformatted($"Wayfinder route: {navigation.Points.Length} corners");
            }
            else ImGui.TextDisabled("No fresh Wayfinder journey available.");
            var changed = false;
            var catchJump=config.JumpCatch;
            if(ImGui.Checkbox("Lift the carpet to catch jumps",ref catchJump)) {config.JumpCatch=catchJump;changed=true;}
            var idleFootwork=config.IdleFootwork;
            if(ImGui.Checkbox("Smooth the rug with my feet after 15 seconds idle",ref idleFootwork)) {config.IdleFootwork=idleFootwork;changed=true;}
            var ride=config.RideVisual;
            if(ImGui.Checkbox("Ride the carpet (local standing pose)",ref ride)) {config.RideVisual=ride;changed=true;}
            ImGui.TextWrapped(characterPose.Status);
            ImGui.TextWrapped(footContacts.Status);
            if (motionProbe.Active)
                ImGui.TextWrapped("Visual-only motion probe active (maximum 15 seconds). /rug motionprobe off stops it. No character movement or saved setting is changed.");
            ImGui.TextDisabled("Normal movement controls and speed apply.");
            var aged = config.RugWear > 0;
            if (ImGui.Checkbox("Aged and well-worn rug", ref aged)) { config.RugWear = aged ? .65f : 0; changed = true; }
            if (aged)
            {
                var wear = config.RugWear;
                if (ImGui.SliderFloat("Wear and faded dye", ref wear, .05f, 1, "%.2f")) { config.RugWear = wear; changed = true; }
                ImGui.TextWrapped("Softened fibers, weathered dye and worn edges. The map and guiding light remain readable.");
            }
            var showMap = config.ShowGameMap;
            if (ImGui.Checkbox("Actual game map on the rug", ref showMap)) { config.ShowGameMap = showMap; changed = true; }
            var mapRadius = config.MapRadius;
            if (ImGui.SliderFloat("Map coverage radius (yalms)", ref mapRadius, 25, 1500)) { config.MapRadius = mapRadius; changed = true; }
            ImGui.TextWrapped(liveRug.MapStatus);
            var dragRadius = config.DragRadius;
            if (ImGui.SliderFloat("Circular slack radius (yalms)", ref dragRadius, 0, 2)) { config.DragRadius = dragRadius; changed = true; }
            var dragResponse = config.DragResponse;
            if (ImGui.SliderFloat("Carpet glide time (seconds)", ref dragResponse, 0.1f, 1.2f)) { config.DragResponse = dragResponse; changed = true; }
            ImGui.TextWrapped("A tiny pull wakes the carpet; it glides up to your speed and settles underfoot. Longer glide time feels more leisurely. Fringe motion also enables subtle slithering flourishes.");
            var shape = (int)config.Shape;
            if (ImGui.Combo("Footprint", ref shape, "Circle\0Rounded rectangle\0"))
            { config.Shape = (FootprintShape)shape; changed = true; }
            if (config.Shape == FootprintShape.Circle)
            {
                var radius = config.Radius;
                if (ImGui.SliderFloat("Radius (yalms)", ref radius, 1, 30)) { config.Radius = radius; changed = true; }
            }
            else
            {
                var width = config.HalfWidth * 2;
                var length = config.HalfLength * 2;
                var corner = config.CornerRadius;
                if (ImGui.SliderFloat("Width (yalms)", ref width, 2, 60)) { config.HalfWidth = width / 2; changed = true; }
                if (ImGui.SliderFloat("Length (yalms)", ref length, 2, 60)) { config.HalfLength = length / 2; changed = true; }
                if (ImGui.SliderFloat("Corner radius", ref corner, 0, Math.Min(config.HalfWidth, config.HalfLength))) { config.CornerRadius = corner; changed = true; }
            }
            var feather = config.Feather;
            var limit = config.Shape == FootprintShape.Circle ? config.Radius : Math.Min(config.HalfWidth, config.HalfLength);
            if (ImGui.SliderFloat("Edge fade (yalms)", ref feather, 0, limit)) { config.Feather = feather; changed = true; }
            var probe = config.ProbeNavmesh;
            var rug = config.RugEdges;
            if (ImGui.Checkbox("Woven rug border and fringe", ref rug)) { config.RugEdges = rug; changed = true; }
            var motion = config.RugMotion;
            if (ImGui.Checkbox("Cloth and fringe motion (off for reduced motion)", ref motion)) { config.RugMotion = motion; changed = true; }
            var musicVisual = config.MusicVisualizationEnabled;
            if (ImGui.Checkbox("3-D music visualization above the map", ref musicVisual)) { config.MusicVisualizationEnabled = musicVisual; changed = true; }
            if (musicVisual)
            {
                var musicMode = (int)config.MusicVisualization;
                if (ImGui.Combo("Music sculpture", ref musicMode, "Spectrum crown\0Radial ribbons\0Spiral fountain\0Orbit halo\0Helix canopy\0Prism bloom\0Wave dome\0Star fountain\0Aurora veil\0Resonance arches\0"))
                { config.MusicVisualization = (MusicVisualizationMode)musicMode; changed = true; }
                var musicDirection = (int)config.MusicDirection;
                if (ImGui.Combo("Spectrum direction", ref musicDirection, "Center outward\0Outside inward\0"))
                { config.MusicDirection = (MusicRadialDirection)musicDirection; changed = true; }
                var musicHeight = config.MusicVisualizationHeight;
                if (ImGui.SliderFloat("Sculpture height (yalms)", ref musicHeight, .1f, 2.5f, "%.2f"))
                { config.MusicVisualizationHeight = musicHeight; changed = true; }
                var musicStrength = config.MusicVisualizationStrength;
                if (ImGui.SliderFloat("Spectrum intensity", ref musicStrength, 0, 1, "%.2f"))
                { config.MusicVisualizationStrength = musicStrength; changed = true; }
                ImGui.TextWrapped("Uses XivPiano's actual audible frequency spectrum. Paused or missing audio stays quiet; the rug and map keep their size.");
                if (!motion) ImGui.TextWrapped("Music visualization is paused while reduced motion is enabled.");
                if (!config.NativeUiComposition || !config.ClothSurface)
                    ImGui.TextWrapped("3-D music requires the cloth rug and native composition, so it stays behind game windows.");
            }
            var music = config.PianoResponse;
            if (ImGui.Checkbox("Let XivPiano music ripple through the cloth", ref music)) { config.PianoResponse = music; changed = true; }
            if (music)
            {
                if (!motion) ImGui.TextWrapped("Music ripples are paused while reduced motion is enabled.");
                var strength = config.PianoStrength;
                if (ImGui.SliderFloat("Music flourish", ref strength, 0, 1, "%.2f")) { config.PianoStrength = strength; changed = true; }
            }
            if (musicVisual || music) ImGui.TextWrapped(liveRug.MusicStatus);
            var showRoute = config.ShowRoute;
            if (ImGui.Checkbox("Weave the Wayfinder path into the rug", ref showRoute)) { config.ShowRoute = showRoute; changed = true; }
            if (ImGui.Checkbox("Read-only navmesh diagnostic (1 Hz)", ref probe)) { config.ProbeNavmesh = probe; changed = true; }
            if (changed) { config.Sanitize(); pi.SavePluginConfig(config); }
            ImGui.TextWrapped(status);
            ImGui.TextWrapped("The rug and idle poses follow your character. Normal movement controls remain available.");
        }
        finally { ImGui.End(); }
    }

    public void Dispose()
    {
        disposed = true;
        ipcVersion?.UnregisterFunc(); ipcStatus?.UnregisterFunc();
        framework.Update -= Update;
        pi.UiBuilder.Draw -= DrawSettings;
        pi.UiBuilder.OpenConfigUi -= Open;
        pi.UiBuilder.OpenMainUi -= Open;
        commands.RemoveHandler(Command);
        shadowAtlas?.Dispose();
        finalizedFeet?.Dispose();
        characterPose.Dispose();
        terrainProbe.Dispose();
        liveRug.CancelClothReplay();
        clothReplay.Dispose();
        liveRug.Dispose();
    }

    private void PollRoute()
    {
        if (!client.IsLoggedIn) { route = null; return; }
        if (routeTerritory != client.TerritoryType)
        {
            routeTerritory = client.TerritoryType;
            route = null;
            nextRoutePoll = 0;
        }
        var now = clock.Elapsed.TotalSeconds;
        if (now < nextRoutePoll) return;
        nextRoutePoll = now + 0.25;
        try
        {
            route = NavigationRoute.TryRead(getRoute.InvokeFunc(), client.TerritoryType,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var latest) ? latest : null;
        }
        catch (Exception) { route = null; }
    }
}
