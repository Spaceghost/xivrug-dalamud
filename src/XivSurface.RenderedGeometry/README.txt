XivSurface.RenderedGeometry (experimental, opt-in candidate geometry)

Independent managed library: reconstructed MDL bytes -> owned indexed LOD0
geometry -> bounded local immutable terrain triangle candidates. No native
pointers, hooks, game input, Ghostty dependency, or plugin/default activation.
Lumina.dll comes from the user's installed reviewed API15 references, not a
bundled game asset. Its SHA256/layout is pinned by the project build gate.

RenderedMdlTriangles.Extract remains strict and unchanged (53-case baseline).
ProfiledTerrainLoader uses the separate inspection decoder ONLY after matching
the explicitly selected model/material/bg.shpk hashes. Terrain submesh +8 is
interpreted as a bounded culling-grid index, never an attribute bitmask, and W
is preserved without homogeneous division. Review found XYZ unchanged in all
56 bg vertex shaders except separate COLOR1-driven waving; parser rejects
waving-enabled models, skinning, shapes and non-main mesh categories.

Assumptions are visible in every batch: AssumedKnownVanilla,
LoadedContentVerified=false, AuthorizesWorldSupport=false,
CoversAllSceneMaterials=false. These are useful dev/candidate colliders but
cannot themselves opt a live caller into physics or claim matching loaded
replacement bytes or the actual selected shader/LOD. Root application must
decide explicit activation separately. No default profile is installed.

OptInKnownVanillaStairs() is a DEV FIXTURE, not a location-independent runtime
default. It pins two previously observed Limsa terrain tiles, the exact bg.shpk,
and three default-key opaque stair/landing materials. The local fixture keeps
128 triangles, including 44 vertical risers; four nearby triangles using an
unreviewed material key are intentionally excluded. No MDL/MTRL/SHPK files are
shipped. AssumeReviewedStaticBgTerrain registers other locally REVIEWED profiles
(<=256 model pins, <=128 material pins), copies the dictionaries, and hashes the
entire definition. Registration is not automatic model discovery or remote
metadata trust. Intended static/opaque semantics of new material pins must be
reviewed; their hash alone does not invent that evidence. Non-default material
keys require exact selector/alias and shader-variant review, not an inference
from a material name or its no-transparency flag.

LimsaRockTerrainProfile.OptInKnownVanilla() is a SEPARATE explicit DEV fixture.
It retains the original pins and adds tile0015 and seven reviewed rock/ground
materials. The original stairs factory and plugin diagnostic selection remain
unchanged. The exact38-shader review, omitted-material policy and current rock
fixture are documented in REVIEWED_LIMSA_ROCK.txt. No game assets are shipped;
using this profile never claims live scene/subview/effect/LOD or loaded-content
proof and does not by itself authorize physical cloth activation.

Each batch now reports UnreviewedMaterialTriangles: omitted faces whose
transformed bounding boxes intersect its interest box. The installed fixture
reports four. This conservative local omission count is immutable; it is
recomputed when interest changes, not hidden by filtering the output mesh.
Zero does NOT establish full-world coverage, exact loaded bytes or support.

Runtime adapter seam
--------------------
On framework thread copy existing terrain inventory fields into TerrainInstance:
instance key, observed resource ID/path, world matrix, local bounds and static
eligibility. This first adapter admits translation-only transforms, not unknown
parent/nonuniform transforms. Resource ID is an observation key, NOT a hash.
Pass CompleteTerrainPlates only from terrain-scoped traversal. A partial mixed
terrain/BgPart capture must remain Incomplete. This still means only traversal
completion, not all missing/rejected scene material is known: batches expressly
cover only profile-eligible OBSERVED instances/materials. Refuse or separately
flag ambiguous nearby native-resource rejections before a live collision use.
The plugin inventory distinguishes UnresolvedTerrain from OutsideInterestTerrain;
invalid/unloaded resources cannot simply be counted as far-away exclusions.
An unknown parent transform also cannot establish an outside exclusion.

Request(zone, observationGeneration, instances, completeness, interest,
        capturedSeconds, profile, providerRevision)
Poll() -> phase, batch, workerPending, observationGeneration/capturedSeconds
state.CandidateAt(zone, now) -> only the latest polled batch while its sample
is <=.25s old (configurable up to .5s); future/invalid/wrong-zone times reject.
Use the SAME monotonic seconds origin as inventory. Observation generations
must increase for a stream lifetime. Do not mislabel request time as bone/GPU
frame evidence. Call Poll again before use after lifecycle invalidation.

Fresh COMPLETE observations with exactly the same descriptor set, transforms,
interest, profile and replacement revision renew observation metadata without
clearing/rebuilding the immutable batch or its geometry generation. Changed,
missing, stale or incomplete observations clear immediately. Merely refreshing
capture generation cannot starve a cold same-content archive read. A fixed
local interest region can remain stable while the player moves inside it;
do not force unnecessary per-frame geometry generation by microscopic centers.

Known replacement providers must pass a changing revision and call Invalidate
on changes/unavailability/reload. Provider change and resource-ID change reload
the model; explicit Invalidate also drops the cache epoch when no revision is
available. Unknown providers remain the explicit vanilla assumption, never
silent proof that no mods exist. Zone/layout teardown invalidates immediately.

One worker and one latest pending request; <=4 parsed-model cache entries;
<=8192 published local triangles; interest half extents <=8y; model <=32MiB,
<=131072 source triangles /262144 vertices; shader <=96MiB; MTRL <=64KiB.
Uncooperative IO/cancellation callbacks occupy the only slot rather than causing
abandoned-worker fanout. Request/Poll do no file IO, hashing, native calls or
synchronous waits. Dispose cancels without waiting. All-or-nothing publication:
errors/budget overrun never silently yield a truncated batch. Identical failed
captures do not repeat IO every frame; explicit Invalidate enables retry.

ITerrainAssetReader is called only on the worker and must enforce maximumBytes
before allocating untrusted data. The optional diagnostic tool reads trusted
installed SqPack via Lumina, which reconstructs before reporting length; it is
NOT a hardened reader for hostile archives or a production loaded-resource
resolver. No production source reader is wired here.

Tests and actual-data/cost commands: tools/XivSurface.TerrainProbe/README.txt.
