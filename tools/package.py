#!/usr/bin/env python3
"""Deterministic Dalamud ZIPs, per-module download links and a shared release listing."""
import argparse, datetime, hashlib, json, os, subprocess, zipfile
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument('--out', type=Path, default=ROOT/'release')
p.add_argument('--epoch', type=int)
a = p.parse_args()
config = json.loads((ROOT/'tools/build-projects.json').read_text())
if a.epoch is None:
 a.epoch = int(os.environ.get('SOURCE_DATE_EPOCH') or subprocess.check_output(['git','log','-1','--format=%ct'],cwd=ROOT,text=True).strip())
# ZIP stores UTC timestamps at two-second resolution, never host-local metadata.
stamp = datetime.datetime.fromtimestamp(max(315532800, a.epoch), datetime.timezone.utc)
stamp = (stamp.year,stamp.month,stamp.day,stamp.hour,stamp.minute,stamp.second//2*2)
a.out = a.out.resolve(); a.out.mkdir(parents=True, exist_ok=True)
listing=[]; packages=[]
for item in config['packages']:
 manifest=json.loads((ROOT/item['manifest']).read_text()); name=manifest['InternalName']; version=manifest['AssemblyVersion']
 source=ROOT/'artifacts/bin'/item['project']/'release'
 if not (source/(name+'.dll')).is_file():raise SystemExit('Build output missing for '+name)
 files={f.name:f.read_bytes() for f in sorted(source.glob('*.dll'))}
 for f in sorted((source/'runtimes/win-x64/native').glob('*')):
  if f.is_file():files[f.relative_to(source).as_posix()]=f.read_bytes()
 files[name+'.json']=(json.dumps(manifest,indent=2)+'\n').encode()
 files['LICENSE']=(ROOT/'LICENSE').read_bytes()
 notice=ROOT/'THIRD-PARTY-NOTICES.md'
 if notice.exists():files[notice.name]=notice.read_bytes()
 for required in item.get('required',[]):
  if required not in files:raise SystemExit(name+': missing dependency '+required)
 # Game-provided reference assemblies must never be redistributed in a plugin ZIP.
 for forbidden in ['Dalamud.dll','FFXIVClientStructs.dll','Lumina.dll','Lumina.Excel.dll','Dalamud.Bindings.ImGui.dll','Dalamud.Bindings.ImGuizmo.dll','Dalamud.Bindings.ImPlot.dll','TerraFX.Interop.Windows.dll','Newtonsoft.Json.dll']:
  if forbidden in files:raise SystemExit(name+': output includes host reference '+forbidden)
 artifact=name+('-dev-' if not item['installer'] else '-')+version+'.zip'
 target=a.out/artifact
 with zipfile.ZipFile(target,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
  for rel,data in sorted(files.items()):
   info=zipfile.ZipInfo(rel,stamp);info.compress_type=zipfile.ZIP_DEFLATED;info.create_system=3;info.external_attr=0o100644<<16;z.writestr(info,data,compresslevel=9)
 packages.append(target)
 if item['installer']:
  alias=a.out/(name+'-latest.zip');alias.write_bytes(target.read_bytes());packages.append(alias)
  url='https://github.com/'+config['repo']+'/releases/download/testing/'+alias.name
  entry={**manifest,'RepoUrl':'https://github.com/'+config['repo'],'IconUrl':item['icon'],'DownloadLinkInstall':url,'DownloadLinkUpdate':url,'DownloadLinkTesting':url,'TestingAssemblyVersion':version,'IsTestingExclusive':True,'LastUpdate':a.epoch}
  listing.append(entry)
(a.out/'pluginmaster-testing.json').write_text(json.dumps(listing,indent=2)+'\n')
(a.out/'build-info.json').write_text(json.dumps({'sourceCommit':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),'references':json.loads((ROOT/'tools/references.json').read_text()),'manualDevelopmentOnly':[i['manifest'] for i in config['packages'] if not i['installer']]},indent=2)+'\n')
(a.out/'SHA256SUMS').write_text(''.join(hashlib.sha256(f.read_bytes()).hexdigest()+'  '+f.name+'\n' for f in sorted(packages+list(a.out.glob('*.json')))))
for file in sorted(a.out.iterdir()):print(file.name)
