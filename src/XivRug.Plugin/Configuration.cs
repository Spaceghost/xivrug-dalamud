using Dalamud.Configuration;
using XivSurface.Core;

namespace XivRug.Plugin;

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public FootprintShape Shape { get; set; } = FootprintShape.Circle;
    public float Radius { get; set; } = 2;
    public float HalfWidth { get; set; } = 2;
    public float HalfLength { get; set; } = 1.5f;
    public float CornerRadius { get; set; } = 0.45f;
    public float Feather { get; set; } = 0.18f;
    public bool ProbeNavmesh { get; set; }
    public bool RugEdges { get; set; } = true;
    public bool RugMotion { get; set; } = true;
    public bool PianoResponse { get; set; } = true;
    public float PianoStrength { get; set; } = .55f;
    // Light sculptures are independent of the optional physical cloth ripples.
    public bool MusicVisualizationEnabled { get; set; } = true;
    public MusicVisualizationMode MusicVisualization { get; set; } = MusicVisualizationMode.SpectrumCrown;
    public MusicRadialDirection MusicDirection { get; set; } = MusicRadialDirection.CenterOut;
    public float MusicVisualizationHeight { get; set; } = .8f;
    public float MusicVisualizationStrength { get; set; } = .65f;
    /// <summary>Optional visual aging, from fresh (0) to heavily travelled (1).</summary>
    public float RugWear { get; set; }
    public bool ShowRoute { get; set; } = true;
    public bool ShowGameMap { get; set; } = true;
    public bool ClothSurface { get; set; } = true;
    public bool JumpCatch { get; set; } = true;
    public bool IdleFootwork { get; set; } = true;
    // Retained only to read older saved configurations; automatic squatting
    // is retired until there is a complete, intentionally selected animation.
    public bool SquatWhenIdle { get; set; }
    public bool RideVisual { get; set; }
    public float MapRadius { get; set; } = 250;
    public float DragRadius { get; set; } = 0.2f;
    public float DragResponse { get; set; } = 0.55f;
    // New configurations draw before native UI. Preserve explicit saved false
    // values so an intentional diagnostic opt-out is never silently undone.
    public bool NativeUiComposition { get; set; } = true;

    public RugStyle Rug => RugStyle.Default with { Enabled = RugEdges, Animate = RugMotion };

    public void Sanitize()
    {
        SquatWhenIdle = false;
        if (!Enum.IsDefined(Shape)) Shape = FootprintShape.Circle;
        Radius = Clamp(Radius, 2, 1, 30);
        HalfWidth = Clamp(HalfWidth, 2, 1, 30);
        HalfLength = Clamp(HalfLength, 1.5f, 1, 30);
        var smallest = Shape == FootprintShape.Circle ? Radius : Math.Min(HalfWidth, HalfLength);
        CornerRadius = Clamp(CornerRadius, 0.45f, 0, smallest);
        Feather = Clamp(Feather, 0.18f, 0, smallest);
        MapRadius = Clamp(MapRadius, 250, 25, 1500);
        RugWear = Clamp(RugWear, 0, 0, 1);
        PianoStrength = Clamp(PianoStrength, .55f, 0, 1);
        if (!Enum.IsDefined(MusicVisualization)) MusicVisualization = MusicVisualizationMode.SpectrumCrown;
        if (!Enum.IsDefined(MusicDirection)) MusicDirection = MusicRadialDirection.CenterOut;
        MusicVisualizationHeight = Clamp(MusicVisualizationHeight, .8f, .1f, 2.5f);
        MusicVisualizationStrength = Clamp(MusicVisualizationStrength, .65f, 0, 1);
        DragRadius = Clamp(DragRadius, 0.2f, 0, Math.Min(8, smallest * 0.8f));
        DragResponse = Clamp(DragResponse, 0.55f, 0.05f, 2);
    }

    private static float Clamp(float value, float fallback, float min, float max) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, min, max);
}
