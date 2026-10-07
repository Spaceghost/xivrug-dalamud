using System.Diagnostics;
using System.Numerics;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using XivSurface.Core;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace XivRug.Plugin;

/// <summary>Unwired diagnostic. Call explicitly on Framework.Update. Only
/// copied paths/transforms/bounds escape the native read scope. This never
/// invokes load/update/set methods or changes cloth/support/game state.</summary>
internal sealed unsafe class StaticRenderInventory(IFramework framework, IClientState client)
{
    private const int MaximumVisited = 16384, MaximumDescriptors = 128, MaximumTerrainPlates = 4096;
    private const double MaximumMilliseconds = 2;
    private static readonly Guid VerifiedBindings = new("7fda2a82-4ca8-49d0-9687-4c8eac011509");
    private ulong generation;

    internal sealed record Snapshot(uint Territory, ulong Generation, double CapturedSeconds,
        double DurationMilliseconds, bool Complete, int Visited, int Rejected,
        IReadOnlyList<StaticRenderDescriptor> Instances, string Status)
    {
        /// <summary>When true, Complete covers the terrain-plate inventory
        /// only. It says nothing about background props or complete colliders.</summary>
        public bool TerrainOnly { get; init; }
        /// <summary>Rejected terrain observations without valid outside-interest bounds.
        /// A finished traversal with these omissions is not complete local ground evidence.</summary>
        public int UnresolvedTerrain { get; init; }
        public int OutsideInterestTerrain { get; init; }
    }

    public Snapshot Capture(uint territory, Vector3 center, float radius = 8) =>
        CaptureCore(territory, center, radius, includeBackground: true);

    /// <summary>Bounded terrain-only producer. Unrelated background prop scans
    /// cannot exhaust its completion budget. Same typed reads and ownership
    /// checks as the diagnostic inventory; still no loaded-content proof.</summary>
    public Snapshot CaptureTerrain(uint territory, Vector3 center, float radius = 8) =>
        CaptureCore(territory, center, radius, includeBackground: false) with { TerrainOnly = true };

    private Snapshot CaptureCore(uint territory, Vector3 center, float radius, bool includeBackground)
    {
        var started = Stopwatch.GetTimestamp(); var at = started / (double)Stopwatch.Frequency;
        if (typeof(LayoutWorld).Assembly.ManifestModule.ModuleVersionId != VerifiedBindings)
            return new(territory, generation, at, 0, false, 0, 0, [], "Installed layout bindings have not been reviewed.");
        if (!framework.IsInFrameworkUpdateThread || territory == 0 || client.TerritoryType != territory
            || !StaticRenderDescriptorPolicy.Finite(center) || !float.IsFinite(radius) || radius is <= 0 or > 30)
            return new(territory, generation, at, 0, false, 0, 0, [], "Invalid framework/scene capture request.");
        var world = LayoutWorld.Instance(); var layout = world == null ? null : world->ActiveLayout;
        if (layout == null || layout->InitState != 7 || layout->TerritoryTypeId != territory
            || layout->FestivalStatus is > 0 and < 5)
            return new(territory, generation, at, 0, false, 0, 0, [], "Active layout is not ready for this territory.");
        // Diagnostic generations are unique capture epochs. No layout or
        // resource address is retained even as a future lookup key.
        var capturedGeneration = ++generation;
        var records = new List<StaticRenderDescriptor>(); var visited = 0; var rejected = 0; var complete = true;
        var unresolvedTerrain = 0; var outsideTerrain = 0;
        bool Budget() => visited < MaximumVisited && records.Count < MaximumDescriptors
            && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaximumMilliseconds;

        // Terrain first: a small typed plate inventory is often the large
        // static ground mesh, while BgPart maps can have thousands of props.
        foreach (var (_, terrainPointer) in layout->Terrains)
        {
            if (!Budget()) { complete = false; break; }
            visited++;
            var manager = terrainPointer.Value; var terrain = manager == null ? null : manager->GfxTerrain;
            if (terrain == null || terrain->TerrainPlateCount > MaximumTerrainPlates)
            { rejected++; unresolvedTerrain++; continue; }
            var plates = terrain->TerrainPlates;
            if (plates.Length > 0 && System.Runtime.CompilerServices.Unsafe.IsNullRef(
                ref System.Runtime.InteropServices.MemoryMarshal.GetReference(plates)))
            { rejected++; unresolvedTerrain++; continue; }
            var parentProven = IdentityParent(&terrain->DrawObject.Object);
            if (!StaticRenderDescriptorPolicy.TryTransform(terrain->Position, terrain->Rotation, terrain->Scale, out var terrainWorld))
            { rejected++; unresolvedTerrain++; continue; }
            // TerrainPlate.Translation is a managed typed getter, verified in
            // installed metadata. Never synthesize a ####.mdl filename.
            foreach (var platePointer in plates)
            {
                if (!Budget()) { complete = false; break; }
                visited++;
                var plate = platePointer.Value;
                if (plate == null) { rejected++; unresolvedTerrain++; continue; }
                var transform = Matrix4x4.CreateTranslation((Vector3)plate->Translation) * terrainWorld;
                var issues = parentProven ? StaticRenderIssue.None : StaticRenderIssue.ParentTransformUnproven;
                var disposition = Read(plate->ModelResourceHandle, transform, center, radius, terrainBounds: true,
                    StaticRenderKind.TerrainPlate, ((ulong)manager->Id << 32) | plate->LinearGridIndex, issues, out var descriptor);
                if (disposition != DescriptorRead.Accepted)
                {
                    rejected++;
                    if (disposition == DescriptorRead.OutsideInterest) outsideTerrain++; else unresolvedTerrain++;
                    continue;
                }
                records.Add(descriptor!);
            }
        }
        if (includeBackground && Budget() && layout->InstancesByType.TryGetValuePointer(InstanceType.BgPart, out var partsPointer)
            && partsPointer != null && partsPointer->Value != null)
        {
            foreach (var (key, instancePointer) in *partsPointer->Value)
            {
                if (!Budget()) { complete = false; break; }
                visited++;
                var instance = (BgPartsLayoutInstance*)instancePointer.Value;
                if (instance == null || !instance->IsActive || instance->Layout != layout) { rejected++; continue; }
                var graphics = instance->GraphicsObject;
                if (graphics == null || graphics->ModelResourceHandle == null
                    || graphics->ModelResourceHandle->LoadState != 7) { rejected++; continue; }
                if (!StaticRenderDescriptorPolicy.TryTransform(graphics->Position, graphics->Rotation, graphics->Scale, out var transform))
                { rejected++; continue; }
                var issues = StaticRenderIssue.None;
                if (!IdentityParent(&graphics->DrawObject.Object)) issues |= StaticRenderIssue.ParentTransformUnproven;
                if (instance->NestingLevel != 0) issues |= StaticRenderIssue.NestedLayout;
                var animation = graphics->LoadedAnimationData;
                if (animation != null && (animation->RenderSkeleton != null || animation->AsyncSkeletonResourceHandle != null
                    || animation->AsyncPapResourceHandle != null)) issues |= StaticRenderIssue.Animated;
                // A typed read-only layout getter offers a second independent
                // SRT observation. Agreement is evidence, not final proof that
                // materials/shape keys do not alter rendered vertices.
                var layoutTransform = instance->GetTransformImpl();
                if (layoutTransform == null || !StaticRenderDescriptorPolicy.TryTransform(layoutTransform->Translation,
                    layoutTransform->Rotation, layoutTransform->Scale, out var layoutWorld)
                    || !StaticRenderDescriptorPolicy.Equivalent(transform, layoutWorld)) issues |= StaticRenderIssue.LayoutTransformMismatch;
                if (Read(graphics->ModelResourceHandle, transform, center, radius, terrainBounds: false,
                    StaticRenderKind.BackgroundPart, key, issues, out var descriptor) != DescriptorRead.Accepted) { rejected++; continue; }
                records.Add(descriptor!);
            }
        }
        else if (includeBackground && !Budget()) complete = false;
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // Reject the whole observation if its loaded-layout/zone ownership
        // changed. Callers must not reuse this diagnostic as a support cache.
        if (client.TerritoryType != territory || LayoutWorld.Instance() != world || world->ActiveLayout != layout
            || layout->InitState != 7 || layout->TerritoryTypeId != territory || layout->FestivalStatus is > 0 and < 5)
            return new(territory, capturedGeneration, at, duration, false, visited, rejected, [], "Scene identity changed during capture.");
        return new(territory, capturedGeneration, at, duration, complete, visited, rejected,
            Array.AsReadOnly(records.ToArray()), complete ? "Static-render inventory copied (diagnostic only)." : "Bounded partial static-render inventory (diagnostic only).")
            { UnresolvedTerrain = unresolvedTerrain, OutsideInterestTerrain = outsideTerrain };
    }

    private enum DescriptorRead { Unresolved, OutsideInterest, Accepted }

    private static DescriptorRead Read(ModelResourceHandle* model, Matrix4x4 world, Vector3 center, float radius,
        bool terrainBounds, StaticRenderKind kind, ulong key, StaticRenderIssue issues, out StaticRenderDescriptor? descriptor)
    {
        descriptor = null;
        if (model == null || model->LoadState != 7) return DescriptorRead.Unresolved;
        var bounds = terrainBounds && model->TerrainBounds != null ? model->TerrainBounds : model->AxisAlignedBounds;
        if (bounds == null) return DescriptorRead.Unresolved;
        Vector3 minimum = bounds->Min, maximum = bounds->Max;
        var disposition = StaticRenderDescriptorPolicy.ClassifyBounds(minimum, maximum, world, center, radius);
        if (disposition == StaticRenderBoundsDisposition.Invalid) return DescriptorRead.Unresolved;
        // An unknown parent transform cannot justify discarding even a far-looking plate.
        if (disposition == StaticRenderBoundsDisposition.OutsideInterest)
            return issues == StaticRenderIssue.None ? DescriptorRead.OutsideInterest : DescriptorRead.Unresolved;
        if (model->FileName.Length is < 8 or > StaticRenderDescriptorPolicy.MaximumPathBytes
            || model->FileName.Length > model->FileName.Capacity) return DescriptorRead.Unresolved;
        var path = model->FileName.AsSpan();
        if (!StaticRenderDescriptorPolicy.ValidPath(path)) return DescriptorRead.Unresolved;
        descriptor = new(kind, key, Encoding.UTF8.GetString(path), model->Id, world, minimum, maximum, issues);
        return DescriptorRead.Accepted;
    }

    private static bool IdentityParent(SceneObject* item)
    {
        var parent = item->ParentObject;
        return parent == null || StaticRenderDescriptorPolicy.TryTransform(parent->Position, parent->Rotation, parent->Scale,
            out var transform) && StaticRenderDescriptorPolicy.Equivalent(transform, Matrix4x4.Identity)
            && parent->ParentObject == null;
    }
}
