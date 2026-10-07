# XivRug — a personal woven map and magic carpet

Status as of 2026-10-03: continuous cloth is the default development renderer.
The current contact solver keeps the map continuous beneath the character,
without the earlier transparent foot cutout. XivPiano's optional played-audio
feature feed is live with a real 32-band spectrum; visual music tuning and
uneven-ground acceptance remain in progress.
The connected rug has been seen in-game on a flat bridge. Earlier tests showed
actual FFXIV map imagery, its background layer, and foliage occlusion in Western
La Noscea. Optional idle footwork records measured foot-contact patches.
Automatic resting squats have been retired, including migration of older saved
settings; standing was checked visually and in the runtime log. The fine weave and corrected warm binding are
also visible in-game. Jump lift still needs its own visual confirmation.
This remains a prototype
with further terrain, animation, and window-layering validation in progress.

The live development build uses the existing XivFloorMap registration. Native
composition is enabled in-game and is now the default for new configurations.
The Wine/DXVK test located the native UI shaders, submitted through the known
device's immediate context, and showed an overlapping native Journal covering
the rug. This is limited-scene evidence, not full layering/device certification.
Existing explicit opt-outs are preserved.

## In-game controls

`/rug` opens the development settings. Changes are saved.

`/rug idle on|off`, `/rug ride on|off`, and
`/rug catch on|off` control the same character and carpet options.
`/rug music on|off` controls the optional cloth ripples, separately from the
3-D visualization. Legacy `/rug squat` commands only disable the retired pose.

- **Actual game map on the rug** switches between cartography and textile-only
  rendering. It uses the game's current map, not a placeholder or whichever map
  the user is browsing.
- **Map coverage radius** defaults to 250 yalms around the character. This is
  map scale, separate from physical rug size. For a rounded rectangle it covers
  that distance along the shorter half-axis and preserves the aspect ratio.
- **Circular slack radius** defaults to 0.2 yalms. This tiny wake-up circle starts
  a glide that accelerates gently and catches up underneath a steady runner,
  rather than staying stretched behind. **Carpet glide time** defaults to 0.55
  seconds; longer values are more leisurely. Existing saved values are preserved.
  Moving flourishes add at most 0.09 yalms of lateral sway and a small directional
  gathering of the fringe; disabling fringe motion removes these flourishes.
  Teleports, zone changes and long frame gaps reset the anchor. The map view
  remains centered on the character independently of the rug's physical anchor.
- Shape, dimensions, edge fade, woven border/fringe, optional fringe motion and
  Wayfinder path visibility are separately configurable. Turn off fringe motion
  for reduced animation.
- **Let XivPiano music ripple through the cloth** defaults on. **Music flourish**
  adjusts its strength. Both cloth rendering and cloth/fringe motion must be
  enabled; reduced motion also stops musical ripples. XivPiano remains optional.
- **3-D music visualization above the map** is separately selectable: Spectrum
  Crown, Radial Ribbons, Spiral Fountain, Orbit Halo, Helix Canopy, Prism Bloom,
  Wave Dome, Star Fountain, Aurora Veil, and Resonance Arches. Aurora Veil forms
  two pleated vertical curtains; Resonance Arches forms sixteen crossed bridge
  ribs. Choose center-outward or outside-inward motion, height, and intensity.
  All ten use the actual played 32-band PCM spectrum,
  remain quiet without fresh audible data, and keep the rug's dimensions.
  They require cloth/native composition and respect reduced motion.
- **Lift the carpet to catch jumps** defaults on. The cloth rises toward the
  character's feet using measured ground height, then follows the descent and
  settles. It changes the rug's visual height; ordinary game jumping still
  controls the character.
- **Smooth the rug with my feet after 15 seconds idle** defaults on. A compatible
  local footwork clip starts after the character stays still for 15 seconds.
  Measured animated toe contacts press nearby wrinkles down, and the resulting
  small patches remain through the rest pose. The idle timer only schedules the
  animation; smoothing requires foot contacts. Moving or turning resets the
  sequence and clears those patches.
- **Ride the carpet (local standing
  pose)** is optional and defaults off. These visual poses yield to game actions
  and other character poses; normal movement controls and speed apply. Settings
  report animation state and unavailable clips. Smoothing waits for usable
  skeleton contacts.
- **Aged and well-worn rug** optionally adds adjustable faded dye and worn fibers.
  New and existing configurations stay fresh until enabled; aging preserves the
  map and route's readability.
- **Compose before native game UI** is recommended and defaults on for new
  configurations. An unavailable native stage hides
  the rug instead of falling back on top of the HUD. `/rug native on` and
  `/rug native off` change the same saved setting. Off selects a legacy
  diagnostic compositor that can overlap native game windows and HUD.
- **Retry renderer** retries session GPU failures without changing the saved
  enabled preference. The optional read-only navmesh diagnostic runs at 1 Hz.

XivRug does not move or teleport the character.

Origin-only terrain-cache handoffs can keep drawing the full-size current
footprint while the replacement warms. Both windows receive sampling work;
the new origin is promoted only after its current-center clearance check.
This does not freeze or stretch old mesh data, extend collision-cache lifetimes,
or shrink the rug. Zone changes, missing geometry, and unavailable fresh foot
tracking can still hide it. Continuous visibility on all terrain is not yet
guaranteed.

Terrain discovery also shares work between equally urgent support points and
cells, with bounded short continuations for multi-frame curb queries. Only one
class owns a continuation at a time; the other receives an opportunity afterward.
A skipped native
query is not treated as a changed floor when the adapter can prove no query
was issued. Actual misses still invalidate evidence, and deferred work does not
renew its lifetime. Measured floor faces encountered by clearance rays can join
the same connected layer; they do not bypass the complete footprint or wall
checks. The periodic support-outcome counts distinguish incomplete coverage
from player-floor, center-floor, clearance, and changed-evidence failures.

Source now includes an explicit measured-cloth role for upward rock bevels
that are too steep for player walking. A real bounded collision hit and a
finite shared edge are still required; ordinary player-floor reads stay
walkable-only. A bevel cannot also become a curb riser that skips a clearance
check. Discovery and refresh retain their existing query budgets, deadlines
and expiry, while every elevated path segment and complete material cell
still needs validation. This does not stretch cached geometry or shrink the rug.
Diagnostic captures use explicit v2 roles for this scope and still read valid
v1 captures; importing a report never authorizes live geometry.

The measured-bevel change is source-only, not live-accepted. Offline replay of
an actual rejected rock-edge ray now admits that face through discovery, but
the remaining unmeasured corridor still returns `Unknown`. Finite fixture
tests cover complete bevel paths, gaps, later-segment walls and expired
evidence. Continuous rock/stair coverage and visual quality remain unverified.

## Current renderer

The default path is `LiveRug` → `ClothContactSampler` → `ClothSurface` →
`ClothContactSolver` → `ClothGpu` → `ClothVertex.hlsl` / `ClothPixel.hlsl`. Collision support now uses
a rolling world-space lattice, separate from the moving material window. At
the default size, support points are 0.4 yalms apart; exact 3× subdivision adds
approximately 0.133-yalm visual spacing without inventing collision evidence.
Larger rugs explicitly coarsen support to bound work. Each new support point
still receives wall-compression and floor queries; each cell receives a floor
midpoint query after its corner revisions are current. Unknown support hides
the rug instead of using a flat anchor or translating old geometry.

The material window and fold phase follow the smooth follower each update,
without waiting for a replacement whole contact batch. Collision X/Z stays
world-anchored. Old and pending compression origins have independent provenance;
only a complete, fresh current window can render. The two-second cache lifetime
is a freshness assumption, not a guarantee about moving game geometry. Cells
retain their original diagonals when subdivided. Raised folds and a tension
pass spread material compression across neighboring fabric; the map bends with
the cloth. This is not a full cloth simulator or proof against every sub-cell
obstacle. Scene-depth rejection and final shared contact constraints remain independent.

Sampling retains a 2 ms deadline and now a 100-ray-per-update bound, including
actual-player floor, current-center floor and center-wall checks. One native
query can overrun its deadline; mesh construction is measured separately.
Once the required current-player and center checks succeed, an exhausted
optional-refresh deadline does not hide otherwise complete, fresh support.
Missing, expired or wrong-layer support still hides the rug.
Bounded diagnostics every 20 seconds count all updates, published and hidden
frames, deferred-refresh publications, ray calls, maximum update/build cost
and measured visible-center speed.
The rolling implementation needs live walking, corners and stairs verification
before claiming fully fluid movement.

`/rug motionprobe on` runs an optional 15-second, visual-only diagonal follow
target test. It changes no saved setting, player position/input, map center or
foot measurement. Actual movement over 0.05 yalms, a scene/zone change, combat
or an invalid clock ends it; `/rug motionprobe off` stops it immediately. The
test exercises real collision queries but does not substitute for walking.

Scene depth occludes the mesh against visible game geometry. The circular or
rounded outline clips only the outer edge. The continuous cloth renderer does
not cut holes around feet: it carries an explicit geometric ground bound through
subdivision, applies a persistent bounded vertical spring/contact solver, and
projects the final lifted/fluttering mesh back onto shared contact constraints.
Under a planted boot, cloth compresses to a thin 0.001-yalm layer above its
ground bound; small displaced folds gather and relax around the pressed area.
This is a lightweight cloth approximation, not a full volume-conserving cloth
or equipment-collision simulator.

The final render pose applies lift, hem flutter and shared contact constraints
once, then recomputes smooth area-weighted shared-vertex normals from those
positions. This smooths lighting, not geometry, ground bounds or collision
constraints. Hem and fringe use a bounded common-period clock, avoiding the
previous nonperiodic 256-second wrap.

Final constraints account for each actual triangle's horizontal diameter and
reduce neighboring triangle bounds onto shared vertices. This prevents raster
interpolation from raising cloth inside a contact and avoids tearing shared
edges. Current contacts expand for bounded travel since sampling, but they
flatten continuous geometry rather than erase pixels. The solver uses measured
contacts for pressure, separately from this render-time sweep. A ground/layer
generation change resets the solver; ordinary moving material windows retain
compatible state.

Complete, current-territory heel/toe poses still have a 1/30-second freshness
limit. Missing/unsupported poses and mounted characters hide the rug. Bone
footprints and 20-yalm/s endpoint motion bounds remain engineering assumptions,
not exact footwear silhouettes or an unconditional no-clipping guarantee.
Unknown/nonplanar cells still use conservative upper floor bounds; on their
lower side those bounds may sit above the local floor. Actual uneven-floor,
walking, equipment and jump acceptance is still required.

XivPiano integration uses the optional read-only `XivPiano.AudioFeatures.v1`
polling gate at no more than 20Hz. Actual played PCM amplitude, broad frequency
bands and rising-energy onsets create bounded traveling ripples and soft
flourishes. No audio, account data, track URL or playback controls are accessed
by XivRug. Pause, stale/missing features, wrong versions and reduced motion
stop new music forces; there is no fabricated beat timer. `/rug music on|off`
and the music-flourish slider control the response. XivPiano is not a required
assembly or dependency. Live IPC and onset delivery are verified; visual
music timing and flourish quality still need acceptance in-game.

The optional legacy floor-projection renderer retains its previous exclusion
behavior; these continuous contact changes apply to the default cloth path.
`NativeUiStage` and `NativeRugPipeline` provide the default experimental
pre-native-UI insertion path. The optional legacy background compositor draws
behind plugin windows, but does not guarantee placement behind native game UI.

`GameMapSource` reads current-map assets through Dalamud and combines foreground
and optional background using the same multiply composition as Mappy. It uses
the game's map scale and offsets and owns its render-time texture references.
It does not import or require Mappy. When map mode is enabled but current
textures are unavailable, the rug is hidden rather than showing an old map.

Cloth sampling confirms ground beneath the center before publishing a rug.
It first probes from only 0.12 yalms above the actual player root, rejecting
overhead hits above 0.06 yalms and floor more than one yalm below. Other samples
stay within a local band around that accepted floor and reject downward-facing
normals. Cached center geometry must agree with a fresh center-floor query.
Jump catch reuses this same accepted player support instead of making a broad
ray from above the character. Missing nearby support (including high jumps)
hides the rug and clears lift. This is a conservative unsupported-state policy,
not complete airborne-carpet behavior or universal stacked-floor detection.
It samples vertex contacts and cell midpoints. Actual collision faces choose
the player's locally reachable floor layer; connectivity to a remote upper deck
does not authorize that deck at the current sample. Proven planar cells preserve
their slope instead of lifting every corner to the highest one. Across face
seams, bounded geometric triangle-union coverage can establish the same plane;
matching corner heights alone cannot. Missing faces, holes, stale evidence or
an exhausted deadline retain the conservative cell bound. Nonplanar cells still
use their measured upper envelope rather than claiming complete terrain proof.
Horizontal collision rays clamp obstructed contacts and gather the cloth;
non-colliding brush and foliage do not enter these probes. The rolling support
cache, 100-ray cap and approximately 2 ms sampling deadline apply here too.
Only a complete, current window can render; unknown support hides the surface.

These finite probes cannot establish every surface between samples. Thin
obstacles, moving objects, steep transitions, stacked floors, and differences
between visible and collision geometry still need testing. Cell midpoints
improve ridge detection but cannot guarantee the maximum height everywhere in
a cell. Conservative nonplanar bounds may hover, and cached collision support
can temporarily disagree with moving geometry; scene depth still hides
occluded fragments. Loading and cutscene transitions discard active rendering.

The material uses a warm, narrow binding and gold stitches, fine procedural
warp/weft, filtered yarn bump detail, and soft fiber highlights. Derivative
filtering reduces distant and oblique weave shimmer. Map ink, optional faded
dye, and wear share the textile's shading. The mesh supplies real fold geometry;
its constrained contact/tension model does not yet simulate full cloth dynamics
or self-collision.

### Floor-atlas fallback

Setting `ClothSurface` to `false` retains the earlier `LiveRugGpu` →
`LiveRug.hlsl` floor-projection path. This is separate from the native-UI
composition setting. It reconstructs visible world positions from scene depth
and accepts pixels against collision triangles anchored by vnavmesh point
queries. `GeometryFloorAtlas` bins up to 1024 validated, deduplicated triangles
into 32×32 cells, with at most 16 candidates per cell. Overflow and unknown
regions are hidden; the 0.22-yalm height tolerance is not enlarged to fill gaps.

The atlas sampler retains its cap of 48 points and approximately 2 ms per
framework update, a world-space cache/halo, and managed geometry workers.
Its obstacle clearance field masks blocked areas and shades gathered edges.
This fallback can leave gaps where accepted floor coverage is incomplete. It
also lacks reliable floor/prop classification and a stable floor-layer identity.

## Optional Wayfinder integration

`XivWayfinder.v1.GetRoute` is read at 4 Hz without an assembly dependency.
Settings show the travel cue and route corner count. Stale, wrong-zone,
unavailable or changed routes clear the path; obsolete worker results are
rejected.

`RouteRibbonField` rasterizes up to 512 points into a bounded 129×129
distance/arc-length/height field on one managed worker. In map mode the route
uses the cartography's world-to-neighborhood projection. In textile-only mode
it follows local world positions and additionally checks route elevation.
The cloth route shares Wayfinder's trail tint and progress when visual metadata
is available, pulses gently, and adds small light pools at the guide and
look-ahead positions. Turning off rug motion freezes the pulse. Route markings
share the rug's depth and footprint visibility. Route planning and travel
actions remain Wayfinder's responsibility.

## Loaded collision-mesh decoder (not active)

`XivSurface.Core.Collision.PcbDecoder` is a portable, experimental decoder for
owned PCB version 1/4 bytes. It preserves full affine transforms, primitive and
effective materials, and per-triangle source offsets. Bounds cover bytes, nodes,
vertices, and primitives, including filtered-out geometry. Invalid or incomplete
declared graphs return no mesh. The decoder uses no native pointers, Dalamud,
Ghostty, or live-game calls.

`Complete` means only that the supplied declared graph was decoded under its
strict format assumptions. It does not prove complete scene coverage, the
player's reachable floor, safe native capture, or rendered stair geometry.
Header child counts currently exclude the root; this convention, quantization,
transforms, winding, and material filtering still need real-resource/native-hit
parity checks. Degenerate triangles and unsupported legacy or generated layouts
are refused rather than silently skipped. Synthetic corruption, material,
transform, bounds, ownership, and deterministic-mutation tests are included in
`tests/XivSurface.Core.Tests/Collision`.

Runtime integration still requires a verified resource data/size pair, a bounded
nonblocking capture at a valid framework lifecycle point, current scene identity,
and explicit handling of unsupported colliders or loading gaps. Decode owned
bytes outside the native lock. A nearby collision mesh alone is not permission
to render or simulate cloth on it; layer, footprint, wall, and foot constraints
remain necessary. Collision ramps also cannot replace the visible stair treads.

## Separate strict surface prototype

`SurfaceDecal`, `TriangleFloorMask`, `SurfaceGpuResources`, `SurfacePixelScope`
and `SurfaceDecal.hlsl` are a stricter reusable prototype, not the live rug's
rendering path. Their contracts require a positively classified floor, selected
layer/territory/geometry generation, and an attested before-game-UI frame.
Tests reject missing classification and late composition; those tests do not
prove that the live renderer supplies these guarantees.

`TriangleFloorMask` is a copied, bounded, linear-search CPU reference for
adapter-provided triangles, not per-pixel runtime IPC. Its separate compiled
shader and resource/binding helpers are not a certified production backend.
vnavmesh's public point/path/bitmap IPC must not be mistaken for a raw-triangle
or stable-layer-ID API. The fallback atlas uses validated game-collision
triangles anchored by the available point queries; the cloth sampler uses
game-collision contacts directly.

## Independent 3-D cloth core (not activated)

[`XivCloth.Core`](src/XivCloth.Core/README.md) is an experimental BCL-only indexed
XPBD library with independent tests. It supports material motion in all three
dimensions, separate stretch/shear compliance and signed-dihedral bending.
It has no Ghostty, Dalamud or plugin dependency. The plugin now references it
through a default-inactive physical publication/render branch; the running
development installation still uses the lattice/contact path above.

Synthetic regressions cover the numerical solver, thin-triangle contacts,
state rollback, bounded work and actual circular/rounded material topology.
The unactivated collision extension adds triangle/edge response and conservative
continuous finite-face checks for each linear substep at half the configured
response thickness. It refuses unresolved geometry or exhausted work budgets.
Immutable per-particle acceleration and compliant XYZ pull inputs now exercise
real material lag/deformation while retaining collision rollback and exact pins.
The combined physics, pattern, input, scene-rebinding, stair-response and
moving-foot suite passes 256 cases. Separate integration cases verify exact
simulated XYZ/UV transfer into the indexed renderer and the compositor's actual
publication adapter (identity/scene/observation invalidation and bounded age).
These CPU checks do not activate the runtime path or establish live
foot clearance, stair fidelity or performance.
An explicit paired planted-contact prototype additionally tests local thin
compression and recovery beneath approximate calibrated soles on horizontal
treads. It retains the complete material and other collision checks; it is not
general stair support or an enabled live visibility fix.
Prepared collision calculations reduce measured CPU cost by 23–30% in the
retained 81-particle, 128-face stair fixtures, with bitwise-identical tested
motion and unchanged collision budgets. The remaining 12–16 ms per 60-Hz call
is still above the few-millisecond target, before acquisition, rendering, or
dynamic-foot workload; this is not a completed runtime-performance claim.
Published endpoints must not be interpolated: their connecting chord is not
certified. Moving-foot capsule estimates now have whole-face response and
relative swept checks; these are not exact rendered footwear. Self-collision,
continuously swept solid-volume containment, verified visible-surface selection,
live foot/body integration and runtime integration remain
required. The library README records synthetic drapes, measured cost and precise
limitations; this does not change the live lattice/contact path.

The separate `XivSurface.RenderedGeometry` library preserves actual terrain
treads and vertical risers under an explicit reviewed-content profile. Its
stream distinguishes stale/incomplete observations and reports nearby material
omissions. The dev-only `/rug inspectterrain` command runs a bounded ten-second
native-inventory/archive diagnostic; it does not switch the rug to physical
cloth, change settings or move the character. The initial profile is a scoped
Limsa fixture, not generic support for every floor or a loaded-mod identity
guarantee. See `tools/XivSurface.TerrainProbe/README.txt` for reproducible source
and installed-data gates.

## Still required before stable release

- Validate coverage during continuous movement, on slopes, stairs, cliffs and
  stacked floors; improve missing geometry discovery where needed. Check the
  connected mesh around thin props and walls, including publication smoothing.
- Confirm jump lift/catch, the 15-second footwork sequence, measured local
  smoothing and optional ride visuals in-game across compatible
  character models and interruptions.
- Extend the successful Journal overlap test to native HUD, plugin windows and
  fullscreen terminals. Exercise Wine/DXVK, camera/depth
  synchronization, dynamic resolution, device resets and frame cost.
- Establish reliable floor/actor/prop classification and layer selection.
  Depth normals alone remain insufficient, even with height checks.
- Expand map features such as dynamic POIs, quest markers, interactive label
  overlays and floor selection. Actual map textures and UV transforms already
  work; a complete interactive map or transport graph is not implemented here.
- Finish packaging/release and regression checks. Extend the current contact
  and tension model if more complete physical cloth behavior is needed.

## Build and tests

```sh
dotnet test tests/XivCloth.Core.Tests/XivCloth.Core.Tests.csproj -c Release
dotnet test tests/XivCloth.Owner.Tests/XivCloth.Owner.Tests.csproj -c Release
dotnet test tests/XivCloth.Rendering.Tests/XivCloth.Rendering.Tests.csproj -c Release
dotnet test tests/XivSurface.Core.Tests/XivSurface.Core.Tests.csproj
dotnet test tests/XivRug.Terrain.Tests/XivRug.Terrain.Tests.csproj -c Release -p:DalamudLibPath=/path/to/reviewed/dalamud/
dotnet test tests/XivRug.Configuration.Tests/XivRug.Configuration.Tests.csproj -p:DalamudLibPath=/path/to/dalamud/
dotnet build src/XivRug.Plugin/XivRug.Plugin.csproj -p:DalamudLibPath=/path/to/dalamud/
```

The dependency-free offline cloth inspector draws full indexed solver snapshots
as SVG: true-scale orthographic geometry plus an explicitly exaggerated height
profile. It includes support heights and labeled foot proxies, compressed
vertices and transition faces; it never interpolates endpoints or changes the
material. Its JSON contract is the `Snapshot` record in
`tools/XivCloth.Inspect/Program.cs`. Export only genuinely accepted solver
captures, retaining their case/step evidence alongside the JSON.

```sh
dotnet run --project tools/XivCloth.Inspect/ClothView.csproj -c Release -- --self-test
dotnet run --project tools/XivCloth.Inspect/ClothView.csproj -c Release -- snapshot.json new-preview.svg
```

Inputs are capped at 1 MiB, 512 vertices and 1,024 full material faces. Output
must be a new file; existing files are never overwritten. The viewer checks
finite geometry and topology, not capture provenance or collision correctness.
The caller's `Ready` status and source-manifest label are not authentication.
These are offline projections, not in-game screenshots or visual acceptance.
The tool is not included in the game plugin and performs no native calls.

The optional integration suite uses the real XivPiano PCM analyzer and the rug
consumer together. It is not required for the default build and adds no runtime
dependency. Supply the producer project explicitly:

```sh
dotnet test tests/XivRug.Piano.Integration.Tests/XivRug.Piano.Integration.Tests.csproj -p:PianoCoreProject=/path/to/xivpiano-dalamud/src/XivPiano.Core/XivPiano.Core.csproj
```

It tests played versus queued audio, late subscription, pause/resume, provider
reload, stale playback and quiet-audio thresholds without network or account
access. Physical cloth tests separately measure solved ripple height, propagation,
release, contact constraints and bounded overlapping impulses.

For the existing XivFloorMap dev registration and persisted configuration type:

```sh
dotnet build src/XivRug.Plugin/XivRug.Plugin.csproj -p:RugCompatibilityName=XivFloorMap -p:DalamudLibPath=/path/to/dalamud/
```

Native composition is selected at runtime and enabled for new configurations;
saved explicit opt-outs are not migrated. After HLSL edits:

```sh
bash tools/build-shaders.sh
```

That script also builds the cloth pair; `bash tools/build-cloth-shaders.sh`
rebuilds only `ClothVertex.hlsl` and `ClothPixel.hlsl`.

Compiled DXBC is embedded for ordinary builds. Shader rebuilding requires
Podman/network access; the script pins `vkd3d-compiler-1.17-2.fc44`, while its
Fedora 44 image tag is not an immutable image digest.

Core test coverage includes projection, footprints, map-coordinate transforms,
route rasterization, triangle coverage
and overflow rejection, circular dragging/reset behavior, strict prototype
acceptance and shader constant-buffer layouts. Cloth tests cover shared mesh
topology, floor clearance, tension, localized pressure, presentation blending,
foot clearance, jump lift, and idle sequencing. These are CPU/ABI checks; live
GPU rendering, animation, and all-scene occlusion require in-game validation.

The previously installed foot-safety build (2026-10-02, before temporal mask
expansion) passed 211 core tests and 21 configuration tests
and produced a Release plugin with no warnings/errors. Both renderers reuse the
previously rebuilt shaders with unchanged constant-buffer layouts. The added
regressions cover lower-stair pose loss, the admitted extended-foot envelope,
immutable publication, incomplete guards, and stale/wrong-zone snapshots.
Its live reload reported four measured foot contacts and native cloth submission;
the rug and the character's standing boots are visible after startup warm-up.
Walking, stairs, mount transitions, low-frame-rate behavior, and equipment-specific
clearance still need live verification. That build's former 100 ms age bound was not proof of
same-animation-frame bone alignment. Installation preserved settings and the
development manifest; this is not a stable-release certification.

The newer bounded-motion mask source passed 227 core tests and 21 configuration
tests, and its Release XivFloorMap compatibility build completed without warnings
or errors. Core regressions include lateral
boot-edge and descending-foot containment at 8, 16 and 33 ms under the declared
speed bounds, unchanged measured contacts, cutoff/invalid-contact failures, and GPU
packing. This is CPU policy validation, not proof of same-pose rendering.
The matching development bundle was loaded on 2026-10-03 at 07:00 UTC with
settings and manifest unchanged and the prior binaries backed up. Logs reported
four measured foot contacts and a 2,048-triangle native cloth submission. After
startup warm-up, a screenshot shows the rug beneath the idle character; walking,
stairs, fast pose changes and frame-rate stress remain unverified live.

The follow-up solid-footprint correction passed 231 core tests and 21
configuration tests, with a warning/error-free Release compatibility build.
Its regressions demonstrate the former half-opacity leak at 85% of the estimated
boot radius, full-footprint containment at 0/8/16/33 ms, and a preserved soft
feather outside that footprint. The matching bundle was reloaded at 07:22 UTC
on 2026-10-03, preserving settings and manifest and backing up the prior bundle.
Logs confirmed four measured contacts and native cloth submission. The live
screenshot shows the rug beneath the resting character; the squat and companion
obscure the boots, so this is not a walking or equipment-clearance validation.
A single 82.5 ms cold-start update hitch was logged during reload.

The scale-envelope follow-up passed 249 core tests and 21 configuration tests
(270 total); its frozen-source Release compatibility build had no warnings or
errors and used copies of the installed API references. New regressions cover
each dominant skeleton axis, unchanged ordinary uniform scaling, rejection of
invalid axes and oversized estimates, the exact footprint-budget boundary, and
suppressed rendering after a rejected boot. The matching bundle was reloaded
at 07:59 UTC on 2026-10-03, with settings and manifest unchanged and prior
binaries retained in `/home/player/.xlcore/rug-foot-scale.wafq2X/before` inside
the game container. Logs confirmed four contacts and a 2,048-triangle native
cloth submission; the screenshot `/tmp/rug-foot-scale-live.png` shows both
standing boots unobscured by the carpet. This is one standing-frame check,
not walking/stair/equipment acceptance. A 91.5 ms cold-start update hitch was
logged during reload. The frozen source, references and verification log are
under `/tmp/rug-foot-scale-final.Fbzfan` on the build host. No shader layout,
character transform or saved preference changed as part of this correction.

The subsequent cadence build passed 263 core tests and 21 configuration tests
(284 total), with a warning/error-free frozen-source Release compatibility
build. Simulated steady motion at 60 Hz with 100 ms batch publications reduced
speed variation from 2.23–11.80 to 4.76–6.89 yalms/s; 30/60/120 Hz regressions
also check bounded stop/reversal/corner behavior. This is simulation, not a live
walking test. The matching bundle was reloaded at 08:18 UTC on 2026-10-03 with
settings/manifest unchanged, four foot contacts and native cloth submission.
The prior bundle remains at `/home/player/.xlcore/rug-fluid-cadence.bnLoK8/before`;
the frozen build is `/tmp/rug-fluid-cadence.aEtNFY` on the build host.

Live stationary Limsa measurements revealed a larger remaining bottleneck:
warm batches required 32–33 framework frames, 995–1,093 ms and 3,202 raycasts;
mesh construction took 0.47–1.55 ms. Maximum query slices were 3.60–5.70 ms:
the deadline is checked between operations, so an individual query or scheduling
delay can exceed 2 ms. Initial load logged a 75.5 ms update hitch and a 13.2 ms
cold mesh build. Removing the item cap alone does not solve this latency.
Moving support-cache/material-window work remains necessary; no running,
stair or full slithering-motion acceptance is claimed for this build.

The foot draw-timing build passed 272 core and 21 configuration tests (293 total)
and a warning/error-free frozen-source Release build. Its diagnostic overload
retains the exact previous draw gate and clears rejected output; tests verify
all rejection reasons and distinguish missing tracking from simple expiry.
The bundle was reloaded at 08:33 UTC on 2026-10-03 with settings and manifest
unchanged, four measured foot contacts and native cloth submission. The previous
bundle is retained at `/home/player/.xlcore/rug-foot-timing.318NS8/before` in the
container; frozen build evidence is `/tmp/rug-foot-timing.4U08c3` on the host.
This adds measurement, not a stronger equipment-silhouette guarantee or a claim
that walking and stairs have been visually validated.
Initial stationary 20-second windows recorded 709 accepted/7 expired and
638 accepted/21 expired draw gates, then varied with load; no incomplete or
other rejection was reported in those windows. Thus expiry is an additional
intermittent hiding cause, not an explanation for all batch-driven motion delay.
That original check was 33.33 ms; the newer cloth cadence policy below replaces
that fixed limit for continuous cloth. The screenshot `/tmp/rug-foot-timing-live.png`
shows the map rug and squatting character, but the pose obscures the boots and
does not establish walking clearance.

The separate configuration suite passed 21 tests using the real Dalamud
configuration interface and Newtonsoft serializer. It checks new/missing-value
defaults, explicit native-composition opt-outs, tiny drag settings, sanitization
and serialization round-trips for both XivRug and the XivFloorMap compatibility
type. These checks do not load the game or initialize the renderer.

## Independence and references

`XivSurface.Core` is BCL-only. No Ghostty assembly, plugin, agent, loader or IPC
is required, and neither Mappy nor Wayfinder is required to load XivRug.
`SceneDepthReader` adapts Ghostty source-level depth access; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Reusing that access does not
provide native-UI ordering or floor classification.

- [vnavmesh IPC](https://github.com/awgil/ffxiv_navmesh/blob/master/vnavmesh/IPCProvider.cs)
- [HLSL matrix multiplication](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-mul)
- [FFXIV render targets](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs)
- [Dalamud UI builder](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Interface/UiBuilder.cs)

## Personal mat and obstacle edges

New defaults use a circle with a two-yalm radius, or a four-by-three-yalm rounded
rectangle, with a narrow woven border so the map remains readable. The live
development configuration was reduced to the circle; existing installations
keep their chosen size.

The default cloth mesh gathers around detected solid obstacles while retaining
its connected interior. Its raised folds, local foot smoothing, and optional
jump lift give the map a physical place beneath and around the character. Vegetation with
solid collision participates in contact sampling; ordinary non-colliding foliage
can still visually occlude the material through scene depth. See the sampling
limits above for unsupported edges and geometry between probes.

### Rock draping and continuous presence

The sampler follows measured floor portals when a straight clearance ray enters
a convex rock. Every resulting segment still receives a wall check. Missing
pieces of a cell or path now request further collision discovery. Shared-corner
ties and floating-point clipping fragments no longer stall otherwise complete
rock coverage. A verified central patch can display the entire rug material
while its outer edge is being sampled.

Curved cells use a measured residual above both possible contact-triangle
diagonals instead of raising all four corners to the cell's highest point.
This follows slopes closely while bounding measured intermediate peaks. The
current rock's maximum added support height fell from 0.505 to 0.098 yalms.
Collision meshes can differ from visible models; this is not a guarantee about
unobserved geometry or a replacement for the separate full 3-D cloth solver.

Jump support tracks the previously reached ground layer using a fresh collision
query at the current character position on every update. It does not substitute
a saved floor height or probe down from above an unrelated bridge. The takeoff
window and travel remain bounded. Cloth foot publications adapt to measured
capture cadence, with a 100 ms maximum age and the existing swept foot contact
constraints; legacy floor projection retains its 33.33 ms limit.

### Lighting and idle care

Cloth dye, binding and sheen now use the current environment's sun, moon and
ambient colors. Sky bounce and celestial direction are approximations; the
native game's shadow maps are not sampled. Three small depth-tested fairy
wisps appear in dark lighting and cast restrained pools onto the rug.

After 15 seconds idle, nearby raised folds can produce short visual excursions:
walk to a supported fold, press it down, return, and rest in the configured squat.
The planner uses the current cloth support and rechecks the displayed feet.
Only the local drawing offset, drawing rotation and animation are leased;
gameplay position and input are unchanged. Movement, lost support and foreign
overrides end the excursion and restore values still owned by the rug.

## Public build and publication

Source: https://github.com/Spaceghost/xivrug-dalamud. Site: https://spacegho.st/mods/ffxiv/xivrug/. MIT licensed.

With Python 3 and .NET SDK 10.0.401 installed, run `tools/fetch-dalamud.sh`, `tools/build.sh --test --locked`, then `tools/package.sh`. For local references pass `--references /path/to/reviewed/dalamud`. Reviewed Dalamud 15.0.3.6 hashes are pinned in `tools/references.json`; changing native bindings requires review. NuGet dependencies have committed lock files. ZIPs and checksums use deterministic ordering/timestamps and record source/reference identity. CI runs the same commands and uploads artifacts; the manual release workflow updates the experimental `testing` channel from `main`. Optional Piano cross-repository integration tests remain separate.

Add `https://spacegho.st/mods/ffxiv/plugins.json` in Dalamud Experimental settings and enable testing builds. This publishes XivRug once, not a second XivFloorMap installer identity. Existing FloorMap development installs should be disabled before loading Rug; retain their settings. The compatibility build remains in the source for existing workflows.

Read-only MCP diagnostics use `XivRug.v1.ApiVersion` (`Func<int>`, version 1) and `XivRug.v1.GetStatus` (`Func<string>`, cached JSON). Status includes observation time, login/enabled state, territory, renderer/native composition, map/music/terrain diagnostics and `movementControl=false`. MCP probes Rug/FloorMap installation aliases and reads this snapshot without loading terrain or changing settings.

This publication and new status IPC have offline validation. Earlier live records above do not constitute live acceptance of the new package. No new screenshots were captured; the game container was stopped.
