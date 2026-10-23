Experimental Dalamud API 15 build 0.0.1.3 fixes disappearing cloth on thin overlapping collision surfaces. Discovery now resolves sub-centimetre overlaps without stepping past the original floor; refresh verifies that exact floor before renewing its evidence. Missing or changed floors still invalidate support, and ray budgets and freshness limits are unchanged.

Validation: all 1,580 offline tests pass, including a regression using captured Limsa collision triangles. The compatibility build was loaded in a live session and restored the rug at the previously failing location. A bounded visual-only follow probe exercised the same area without moving the character. This does not establish coverage across all zones, stairs, or moving geometry.

MCP's cached Rug status now includes support update/publication counters and the current visibility reason. See README for pinned-reference build commands and verification limits.

XivRug retains development FloorMap compatibility. Disable a development FloorMap copy before installing Rug; do not load both.
