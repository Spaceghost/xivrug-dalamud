# XivCloth.Core

Experimental, independent indexed 3-D cloth simulation targeting .NET 10. The
library uses only the BCL; it has no Ghostty, Dalamud, FFXIV, graphics API, or
plugin dependency. The plugin compiles a default-inactive physical publication
and indexed-render branch, but no runtime controller currently calls it. It is
**not activated in the live rug renderer**. The existing live cloth, foot
protection, collision queries, settings, and rendering remain unchanged.

## Model and API

`XpbdDefinition` copies particle rest positions, UVs, triangle indices, inverse
masses and explicit material edges. Positions move in all three dimensions;
there is no fixed-X/Z lattice or `GroundMinimumY` projection. Zero inverse mass
pins a particle to its initial or explicitly reset pose. Stretch and shear use
separate compliant distance constraints; shared triangle edges carry signed
dihedral bending constraints with analytic gradients.

`XpbdCloth.Advance(elapsedSeconds, scene, inputs: null)` runs fixed
1/120-second substeps.
It returns status, work counters and deliberately dropped elapsed time.
Multipliers accumulate across iterations and reset each substep. Failed
advances restore the entire call's positions and velocities and preserve the
prior fractional clock and geometry generation. `Capture()` returns an owned,
immutable indexed frame with recalculated normals and a scene-generation marker.
`EndpointInterpolationAllowed` is always false: accepted substeps are individually
linear and swept, but the chord from an earlier publication to the final endpoint
is not certified. Future smoothing needs the actual proven substep trajectory
and matching render alpha. `Reset()` discards velocity,
clock and scene association; it does not silently establish collision safety.
Instances are mutable, single-owner simulations, not thread-safe services.

`MeasuredTriangleScene` copies explicitly supplied finite triangles. Its
positive generation is assigned by the caller and must change when geometry
changes; ordinary `Advance` refuses a different generation until explicit
`RebindScene` admission or reset.
The type validates shape, **not** provenance, freshness, floor layer, winding
semantics, or suitability of a game model. Winding must point toward the chosen
free space under the default one-sided support policy. Explicit `twoSided: true`
selects finite thin sheets, not automatic layer or closed-solid classification.
`Ready` means the bounded numerical step and supplied-face checks were accepted;
it does not authorize the completeness or provenance of that scene. An advance
shorter than one substep may publish no new simulation work.

`RebindScene(scene)` checks the entire unchanged current cloth against the new
static finite faces and one-sided support policy, with the existing bounded
collision budget. Successful admission replaces only the scene association:
momentum, material positions and fractional time survive local terrain-window
refreshes. Failure preserves all state and the old association. This does not
sweep an obstacle moving between snapshots, establish scene completeness, or
authorize a new zone/floor layer. Those transitions remain caller responsibilities.

An explicit `MeasuredTriangleScene` overload accepts a `CollisionCoverage` box.
It bounds point responses, full-face queries, padded cache refills and complete
linear swept hulls. Leaving that captured volume is unknown collision support,
not an empty scene or permission to clamp the cloth. The box does not establish
that a game-world provider actually captured every relevant collider. The
original constructor and its unbounded behavior remain available unchanged.

Explicit `RebindScene` and `AdvanceMeasuredFeet` overloads also accept a
synchronous, read-only `canCommit` predicate. A false or throwing predicate
refuses the complete operation without changing accepted positions, velocities,
foot history, clock or generation. Reentrant mutation and exporting tentative
positions are rejected. The original CLR signatures and optional source defaults
remain intact for existing callers.

The separate `PhysicalClothRuntime` adapter now exercises the owner sequence
against complete finite **test fixtures only**: immutable, expiring scene leases,
every consecutive early/late foot observation, exact indexed publication, and a
final lease/publication identity check immediately before submission. Scene
refreshes keep the full material pattern and its UVs. This adapter is not compiled
into or activated by the plugin. It cannot recover from skipped foot history;
that currently requires a new owner and full admission. Zero-gap planted feet
still refuse ordinary positive-thickness cloth. The explicitly named paired
model below is a separate prototype, not a silent relaxation of that contract.
No runtime performance claim is made.

The compliant constraint equation follows [Macklin, Müller and Chentanez,
XPBD (2016), Algorithm 1 and equations 17–18](https://mmacklin.com/xpbd.pdf).
The gradients are derived directly by reverse chain rule, not copied from a
solver implementation. Exponential external drag is not the paper's Rayleigh
damping term; finite iterative convergence does not imply exact stiffness
invariance for arbitrary interacting constraints.

## Bounds and current limitations

- At most 512 particles, 3,072 indices, 3,072 material edges, and 128 supplied
  collider triangles; at most 16 iterations and eight substeps per call.
- A conservative two-million-work-unit preflight refuses excessive work before
  state mutation. Collision pair, BVH-node and interval-node work shares a second
  hard budget: `CollisionWorkLimit` defaults to 100,000 and permits at most
  500,000 units. Exhaustion refuses the entire advance, including already solved
  substeps. These counts are not a two-millisecond wall-clock guarantee.
  Excess accumulated time beyond eight substeps is reported and dropped, not
  saved as an unbounded debt.
- Simulation scratch is reused; the warmed 25-particle regression allocates
  zero managed bytes over 100 fixed steps. Immutable `Capture()` intentionally
  allocates copied positions, normals, UVs and indices.
- Contact response uses vertex/face, edge/edge and triangle-interior witnesses
  with mass-weighted 3-D corrections. A bounded immutable BVH prunes finite-face
  response and sweep candidates. Floating-point response heuristics are not the
  acceptance proof; initially intersecting/buried or unresolved states refuse
  rather than selecting an invented safe side.
- A deep point/face candidate receives no arbitrary depenetration. Later shallow
  contacts may move it out of an adjacent riser's footprint; a bounded, counted
  rescan rejects any remaining deep overlap. A failed finite-face sweep can
  supply a temporal closest-feature response hint, with at most four repairs,
  at most 0.025 distance units per particle per repair and 0.05 accumulated per
  substep. Every corrected proposal repeats the independent point, whole-face
  support and complete swept-path checks. A hint never authorizes publication;
  uncertainty or budget exhaustion still rolls back the entire advance.
- Independent continuous acceptance encloses each linearly moving triangle in
  the convex hull of its six endpoint positions. Outward-rounded interval
  support bounds must prove separation over every time slice. At depth 12,
  unresolved intervals return `CollisionUnproven`, never a guessed clear result.
  The certificate is against supplied **static finite faces at half `Thickness`**
  (default 0.002 units); response targets full `Thickness` (default 0.004).
  It is not exact time of impact, arbitrary curved-motion CCD, or self-collision.
- Default one-sided admission and endpoint checks additionally clip against a
  triangle's extruded footprint. That support prism is **not continuously swept**.
  Lateral travel below a finite face can remain face-clear while entering its
  prism between endpoints; an explicit regression preserves this limitation.
  Real solid stairs need actual risers/appropriate boundaries. An upper deck can
  still reject a valid lower-layer pose; callers own layer and resource identity.
- There is no friction, cloth self-collision, moving-collider synchronization,
  exact TOI, or robust moving-pin policy. `MaximumVertexPenetration` remains the
  legacy one-sided vertex metric, not a whole-face/two-sided certificate.
- Moving pins, dynamic topology, physical material calibration, active
  foot/body constraints, and a live runtime adapter are not implemented. An
  independent final-pose upload path is tested separately but not activated.
  Close/grazing cases can conservatively freeze, and large soups can exhaust
  the count budget. No stable production or live stair-conformance claim is made.

Synthetic four-tread 0.15-unit stairs with 128 measured tread/riser triangles now
advance all four circle/rounded 9x9 cases through 180 frames at the unchanged
default 100,000 work budget. Per-call candidate caching reduces repeated BVH
traversals; it changes neither the actual sorted obstacle candidates nor any
response, one-sided endpoint, continuous terrain or final moving-foot proof.
This is a bounded synthetic regression, not a realtime claim.
Without friction or pins, free material can slide off the finite landing;
successful motion does not imply that an unsupported cloth remains on stairs.
Separate versions with two upper pins keep every vertex within the actual
support footprint and above its tread while continuing to bend and descend for
the same 180 frames. They retain multiple near-floor contacts and exact pins;
this does not claim calibrated stiffness, settled equilibrium or visual quality.

Candidate reuse requires the exact immutable scene identity and complete
containment of the current outward-rounded query box in a stored envelope.
Every stored candidate is re-filtered against the actual query; all containment,
initialization, refill and filtering work is charged. Exhausted partial refills
cannot be reused. Caches reset for each Advance/Rebind call, preserving exact
retry work after failure. No accepted collision verdict is cached. The bounded
constructor storage is 540 bytes per cloth triangle plus array overhead (about
67.5 KiB for128 faces, 540 KiB at1024); the scene retains24 bytes per obstacle
box. Integration retains zero per-call managed allocation, excluding Capture.

Prepared immutable triangle coefficients and support intervals now avoid
repeated static calculations. Conservative response separation and duplicate
clipping-work removal preserve the tested trajectories and work counters;
the independent terrain and moving-foot proofs remain unchanged. In four
retained 81-particle, 128-face stair fixtures, executing-thread CPU time fell
23–30%, from 16.8–21.1 ms to 12.4–15.8 ms per 60-Hz call (two substeps).
This excludes terrain acquisition, rendering, and dynamic-foot workload. It
still exceeds the few-millisecond runtime target and does not establish live
performance. Frozen-baseline comparisons retain every vertex bitwise across
all 180 frames of each fixture, with unchanged collision work and budgets.

## Circular and rounded material

`ClothRestPattern.Circle(diameter, resolution, totalMass, height, pinnedVertices)`
and `RoundedRectangle(width, depth, cornerRadius, columns, rows, totalMass,
height, pinnedVertices)` build actual indexed cloth boundaries, not a square
sheet with invisible corners. `ToDefinition()` supplies the solver's copied
rest state. UVs describe physical rest-plane locations and stay attached to
the moving material, so the map does not stretch a square chart into a circle.

Vertex and pin IDs are stable within a pattern. Both diagonal shear constraints
are retained; triangle diagonals are chosen at creation for bounded quality.
Lumped masses follow each vertex's incident triangle area, including pinned
material. Extreme aspect/resolution combinations or too little mass for the
solver's inverse-mass bounds are rejected, not silently reshaped or reweighted.
These are inscribed polygonal boundaries; more resolution improves the curve.
Changing shape or dimensions creates a new rest pattern, not live remeshing.

## External acceleration and compliant pulling

`XpbdStepInputs` owns additional per-particle accelerations and up to 64 sparse
`XpbdTarget` XYZ constraints. Each snapshot belongs to the exact immutable
definition object; similarly sized or reordered geometry is not interchangeable.
Acceleration is bounded at 30 simulation distance units/second squared per
particle, separately from gravity. A target's compliance controls a true XPBD
constraint; it is not a positional interpolation or rigid-sheet translation.
Pins remain exact and deliberately ignore both forces and conflicting targets.

Non-pinned targets farther than one simulation distance unit reject the call.
Cumulative target correction is bounded at 0.04 units per particle per
1/120-second substep (scaled by actual duration for measured intervals);
other constraints, prediction and contacts also move particles, so this is not
a total speed bound. Inputs share the solver's preflight budget and have a
separate 16,384-work-unit cap. Collision checks still run after the pulls, and
an unproved or excessive contact rolls back the complete advance.

In the default fixed-clock API, inputs apply only to substeps actually produced by that call. They are
never latched: a no-substep call discards its inputs, even if some fractional
elapsed time remains. Changing forces must be sampled at fixed-step boundaries
for reproducibility. Constant inputs preserve 60/120-Hz call grouping exactly.
Snapshots intentionally allocate owned copies; reused snapshots add no warmed
per-Advance allocation. These mechanisms enable future music and follow inputs,
but no plugin adapter, audio playback or live character movement is activated.

## Tests and synthetic acceptance

From the repository root:

```sh
dotnet test tests/XivCloth.Core.Tests/XivCloth.Core.Tests.csproj -c Release
dotnet test tests/XivCloth.Owner.Tests/XivCloth.Owner.Tests.csproj -c Release
dotnet test tests/XivCloth.Rendering.Tests/XivCloth.Rendering.Tests.csproj -c Release
dotnet build src/XivCloth.Core/XivCloth.Core.csproj -c Release
```

The Core suite has 256 deterministic cases, including the
solver, patterns, inputs, scene rebinding, stair response, moving feet,
measured-interval timing and owned calibrated capsule construction.
Twenty-four separate owner integration cases cover complete circular/rounded
material on finite tread/riser fixtures, lease refresh and revocation, exact
raw-foot calibration binding, ordinary rejected zero-gap plants, explicit paired
plant/lift/recovery/replant, and publication checks.
Two public composition regressions specifically verify that a terrain repair
cannot bypass the final moving-foot sweep, including full rollback and retry.
The cases cover geometry,
mass, UVs, pins, actual solver admission, input ownership, bounded compliant
pulling, no-step discard, exact grouping, collision refusal and complete rollback.
Rebinding tests retain momentum and fractional time exactly, reject a new
obstacle through a cloth face despite clear vertices, preserve old state on
budget/contact failure, and explicitly demonstrate the absent moving-body proof.
Separate integration cases take actual simulated indexed poses through the
renderer upload contract without creating a device or activating the plugin.
The optional `tests/XivCloth.FootAdapter.Tests` project links the real
`XivSurface.Core` producer types via `-p:SurfaceCoreProject=/absolute/path/to/XivSurface.Core.csproj`.
Its 22 cases verify measured-marker and actual raw/calibrated proxy conversion,
not live native skeleton or footwear geometry. These counts are source-level
regression evidence, not live renderer acceptance.
The physics cases cover all 12 analytic
dihedral-gradient components at five signed folds; stretch, shear and genuine
hinge response; exact fixed-step grouping; immutable input/output ownership;
bounded work and time debt; generation/reset policy; pin and deep-contact
refusal; thin triangles in three orientations; and later-substep rollback
including velocity and fractional time. Collision regressions exercise interior
tunnelling despite clear corners, edge grazing, skinny/large-coordinate geometry,
coplanar separation, BVH versus linear queries, explicit stacked thin sheets,
half-thickness semantics, noninterpolable publication and bounded refusal.

Synthetic results, not observations of the game:

- An 81-particle hanging sheet sags from center Y=1.0 to approximately 0.597,
  with fixed corner pins and maximum stretch about 1.94%.
- An 85-particle sheet drapes over six explicit 0.15-unit tread/riser triangles
  for 600 accepted substeps, moving material horizontally by approximately
  0.0504 units with maximum stretch error about 0.625% and zero measured vertex
  penetration. A 0.2-unit fixture moves about 0.0853 units with 0.852% stretch
  error. Every accepted linear substep also passes the finite-face guard.
  These rounded synthetic drapes do not prove fidelity on live stairs.

The existing `XivSurface.Core` lattice solver and plugin remain independent.
Future integration must preserve the live depth/HUD and foot-safety contracts,
establish actual visible-surface provenance and bounded local collision policy,
handle unresolved response safely, and measure runtime cost. Converting this free 3-D state back into a fixed-X/Z
heightfield would undo the material motion this library is intended to provide.

## Moving foot proxies (not yet connected to live cloth)

`FootProxyPose` owns two capsule estimates built from four measured heel/toe
markers. The capsule bottoms use the existing sole estimates; they do not move
the character, invent a shoe surface or substitute a floor ray for a foot.
`FootProxyMotion` requires matched actor/model identity, consecutive observations,
a strict 1/30-second freshness horizon and bounded endpoint motion.
`AdvanceWithFeet` applies whole-face response and conservatively swept relative
capsule motion. Collision work is counted; a refused call rolls positions,
velocities, clock and accepted-foot state back together. Once feet are accepted,
missing or discontinuous tracking cannot silently remove them: explicit reset
and readmission is needed.

For `AdvanceWithFeet`, the complete observed foot interval maps across the fixed substeps produced by
the call. This certifies the modeled linear segments, not unsampled animation
or exact wall-time alignment with a GPU skeleton. Render publication therefore
still needs exact owner/model binding, a current whole-surface foot check and
freshness handling. Legacy unbound frames must not be relabeled by a consumer.

The separate `MeasuredFootInterval` / `AdvanceMeasuredFeet` API supports genuine
consecutive observations up to 50 ms apart, including 25 FPS capture cadence.
Only the after-pose must be fresh within 1/30 second; the before-pose is bounded
history, never retimestamped. It covers the complete interval with at most six
equal substeps, using actual duration for forces, damping, compliance, velocity
and target travel. Strict and measured clock modes cannot mix without `Reset`.
The same whole-surface terrain/foot checks and complete rollback remain in force.
This does not reconstruct hidden animation, permit endpoint interpolation or
widen the separate late-publication observation policy.

`CalibratedFootSnapshotAdapter` optionally consumes the actual immutable
`RawFootFrame` and `FootPlantCalibration` producer types. It preserves lifted
and rotated XYZ capsule axes instead of flattening them to the old render-mask
sole. Calibration remains an approximate stationary-plant model, not a shoe-mesh
measurement; unsupported model/joint scale and stale or relabeled captures refuse.
A dedicated sealed reference-only `FootProxyModelBinding` binds the exact
calibration. Recalibration, missing observations or adapter reset break motion
continuity even when numeric actor fields match. Exact raw-frame metadata stays
separate from model equality so genuine next captures can share a model. The
adapter copies geometry only; plant/conflict diagnostics never authorize cloth
admission or move feet, terrain or cloth. This seam is not runtime activation.

The estimated soles and ground may leave no feasible gap for cloth. That is a
real integration constraint, not permission to raise feet, lower ground, punch
holes or claim a raised-sole synthetic test proves actual shoe clearance.
Self-collision, friction and live visual acceptance remain separate work.

### Explicit paired planted-contact model (prototype)

`PairedPlantPolicy` names a separate approximate plane-parametric sole model;
it preserves the original raw captures and capsules rather than relabeling them.
Each foot names one horizontal tread triangle or an exact convex union of two.
The complete scene and its required `CollisionCoverage` remain unchanged.
`AdmitPairedScene`, `AdvanceMeasuredFeetWithPlants`, and
`CaptureForPlantedFeet` can accept local zero-thickness compression under these
declared soles, while other terrain/foot pairs retain ordinary positive margins.
Whole faces and shared vertices are checked; material faces and UVs are never
removed. Unequal heel/toe lift and rotation are retained. Actual accepted test
steps recover ordinary thickness after lift and replant without flattening all
surrounding material. The outer test folds are seeded, not emergent bunching.

Policy replacement and `RemovePlantPolicy` require complete unchanged-pose
admission. Failed work rolls back compression along with accepted solver state.
Publication requires exact policy, scene and observation references plus the
serialized owner's final `CanSubmit`; a numeric generation or immutable binding
alone cannot revoke stale work. The original ordinary APIs remain unchanged.
This does not solve slopes, many-face treads, tread transfer, missing-history
recovery, shoe-mesh accuracy, real-time cost or live terrain acquisition. Neither
the paired owner nor its calibrated adapter is activated in the game.

## Reproducible cost probe

`tools/XivCloth.CostProbe` references this library and reports its loaded assembly
hash and embedded build-source hashes. It separately measures fixed-step
integration and immutable publication for 81, 225 and 441 particles against
two or 128 synthetic triangles, including the current bounded finite-face guard.
Expected budget refusals are reported without an accepted-step timing. It does
not load game data or access devices.

```sh
dotnet build tools/XivCloth.CostProbe/XivCloth.CostProbe.csproj -c Release
DOTNET_TieredCompilation=0 dotnet run --no-build -c Release --project tools/XivCloth.CostProbe/XivCloth.CostProbe.csproj
```

Run on an otherwise available CPU and record runtime, affinity and contention;
disable tiered compilation when comparing optimized steady-state results.
The historical vertex-only baseline measured approximately 1.19/3.42/6.79 ms of
process CPU time per 60-Hz call at 81/225/441 particles against 128 triangles;
those numbers exclude this extension and must not describe its current cost.
Before the whole-cloth prism optimization, the reviewed extension's separate
81-particle/128-face fixture measured about
1.962 ms airborne and 2.528 ms resting per 60-Hz call, with zero warmed integration
allocations. Those stationary cases prune finite sweeps by vertical separation;
crossing geometry can be more expensive. That version's 225-particle/128-collider
case refused at 100,000 collision work units. No maximum-size real-time
claim is made. All timings exclude scene acquisition, self-collision, foot/body
constraints, uploads and rendering. Immutable `Capture()` still allocates.

The current whole-cloth prism prefilter charges its AABB refit and obstacle
half-space exclusions. It does not replace an infinite blocked prism with a
finite obstacle box, change clearances or increase budgets. A clear 81-particle /
128-face admission drops from 16,384 to 209 counted work units; a 225-particle
sheet wholly above those floors now fits the default budget. 480 deterministic
rotated/layered cases match the original admission verdict. The prior failure
test is retained as a separately smaller-budget refusal rather than asserting
that this successful optimization must still fail.

Four 9-by-9 circle/rounded material tests over 128 explicit stair/landing faces
also measure the work reduction, but still stop after 12 accepted frames during
contact response. Their asserted refusal is a tracked reproducer, NOT evidence
of finished stair draping. Current warmed 81-particle flat fixtures measured
approximately 1.212 ms airborne / 2.044 ms resting per 60-Hz call; these limited
synthetic observations are not live frame-time or collision-heavy guarantees.
