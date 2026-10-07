#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
shader_tmp=$(mktemp -d "$repo_root/.music-shader-build.XXXXXX")
cleanup() {
  rm -f -- "$shader_tmp/MusicVertex.dxbc" "$shader_tmp/MusicPixel.dxbc" "$shader_tmp/source/MusicVertex.hlsl" "$shader_tmp/source/MusicPixel.hlsl"
  rmdir -- "$shader_tmp/source" "$shader_tmp"
}
trap cleanup EXIT
mkdir -- "$shader_tmp/source"
cp -- "$repo_root/src/XivSurface.Core/Shaders/MusicVertex.hlsl" "$shader_tmp/source/MusicVertex.hlsl"
cp -- "$repo_root/src/XivSurface.Core/Shaders/MusicPixel.hlsl" "$shader_tmp/source/MusicPixel.hlsl"
podman run --rm --cpus=1 --memory=768m --memory-swap=768m --pids-limit=128 \
  -v "$shader_tmp/source:/source:ro,Z" -v "$shader_tmp:/output:Z" \
  registry.fedoraproject.org/fedora:44 sh -ec '
    dnf -y -q install vkd3d-compiler-1.17-2.fc44 >/dev/null
    vkd3d-compiler -x hlsl -b dxbc-tpf -p vs_5_0 -o /output/MusicVertex.dxbc /source/MusicVertex.hlsl
    vkd3d-compiler -x hlsl -b dxbc-tpf -p ps_5_0 -o /output/MusicPixel.dxbc /source/MusicPixel.hlsl
  '
for name in MusicVertex MusicPixel; do
  cmp -- "$shader_tmp/source/$name.hlsl" "$repo_root/src/XivSurface.Core/Shaders/$name.hlsl"
  test "$(od -An -tx1 -N4 "$shader_tmp/$name.dxbc" | tr -d ' \n')" = 44584243
done
for name in MusicVertex MusicPixel; do
  mv -- "$shader_tmp/$name.dxbc" "$repo_root/src/XivSurface.Core/Shaders/$name.dxbc"
done
sha256sum "$repo_root/src/XivSurface.Core/Shaders/MusicVertex.dxbc" "$repo_root/src/XivSurface.Core/Shaders/MusicPixel.dxbc"
