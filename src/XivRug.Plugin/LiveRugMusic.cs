using System.Numerics;
using XivSurface.Core;
using XivSurface.Dalamud;

namespace XivRug.Plugin;

internal sealed unsafe partial class LiveRug
{
    private readonly MusicGpu musicGpu = new();
    private readonly MusicVertex[] musicLocal = new MusicVertex[2304];
    private readonly MusicVertex[] musicWorld = new MusicVertex[2304];
    private Vector3[] musicPositions = [], musicNormals = [];
    private float[] musicCeilings = [];
    private bool musicFaulted;
    private PianoRugSource.MusicFrame? submittingMusic;
    private Func<bool>? submittingMusicParentGuard;
    private Func<bool>? musicSubmitGuard;

    // Caller holds renderLock and remains inside the native UI composition
    // envelope, after a successful rug draw. No independent HUD overlay path.
    private void DrawMusic(nint device, ClothMesh mesh, Vector2 halfSize,
        Configuration config, ReadOnlySpan<ClothFootContact> feet, double clothSeconds, float renderLift, Func<bool>? parentGuard)
    {
        if (musicFaulted || !config.MusicVisualizationEnabled || !config.RugMotion
            || piano.CurrentMusic is not { } audio) return;
        var now = clock.Elapsed.TotalSeconds;
        var bands = audio.At(now);
        if (bands.IsEmpty) return;
        try
        {
            // These are the SAME deformation inputs as ClothGpu, including
            // final feet ceilings and current lift; not a guessed flat plane.
            var count = mesh.Positions.Length;
            if (musicPositions.Length < count)
            {
                musicPositions = new Vector3[count]; musicNormals = new Vector3[count];
                musicCeilings = new float[count];
            }
            var positions = musicPositions.AsSpan(0, count);
            var normals = musicNormals.AsSpan(0, count);
            var ceilings = musicCeilings.AsSpan(0, count);
            ClothContactConstraint.BuildRenderCeilings(mesh, feet, renderLift, ceilings);
            ClothRenderPose.Prepare(mesh, ceilings, halfSize, config.Shape == FootprintShape.Circle,
                config.CornerRadius, ClothRenderClock.Phase(clothSeconds), config.RugMotion, config.RugEdges,
                renderLift, positions, normals);
            var radius = Math.Min(halfSize.X, halfSize.Y) * .9f;
            var vertices = MusicVisualization.Build(musicLocal, bands, config.MusicVisualization,
                config.MusicDirection, radius, config.MusicVisualizationHeight, now,
                config.MusicVisualizationStrength);
            if (vertices == 0 || !MusicSurfaceAnchor.TryGround(mesh, positions, halfSize,
                musicLocal.AsSpan(0, vertices), musicWorld.AsSpan(0, vertices))) return;
            submittingMusic = audio;
            submittingMusicParentGuard = parentGuard;
            musicSubmitGuard ??= CanSubmitMusic;
            musicGpu.Draw(device, musicWorld.AsSpan(0, vertices), musicSubmitGuard);
        }
        catch (Exception error)
        {
            // An optional visualization failure must not turn off the rug.
            musicFaulted = true;
            log.Warning(error, "XivRug music visualization stopped; rug rendering remains available.");
        }
        finally { submittingMusic = null; submittingMusicParentGuard = null; }
    }

    private bool CanSubmitMusic() => !disposed && !gpuFaulted && !musicFaulted
        && settings is { Enabled: true, NativeUiComposition: true, ClothSurface: true,
            RugMotion: true, MusicVisualizationEnabled: true }
        && submittingMusic is { } audio && ReferenceEquals(audio, piano.CurrentMusic)
        && !audio.At(clock.Elapsed.TotalSeconds).IsEmpty
        && (submittingMusicParentGuard is null || submittingMusicParentGuard());
}
