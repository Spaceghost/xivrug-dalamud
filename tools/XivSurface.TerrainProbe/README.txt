Reproducible bounded terrain-candidate gate (no live game API)

From repository root, with .NET10 installed:
  dotnet run --project tools/XivSurface.TerrainProbe -c Release \
    -p:DalamudLibPath=/path/to/reviewed/dalamud/reference/directory/

This runs the frozen53 parser cases plus43 synthetic orchestration/profile tests
and8 synthetic rock-profile cases. No proprietary geometry is in these fixtures.
The library build checks the
exact installed Lumina.dll SHA256. Missing/wrong references fail before compile;
do not override the hash gate merely to accept another DLL version.

The plugin archive-admission and report-clock policy has a separate reproducible
13-case test target (no live game API):
  dotnet test tests/XivRug.Terrain.Tests/XivRug.Terrain.Tests.csproj -c Release \
    -p:DalamudLibPath=/path/to/reviewed/dalamud/reference/directory/

Optional read-only installed-data fixture and 120 warmed rebuild cost samples:
  dotnet run --project tools/XivSurface.TerrainProbe -c Release --no-build -- \
    --actual /path/to/ffxiv/game/sqpack

Use nice19 and one CPU on a busy game host. No model/shader assets are exported,
downloaded or included. The fixture uses the two previously observed s1t2 tiles
0014/0023 at known translations, not the player's current location. It is a
separate installed-data assertion, not synthetic evidence or a live game test.

Expected:128 scoped triangles,84 horizontal+44 vertical, both tiles, multiple
0.15-yalm steps. Four triangles of the broader132-triangle region are excluded
because their material has a non-default key not reviewed by this profile.
The gate also checks that all four omissions are explicitly reported in every
cold/warm batch; a reviewed subset must not masquerade as complete local ground.
The warm test alternates the interest center by0.0001y to actually rebuild.
Reports cold allocation/time, warm request-to-publication (including scheduling)
and worker assembly time. An unchanged complete observation instead reuses the
exact prior immutable batch, as synthetic tests explicitly verify.

Optional provenance-only material digest read:
  dotnet run --project tools/XivSurface.TerrainProbe -c Release --no-build -- \
    --material-hashes /path/to/ffxiv/game/sqpack

The tool's Lumina archive reader checks the reconstructed length AFTER Lumina
allocation; installed files are trusted fixture data. Production readers must
bound size earlier and provide replacement revision/lifecycle observations.

Optional separately selected installed-data rock profile:
  dotnet run --project tools/XivSurface.TerrainProbe -c Release --no-build -- \
    --actual-rock /path/to/ffxiv/game/sqpack

Expected: three complete fixture batches,154rock faces/0omitted,122faces/0omitted
in the narrower box,128old-stair faces/4explicitly omitted. The exact rock point
XZ(-65.33346,-17.446589) yieldsY18.98386 on tile0014mesh1triangle402; tile0015 is
also retained across the nearby boundary. These are fixed copied descriptors,
not current player/native observations or runtime scene authorization. No assets
are exported.154faces exceeds the current physical solver's128-face bound and
must NOT be truncated. A smaller runtime domain is valid only when it contains
the whole current/proposed swept cloth and body contacts plus required margin.
See src/XivSurface.RenderedGeometry/REVIEWED_LIMSA_ROCK.txt for material evidence
and the preserved unknown loaded-replacement/effect/LOD limits.
