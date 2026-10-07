namespace XivSurface.Core;

public static class SurfaceShader
{
    /// <summary>Embedded ps_5_0 bytecode, built offline; no runtime compiler or Ghostty required.</summary>
    public static byte[] ReadBytecode()
    {
        using var stream = typeof(SurfaceShader).Assembly.GetManifestResourceStream("XivSurface.SurfaceDecal.dxbc")
            ?? throw new InvalidOperationException("Surface shader was not embedded.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
