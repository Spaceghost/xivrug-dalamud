Experimental Dalamud API 15 build 0.0.1.5 publishes cloth geometry, map coordinates and lift together. Failed world updates, scene resets and GPU faults revoke supported cloth; canceled producers cannot restore it or erase a replacement. Render submission and optional character-pose assistance now use this same publication boundary.

The preceding 0.0.1.4 changes keep supported woven cloth visible when map textures are unavailable, restoring current cartography automatically. Map-scaled route decoration waits for its map, and no previous-zone texture is reused.

Fallback foot protection now covers a bounded GPU preparation window and checks the exact capture, zone and deadline immediately before submission. Slow preparation can skip a draw without renewing stale foot evidence. Native finalized-frame and physical-pose leases remain independent.

Validation: all 1,600 offline tests pass, including expiry, supersession, clock rollback, preparation stalls, next-frame recovery, canceled producers and allocation-free publication reads. Both normal and compatibility builds compile without warnings. In-world texture-outage, publication and fallback-rendering checks are still pending; see docs/stability-audit.json. Earlier 0.0.1.3 restored the previously failing Limsa location and passed a continuous supported-frame check, but that result does not certify these newer changes or all terrain. The public testing release remains 0.0.1.3 pending this validation.

MCP status distinguishes supported meshes from actual CPU-side GPU submissions and reports map fallback, foot-gate state and faults. Development-only `/rug mapprobe on|off` simulates a ten-second map outage without changing settings. See README for build commands and verification limits.

XivRug retains development FloorMap compatibility. Disable a development FloorMap copy before installing Rug; do not load both.
