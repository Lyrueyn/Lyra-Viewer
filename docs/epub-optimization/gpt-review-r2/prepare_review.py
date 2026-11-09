from pathlib import Path, PurePosixPath
import hashlib,zipfile,json,shutil
root=Path(__file__).parent; docs=root.parent
source=Path(r'D:\Temp\Lyra-Extension-Epub'); host=Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')
archive=docs/'candidates-r2'/'lyra-ext-epub-0.6.1-candidate.zip'
sha=lambda b:hashlib.sha256(b).hexdigest()
result={'archive_sha256':sha(archive.read_bytes()),'files':[],'host_files':[],'binaries':[]}
assert result['archive_sha256']=='65cf0784ad4a006ba2444d1a5b08ef2c354b116e71325dfcc5e9a2c194170063'
def frozen_write(p,data):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists(): assert p.read_bytes()==data,str(p)
 else:p.write_bytes(data)
with zipfile.ZipFile(archive) as z:
 for info in z.infolist():
  if info.is_dir():continue
  rel=PurePosixPath(info.filename.replace('\\','/'));assert not rel.is_absolute() and '..' not in rel.parts
  data=z.read(info.filename);assert (source/Path(*rel.parts)).read_bytes()==data,str(rel)
  frozen_write(root/'candidate'/Path(*rel.parts),data)
  result['files'].append({'path':str(rel),'sha256':sha(data),'matches_source':True})
for p in (source/'tests').glob('*.cjs'):frozen_write(root/'candidate'/'tests'/p.name,p.read_bytes())
for name in ['ExtensionHostWindow.xaml','ExtensionHostWindow.xaml.cs','ReaderProtocol.cs','ReaderStateStore.cs']:
 data=(host/name).read_bytes();frozen_write(root/'host-snapshot'/name,data);result['host_files'].append({'name':name,'sha256':sha(data)})
for folder in [Path(r'D:\Temp\lyra_epub_host_r2'),Path(r'D:\Temp\lyra_epub_candidate')]:
 p=folder/'WinUIGui.dll';digest=sha(p.read_bytes());assert digest=='21932e2c246bf6152ba9c42834f60654a7a5ae239151b645f0ee8f14f17a30da';result['binaries'].append({'path':str(p),'sha256':digest})
for name in ['DeepSeek实施回报-R2.md']:
 frozen_write(root/'submitted-report'/name,(docs/name).read_bytes())
for p in (docs/'r2-evidence').glob('*'):
 if p.is_file():frozen_write(root/'submitted-evidence'/p.name,p.read_bytes())
frozen_write(root/'identity.json',json.dumps(result,ensure_ascii=False,indent=2).encode('utf-8'))
print(json.dumps({'archive_verified':True,'source_matches_candidate':True,'files':len(result['files']),'host_source_snapshot':str(root/'host-snapshot'),'binaries_match_report':True},ensure_ascii=False))
