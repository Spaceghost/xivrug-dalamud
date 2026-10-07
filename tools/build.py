#!/usr/bin/env python3
"""Build and test against reviewed references; all outputs stay inside this checkout."""
import argparse, hashlib, json, os, shutil, subprocess
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument('--references', type=Path, default=Path(os.environ.get('DALAMUD_HOME', str(ROOT/'.dalamud'))))
p.add_argument('--test', action='store_true')
p.add_argument('--offline-feed', type=Path)
p.add_argument('--tests-only', action='store_true')
p.add_argument('--locked', action='store_true')
a = p.parse_args()
config = json.loads((ROOT/'tools/build-projects.json').read_text())
dotnet = os.environ.get('DOTNET') or shutil.which('dotnet') or str(Path.home()/'.dotnet/dotnet')
common = ['-c', 'Release', '-p:UseArtifactsOutput=true', '-p:ArtifactsPath='+str(ROOT/'artifacts'), '-p:NuGetAudit=false', '-p:RestorePackagesWithLockFile=true']
if a.locked: common += ['-p:RestoreLockedMode=true']
if a.offline_feed: common += ['-p:RestoreSources='+str(a.offline_feed.resolve())]
if not a.tests_only:
 reviewed = json.loads((ROOT/'tools/references.json').read_text())
 for name, expected in reviewed['files'].items():
  f = a.references/name
  if not f.is_file() or hashlib.sha256(f.read_bytes()).hexdigest() != expected:
   raise SystemExit('Reviewed Dalamud '+reviewed['version']+' references required: '+name+' differs or is missing. Set --references; do not update the digest without reviewing native bindings.')
 for project in config['plugins']:
  subprocess.run([dotnet,'build',str(ROOT/project),*common,'-p:DalamudLibPath='+str(a.references.resolve())+'/'],cwd=ROOT,check=True)
if a.test or a.tests_only:
 for project in config['tests']:
  test_props = ['-p:SurfaceCoreProject='+str(ROOT/'src/XivSurface.Core/XivSurface.Core.csproj')] if 'FootAdapter.Tests' in project else []
  subprocess.run([dotnet,'test',str(ROOT/project),*common,*test_props,'-p:DalamudLibPath='+str(a.references.resolve())+'/'],cwd=ROOT,check=True)
