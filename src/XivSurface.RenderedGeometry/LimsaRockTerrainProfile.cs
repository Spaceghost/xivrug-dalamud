namespace XivSurface.RenderedGeometry;

/// <summary>Explicit DEV fixture, not location-independent support or an automatic default.
/// Pins the installed Limsa rock/stair assets and reviewed keyed static bg shader families.
/// See REVIEWED_LIMSA_ROCK.txt for exact scope and unresolved live-content/variant limits.</summary>
public static class LimsaRockTerrainProfile
{
    public const string Model15 = "bg/ffxiv/sea_s1/twn/s1t2/bgplate/0015.mdl";
    public const string Model15Hash = "fd01e79ba846e323787c5df604a1d3a233751e5e67a4f367b3291f44271218e8";

    public static TerrainContentProfile OptInKnownVanilla() => TerrainContentProfile.AssumeReviewedStaticBgTerrain(
        "DEV-limsa-s1t2-known-vanilla-rock-static-20261003-v2", new Dictionary<string, TerrainAssetProfile>
        {
            ["bg/ffxiv/sea_s1/twn/s1t2/bgplate/0014.mdl"] = new("6833786df868d5a4dccdd2a9bfc4618b0a2de564efcd32d6e255ea5b312811e4", 30),
            [Model15] = new(Model15Hash, 5),
            ["bg/ffxiv/sea_s1/twn/s1t2/bgplate/0023.mdl"] = new("78711c30accda284655b105cab0a9f21f1edbdbc3ac0baee7d6d237ed8ae31ea", 50),
        }, new Dictionary<string, string>
        {
            [TerrainContentProfile.StairMaterial] = TerrainContentProfile.MaterialHash,
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_ston1_h.mtrl"] = "91c18d6ae6dac1ebfcc92aedcdf8f5c440cf5a541fd5b9a9253fffe6f082e1ad",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_stpv2_h_bgs0.mtrl"] = "a72e6eeaae87fb8b07d2c427c7210eb2bfe3d2d3ccf09ef7155c676448a981d4",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_b0_sbrk1_h.mtrl"] = "021106397e8c02743a7ffaddc96ea9af7c762dd3af3036d8e36adbb80e22e013",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_grnd1a.mtrl"] = "bf4d5b38b429fdcc7f3bf0eeca6a3fddaec7af0cc3bf1de133c289984c852eaa",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_grnd1a_bgs0.mtrl"] = "7e7eed02c8bf5e9cd781c252abe8e946b630b0b999ac4d6efc6a93b8e633b7d6",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_wall1a.mtrl"] = "bbfdbb3bc8d6dadccf790a4807f51c8ec5b7f930b834bc2237f68a053fc659f5",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_wall2a.mtrl"] = "5fbe7ce5695a3d532bc67cdd4bd3d8ad0f9ecc2f077895ef9e36c4e5b0aa6c52",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_wall2a_bgs0.mtrl"] = "1c23a1bb542faab47d40baea64315819c6f47225df59e3a5edba9f514deccb6a",
            ["bg/ffxiv/sea_s1/twn/common/material/s1t0_t1_wall2a_bgs1.mtrl"] = "ab5caf48fba79cb7b960e05d5c41d31a133e63282be67fd0739cfa854890ac3f",
        });
}
