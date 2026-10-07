using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XivSurface.RenderedGeometry;

/// <summary>Explicit archive-content assumption, NOT an assertion about bytes already loaded
/// by the game. LOD0 main only, explicitly reviewed static opaque materials, no waving/skin/shapes.
/// Camera-culling grid indices do not remove material from this geometric candidate.</summary>
public sealed class TerrainContentProfile
{
    public const string ShaderPath = "shader/sm5/shpk/bg.shpk";
    public const string StairMaterial = "bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_sbwd2_h.mtrl";
    public const string ShaderHash = "9617282ff8a947ceecd11846a73fc1d5ec7782cf19fe22ec363cc6d3195375be";
    public const string MaterialHash = "03093de0e8835f7c65dd089103eb2341c17541483100c2704fd119d3b136e4aa";
    private readonly Dictionary<string, TerrainAssetProfile> assets;
    private readonly Dictionary<string, string> materials;
    private TerrainContentProfile(string id, Dictionary<string, TerrainAssetProfile> assets, Dictionary<string,string> materials)
    {
        Id=id;this.assets=assets;this.materials=materials;
        var identity=id+"\n"+ShaderHash+"\n"+string.Join('\n',assets.OrderBy(v=>v.Key,StringComparer.Ordinal).Select(v=>$"{v.Key}:{v.Value.Sha256}:{v.Value.CullingGridCount}"))
            +"\n"+string.Join('\n',materials.OrderBy(v=>v.Key,StringComparer.Ordinal).Select(v=>$"{v.Key}:{v.Value}"));
        Fingerprint=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public string Id { get; }
    public string Fingerprint { get; }
    public string Confidence => "AssumedKnownVanilla";
    public bool LoadedContentVerified => false;
    public int Lod => 0;
    public string Limit => "Exact archive profile, not loaded-byte or live shader/LOD proof; only explicitly reviewed material pins are included.";
    public static TerrainContentProfile OptInKnownVanillaStairs() => new("DEV-limsa-s1t2-known-vanilla-20261003-v1",new()
    {
        ["bg/ffxiv/sea_s1/twn/s1t2/bgplate/0014.mdl"] = new("6833786df868d5a4dccdd2a9bfc4618b0a2de564efcd32d6e255ea5b312811e4", 30),
        ["bg/ffxiv/sea_s1/twn/s1t2/bgplate/0023.mdl"] = new("78711c30accda284655b105cab0a9f21f1edbdbc3ac0baee7d6d237ed8ae31ea", 50),
    },new()
    {
        [StairMaterial]=MaterialHash,
        ["bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_ston1_h.mtrl"]="91c18d6ae6dac1ebfcc92aedcdf8f5c440cf5a541fd5b9a9253fffe6f082e1ad",
        ["bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_stpv2_h_bgs0.mtrl"]="a72e6eeaae87fb8b07d2c427c7210eb2bfe3d2d3ccf09ef7155c676448a981d4",
    });
    /// <summary>Local, explicitly reviewed profile registration. Not a parser for remote/model
    /// metadata and never verified loaded content. Caller attests each pinned MTRL's intended
    /// static opaque variants for the exact reviewed bg package. Non-default keys require
    /// exact selector/alias and shader review, not merely a hash or no-transparency flag.
    /// This attestation does not establish live scene/effect/subview/LOD selection or loaded
    /// replacement bytes. Runtime default remains no profile. See REVIEWED_LIMSA_ROCK.txt.</summary>
    public static TerrainContentProfile AssumeReviewedStaticBgTerrain(string id,
        IReadOnlyDictionary<string,TerrainAssetProfile> modelPins,IReadOnlyDictionary<string,string> materialPins)
    {
        if(string.IsNullOrWhiteSpace(id)||id.Length>128||id.Any(char.IsControl)||modelPins.Count is <1 or >256||materialPins.Count is <1 or >128)
            throw new ArgumentException("Profile bounds");
        foreach(var pin in modelPins)
            if(!Path(pin.Key,".mdl")||!Hash(pin.Value.Sha256)||pin.Value.CullingGridCount is <1 or >4096)throw new ArgumentException("Model profile");
        foreach(var pin in materialPins)if(!Path(pin.Key,".mtrl")||!Hash(pin.Value))throw new ArgumentException("Material profile");
        return new(id,modelPins.ToDictionary(),materialPins.ToDictionary());
    }
    private static bool Hash(string value)=>value.Length==64&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Path(string value,string suffix)=>value.Length<=768&&value.StartsWith("bg/",StringComparison.Ordinal)
        &&value.EndsWith(suffix,StringComparison.Ordinal)&&!value.Any(char.IsControl)&&!value.Contains('\\')
        &&value.Split('/').All(s=>s is not "" and not "." and not "..");
    public bool TryAsset(string path, out TerrainAssetProfile asset) => assets.TryGetValue(path, out asset);
    public bool IncludesMaterial(string path)=>materials.ContainsKey(path);
    internal IEnumerable<KeyValuePair<string,string>> MaterialPins=>materials;
}

public readonly record struct TerrainAssetProfile(string Sha256, uint CullingGridCount);

/// <summary>Read reconstructed resource bytes, bounded BEFORE allocation by implementation.
/// Only invoked on the stream worker. Cancellation may be cooperative; the stream never fans
/// out an abandoned reader. Replacement revision must change when a known provider changes.</summary>
public interface ITerrainAssetReader
{
    ValueTask<ReadOnlyMemory<byte>?> ReadAsync(string path, int maximumBytes, CancellationToken cancellationToken);
}

public interface ITerrainCandidateLoader
{
    ValueTask<RenderedMdlGeometry?> LoadAsync(string path, TerrainContentProfile profile,
        string providerRevision, CancellationToken cancellationToken);
}

/// <summary>Review-scoped decoder. Uses the frozen inspection extractor without changing its
/// strict API. Exact profile hashes establish reviewed archive semantics, never live identity.</summary>
public sealed class ProfiledTerrainLoader(ITerrainAssetReader reader) : ITerrainCandidateLoader
{
    private string? checkedRevision;
    public async ValueTask<RenderedMdlGeometry?> LoadAsync(string path, TerrainContentProfile profile,
        string providerRevision, CancellationToken cancellationToken)
    {
        if (!profile.TryAsset(path, out var asset)) return null;
        var dependencyRevision=profile.Fingerprint+":"+providerRevision;
        if (checkedRevision != dependencyRevision)
        {
            if (!await Matches(TerrainContentProfile.ShaderPath, TerrainContentProfile.ShaderHash, 96 * 1024 * 1024, cancellationToken))return null;
            foreach(var pin in profile.MaterialPins)if(!await Matches(pin.Key,pin.Value,65536,cancellationToken))return null;
            checkedRevision = dependencyRevision;
        }
        var memory = await reader.ReadAsync(path, RenderedMdlTriangles.MaximumBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (memory is not { } raw || raw.Length > RenderedMdlTriangles.MaximumBytes || raw.Length < 68) return null;
        // Own bytes before hash/metadata/parse; a provider cannot mutate this snapshot afterward.
        var bytes = raw.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != asset.Sha256) return null;
        var declarations = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
        var stringHeader = checked(68 + declarations * 136);
        if (stringHeader + 8 > bytes.Length) return null;
        var strings = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(stringHeader + 4));
        var header = (long)stringHeader + 8 + strings;
        if (header + 0x26 > bytes.Length || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan((int)header + 0x24)) != asset.CullingGridCount) return null;
        var result = RenderedMdlTriangles.InspectUnresolvedDrawGroups(path, bytes, cancellationToken);
        if (result.Geometry is not { } geometry || result.Status != RenderedMdlStatus.Success) return null;
        foreach (var group in geometry.DrawRanges)
            if (group.RawVisibilityField >= asset.CullingGridCount) return null;
        return geometry;
    }

    private async ValueTask<bool> Matches(string path, string hash, int cap, CancellationToken ct)
    {
        var memory = await reader.ReadAsync(path, cap, ct);
        ct.ThrowIfCancellationRequested();
        return memory is { } bytes && bytes.Length <= cap && Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) == hash;
    }
}
