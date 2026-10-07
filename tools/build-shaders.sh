#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
shader_tmp=$(mktemp -d "$repo_root/.shader-build.XXXXXX")
cleanup() { rm -f -- "$shader_tmp/SurfaceDecal.dxbc" "$shader_tmp/LiveRug.dxbc" "$shader_tmp/LiveRugVertex.dxbc" "$shader_tmp/source/SurfaceDecal.hlsl" "$shader_tmp/source/LiveRug.hlsl" "$shader_tmp/source/LiveRugVertex.hlsl"; rmdir -- "$shader_tmp/source" "$shader_tmp"; }
trap cleanup EXIT
mkdir -- "$shader_tmp/source"
for name in SurfaceDecal LiveRug LiveRugVertex; do
  cp -- "$repo_root/src/XivSurface.Core/Shaders/$name.hlsl" "$shader_tmp/source/$name.hlsl"
done
podman run --rm \
  -v "$shader_tmp/source:/source:ro,Z" \
  -v "$shader_tmp:/output:Z" \
  registry.fedoraproject.org/fedora:44 sh -ec '
    dnf -y -q install vkd3d-compiler-1.17-2.fc44 >/dev/null
    vkd3d-compiler -x hlsl -b dxbc-tpf -p ps_5_0 \
      -o /output/SurfaceDecal.dxbc /source/SurfaceDecal.hlsl
    vkd3d-compiler -x hlsl -b dxbc-tpf -p ps_5_0 \
      -o /output/LiveRug.dxbc /source/LiveRug.hlsl
    vkd3d-compiler -x hlsl -b dxbc-tpf -p vs_5_0 \
      -o /output/LiveRugVertex.dxbc /source/LiveRugVertex.hlsl
  '
for name in SurfaceDecal LiveRug LiveRugVertex; do
  cmp -- "$shader_tmp/source/$name.hlsl" "$repo_root/src/XivSurface.Core/Shaders/$name.hlsl"
done
test "$(od -An -tx1 -N4 "$shader_tmp/SurfaceDecal.dxbc" | tr -d ' \n')" = 44584243
mv -- "$shader_tmp/SurfaceDecal.dxbc" "$repo_root/src/XivSurface.Core/Shaders/SurfaceDecal.dxbc"
test "$(od -An -tx1 -N4 "$shader_tmp/LiveRug.dxbc" | tr -d ' \n')" = 44584243
mv -- "$shader_tmp/LiveRug.dxbc" "$repo_root/src/XivSurface.Core/Shaders/LiveRug.dxbc"
test "$(od -An -tx1 -N4 "$shader_tmp/LiveRugVertex.dxbc" | tr -d ' \n')" = 44584243
mv -- "$shader_tmp/LiveRugVertex.dxbc" "$repo_root/src/XivSurface.Core/Shaders/LiveRugVertex.dxbc"
sha256sum "$repo_root/src/XivSurface.Core/Shaders/SurfaceDecal.dxbc"
bash "$repo_root/tools/build-cloth-shaders.sh"
