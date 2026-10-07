using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;

namespace XivSurface.Dalamud;

/// <summary>Read-only weather/time lighting. The reviewed EnvState prefix is
/// bounded by the matched native structure; no environment values are written.
/// Direction is a celestial approximation, not access to the game's shadow map.</summary>
internal sealed unsafe class WorldClothLighting
{
    private static readonly Guid VerifiedBindings = new("7fda2a82-4ca8-49d0-9687-4c8eac011509");
    private static readonly bool Compatible = typeof(EnvManager).Assembly.ManifestModule.ModuleVersionId == VerifiedBindings
        && Marshal.OffsetOf<EnvManager>(nameof(EnvManager.EnvState)).ToInt64() == 0x58 && sizeof(EnvState) == 0x2F8;
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeColors { public Vector3 Sun, Moon, Ambient; }
    internal readonly record struct Frame(Vector3 Ambient, Vector3 Sun, Vector3 Moon, Vector3 Direction, float Darkness);
    private Frame previous = new(new(.3f), Vector3.Zero, Vector3.Zero, Vector3.UnitY, 0);
    private double lastTime = double.NaN;
    private Vector3 rawSun, rawMoon, rawAmbient;
    private float daySeconds;
    private string source = "Waiting for world lighting";
    public string Diagnostic => FormattableString.Invariant($"{source}; time={daySeconds:F0}s, raw sun={rawSun}, moon={rawMoon}, ambient={rawAmbient}; used ambient={previous.Ambient}, sun={previous.Sun}, moon={previous.Moon}, wisps={previous.Darkness:F3}");

    public Frame Sample(double now)
    {
        var desired = new Frame(new(.3f), Vector3.Zero, Vector3.Zero, Vector3.UnitY, 0);
        source = "World lighting unavailable: conservative neutral fallback";
        if (Compatible)
        {
            try
            {
                var manager = EnvManager.Instance();
                if (manager != null && manager->EnvScene != null)
                {
                    // EnvState.EnvironmentLighting at0x20 is the reviewed
                    // Brio EnvLighting prefix: three consecutive float3 colors.
                    var colors = *(NativeColors*)((byte*)&manager->EnvState + 0x20);
                    var time = manager->DayTimeSeconds;
                    if (Valid(colors.Sun) && Valid(colors.Moon) && Valid(colors.Ambient)
                        && float.IsFinite(time) && time is >= 0 and <= 86400)
                    {
                        rawSun = colors.Sun; rawMoon = colors.Moon; rawAmbient = colors.Ambient; daySeconds = time;
                        var angle = (time / 86400f - .25f) * MathF.Tau;
                        var elevation = MathF.Sin(angle);
                        var direction = Vector3.Normalize(new(.8f * MathF.Cos(angle), elevation, .6f * MathF.Cos(angle)));
                        var activeSun = colors.Sun * Smooth(-.08f, .16f, elevation);
                        var activeMoon = colors.Moon * Smooth(-.08f, .16f, -elevation);
                        // The native colors can exceed one at noon. Keep the
                        // direct-plus-bounce response below display clipping for
                        // ordinary daylight; retain the actual color/direction.
                        var sun = activeSun * .45f;
                        var moon = activeMoon * .4f;
                        // Some outdoor environments publish zero in this ambient
                        // prefix while their sky still contributes indirect light.
                        // Approximate that bounce from the actual active key color,
                        // so grazing morning light does not make flat cloth black.
                        var ambient = colors.Ambient * .8f + activeSun * .2f + activeMoon * .2f;
                        // Darkness measures available environment radiance, not
                        // the cloth normal or reduced direct-light contribution.
                        // A sunny morning must not turn the night wisps on.
                        var luminance = Vector3.Dot(colors.Ambient + activeSun * .65f + activeMoon * .4f,
                            new(.2126f, .7152f, .0722f));
                        desired = new(Vector3.Min(ambient, new(1.2f)), Vector3.Min(sun, new(1.4f)),
                            Vector3.Min(moon, new(.8f)), direction, 1 - Smooth(.12f, .38f, luminance));
                        source = "World EnvState lighting";
                    }
                }
            }
            catch (Exception) { source = "World lighting not ready: conservative neutral fallback"; }
        }
        else source = "World lighting bindings changed: conservative neutral fallback";
        var blend = !double.IsFinite(lastTime) || now < lastTime || now - lastTime > 2 ? 1
            : 1 - MathF.Exp(-(float)Math.Clamp(now - lastTime, 0, 1) / .4f);
        lastTime = now;
        previous = new(Vector3.Lerp(previous.Ambient, desired.Ambient, blend), Vector3.Lerp(previous.Sun, desired.Sun, blend),
            Vector3.Lerp(previous.Moon, desired.Moon, blend), desired.Direction, float.Lerp(previous.Darkness, desired.Darkness, blend));
        return previous;
    }
    private static bool Valid(Vector3 color) => float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z)
        && color.X is >= 0 and <= 32 && color.Y is >= 0 and <= 32 && color.Z is >= 0 and <= 32;
    private static float Smooth(float low, float high, float value)
    { var t = Math.Clamp((value - low) / (high - low), 0, 1); return t * t * (3 - 2 * t); }
}
