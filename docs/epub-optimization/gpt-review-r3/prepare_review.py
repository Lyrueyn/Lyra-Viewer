from pathlib import Path,PurePosixPath
import hashlib,zipfile,json
root=Path(__file__).parent; docs=root.parent
source=Path(r'D:\Temp\Lyra-Extension-Epub');host=Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')
sha=lambda b:hashlib.sha256(b).hexdigest()
archive=docs/'candidates-r3'/'lyra-ext-epub-0.7.0-candidate.zip'
result={'archive_sha256':sha(archive.read_bytes()),'files':[],'host_files':[],'binaries':[]}
assert result['archive_sha256']=='847d8047702e8de6a8dec8b7ae9e3fb60693686400b71aa65c2b134a7270636c'
def freeze(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():assert p.read_bytes()==b,str(p)
 else:p.write_bytes(b)
with zipfile.ZipFile(archive)as z:
 for item in z.infolist():
  if item.is_dir():continue
  rel=PurePosixPath(item.filename.replace('\\','/'));assert not rel.is_absolute()and'..'not in rel.parts
  b=z.read(item.filename);assert(source/Path(*rel.parts)).read_bytes()==b,str(rel)
  freeze(root/'candidate'/Path(*rel.parts),b)
  result['files'].append({'path':str(rel),'sha256':sha(b),'matches_source':True})
for p in(source/'tests').glob('*.cjs'):freeze(root/'candidate'/'tests'/p.name,p.read_bytes())
for name in['ExtensionHostWindow.xaml','ExtensionHostWindow.xaml.cs','ReaderProtocol.cs','ReaderStateStore.cs']:
 b=(host/name).read_bytes();freeze(root/'host-snapshot'/name,b);result['host_files'].append({'name':name,'sha256':sha(b)})
for base in[Path(r'D:\Temp\lyra_epub_host_r3'),Path(r'D:\Temp\lyra_epub_r3_candidate')]:
 p=base/'WinUIGui.dll';digest=sha(p.read_bytes());assert digest=='8067990ebbaf126239b1f3b8cc936d36cb3b1f47dc317d541fdf4ce9c4a9905e';result['binaries'].append({'path':str(p),'sha256':digest})
r=(docs/'DeepSeek实施回报-R3.md').read_bytes();assert sha(r)=='da4e957a5c7963714066d766712d6f91d0c9b9cda43e5f68902467fc1284e6b2';freeze(root/'submitted-report'/'DeepSeek实施回报-R3.md',r)
for p in(docs/'r3-evidence').glob('*'):
 if p.is_file():freeze(root/'submitted-evidence'/p.name,p.read_bytes())
freeze(root/'identity.json',json.dumps(result,ensure_ascii=False,indent=2).encode('utf-8'))
print(json.dumps({'archive_verified':True,'source_matches_candidate':True,'files':len(result['files']),'host_snapshot':str(root/'host-snapshot'),'binaries_match_report':True,'report_sha_verified':True},ensure_ascii=False))
