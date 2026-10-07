#!/usr/bin/env bash
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
shader_tmp=$(mktemp -d "$repo_root/.cloth-shader-build.XXXXXX")
cleanup() { rm -f -- "$shader_tmp/ClothVertex.dxbc" "$shader_tmp/ClothPixel.dxbc" "$shader_tmp/IndexedClothVertex.dxbc" "$shader_tmp/source/ClothVertex.hlsl" "$shader_tmp/source/ClothPixel.hlsl" "$shader_tmp/source/IndexedClothVertex.hlsl"; rmdir -- "$shader_tmp/source" "$shader_tmp"; }
trap cleanup EXIT
mkdir -- "$shader_tmp/source"
cp -- "$repo_root/src/XivSurface.Core/Shaders/ClothVertex.hlsl" "$shader_tmp/source/ClothVertex.hlsl"
cp -- "$repo_root/src/XivSurface.Core/Shaders/ClothPixel.hlsl" "$shader_tmp/source/ClothPixel.hlsl"
cp -- "$repo_root/src/XivSurface.Core/Shaders/IndexedClothVertex.hlsl" "$shader_tmp/source/IndexedClothVertex.hlsl"
podman run --rm --cpus=1 --memory=768m --memory-swap=768m --pids-limit=128 \
  -v "$shader_tmp/source:/source:ro,Z" \
  -v "$shader_tmp:/output:Z" \
  registry.fedoraproject.org/fedora:44 sh -ec '
    dnf -y -q install vkd3d-compiler-1.17-2.fc44 >/dev/null
    vkd3d-compiler -x hlsl -b dxbc-tpf -p vs_5_0 -o /output/ClothVertex.dxbc /source/ClothVertex.hlsl
    vkd3d-compiler -x hlsl -b dxbc-tpf -p ps_5_0 -o /output/ClothPixel.dxbc /source/ClothPixel.hlsl
    vkd3d-compiler -x hlsl -b dxbc-tpf -p vs_5_0 -o /output/IndexedClothVertex.dxbc /source/IndexedClothVertex.hlsl
  '
cmp -- "$shader_tmp/source/ClothVertex.hlsl" "$repo_root/src/XivSurface.Core/Shaders/ClothVertex.hlsl"
cmp -- "$shader_tmp/source/ClothPixel.hlsl" "$repo_root/src/XivSurface.Core/Shaders/ClothPixel.hlsl"
cmp -- "$shader_tmp/source/IndexedClothVertex.hlsl" "$repo_root/src/XivSurface.Core/Shaders/IndexedClothVertex.hlsl"
for name in ClothVertex ClothPixel IndexedClothVertex; do
  test "$(od -An -tx1 -N4 "$shader_tmp/$name.dxbc" | tr -d ' \n')" = 44584243
  mv -- "$shader_tmp/$name.dxbc" "$repo_root/src/XivSurface.Core/Shaders/$name.dxbc"
done
sha256sum "$repo_root/src/XivSurface.Core/Shaders/ClothVertex.dxbc" "$repo_root/src/XivSurface.Core/Shaders/ClothPixel.dxbc" "$repo_root/src/XivSurface.Core/Shaders/IndexedClothVertex.dxbc"
