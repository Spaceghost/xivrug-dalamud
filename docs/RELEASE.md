Experimental Dalamud API 15 build 0.0.1.6 fixes a captured shared-floor-edge rejection when X and Z have different float spacing. The bounded fallback searches along the measured portal inside the original correction radius; both floor-containment checks, portal membership, ordering and the 2 mm world-space cap remain required. Five regression cases cover the captured corridor in both directions, unequal float spacing, wrong heights, expiry, deadlines and the world-space cap.

This is a partial repair, not a visibility fix release. A recorded Limsa walk shows the rug disappearing by a planter and remaining hidden. Public 0.0.1.3 produced 328 supported publications across 3,001 movement updates and none across 2,432 stationary updates. These are support counters, not displayed-frame percentages. Candidate 0.0.1.6 still produced zero publications and zero render attempts across 4,426 updates at that stationary location. Diagnostics identify 11/49 missing vertices and 11/36 missing cells. A second capture has an uncovered surface corridor; bounded downward discovery hits an upper surface and misses below it. That remaining discovery/clearance failure needs repair before release.

MCP support diagnostics now include the timestamped geometry report already generated every 20 seconds, allowing investigation even when Dalamud's log file is capped. The report is historical evidence, never render authorization.

The preceding 0.0.1.5 changes publish cloth geometry, map coordinates and lift together. Failed world updates, scene resets and GPU faults revoke supported cloth; canceled producers cannot restore it or erase a replacement. Render submission and optional character-pose assistance now use this same publication boundary.

The preceding 0.0.1.4 changes keep supported woven cloth visible when map textures are unavailable, restoring current cartography automatically. Map-scaled route decoration waits for its map, and no previous-zone texture is reused.

Fallback foot protection now covers a bounded GPU preparation window and checks the exact capture, zone and deadline immediately before submission. Slow preparation can skip a draw without renewing stale foot evidence. Native finalized-frame and physical-pose leases remain independent.

Validation: all 1,605 offline tests pass, including expiry, supersession, clock rollback, preparation stalls, next-frame recovery, canceled producers and allocation-free publication reads. Both normal and compatibility builds compile without warnings. The live planter check still fails; in-world texture-outage, publication and fallback-rendering checks remain pending; see docs/stability-audit.json. Earlier 0.0.1.3 restored the previously failing Limsa location and passed a continuous supported-frame check, but that result does not certify these newer changes or all terrain. The public testing release remains 0.0.1.3 pending this validation.

MCP status distinguishes supported meshes from actual CPU-side GPU submissions and reports map fallback, foot-gate state and faults. Development-only `/rug mapprobe on|off` simulates a ten-second map outage without changing settings. See README for build commands and verification limits.

XivRug retains development FloorMap compatibility. Disable a development FloorMap copy before installing Rug; do not load both.
