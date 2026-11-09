from pathlib import Path,PurePosixPath
import hashlib,zipfile,json
root=Path(__file__).parent;docs=root.parent;source=Path(r'D:\Temp\Lyra-Extension-Epub');host=Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui');runtime=Path(r'D:\Temp\lyra_epub_r4_candidate')
sha=lambda b:hashlib.sha256(b).hexdigest()
def freeze(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():assert p.read_bytes()==b,str(p)
 else:p.write_bytes(b)
archive=docs/'candidates-r4/lyra-ext-epub-0.8.0-candidate.zip'
result={'archive_sha256':sha(archive.read_bytes()),'files':[],'host_files':[],'binaries':[],'evidence':[]}
assert result['archive_sha256']=='b128b9dfbe9d7a1085cc3505f9b3edc645847cfa24da33958eb26edd685f184b'
manifest=json.loads((docs/'r4-evidence/build-manifest.json').read_text(encoding='utf-8-sig'))
with zipfile.ZipFile(archive) as z:
 for item in z.infolist():
  if item.is_dir():continue
  rel=PurePosixPath(item.filename.replace('\\','/'));assert not rel.is_absolute() and '..' not in rel.parts
  b=z.read(item.filename);p=Path(*rel.parts);assert(source/p).read_bytes()==b,str(rel)
  assert(runtime/'extensions/epub'/p).read_bytes()==b,'runtime '+str(rel)
  freeze(root/'candidate'/p,b);result['files'].append({'path':str(rel),'sha256':sha(b),'matches_source':True,'matches_runtime':True})
for p in (source/'tests').glob('*.cjs'):freeze(root/'candidate/tests'/p.name,p.read_bytes())
for name in ['ExtensionHostWindow.xaml','ExtensionHostWindow.xaml.cs','ReaderProtocol.cs','ReaderStateStore.cs']:
 b=(host/name).read_bytes();assert sha(b)==manifest['host']['sourceHashes'][name.replace('.','_')]
 freeze(root/'host-snapshot'/name,b);result['host_files'].append({'name':name,'sha256':sha(b)})
for base in [Path(r'D:\Temp\lyra_epub_host_r4'),runtime]:
 p=base/'WinUIGui.dll'
 if not p.exists():continue
 digest=sha(p.read_bytes());assert digest=='9eba643fa6e3933b5a3f86afc1be424ffaa08a09bf11b9ac89d2c451e3a9df95';result['binaries'].append({'path':str(p),'sha256':digest})
r=(docs/'DeepSeek实施回报-R4.md').read_bytes();assert sha(r)=='3168031da48b7c4d127f3a5102641aa9e324cbefd6fd8e8fb0e20da5573af207';freeze(root/'submitted-report/DeepSeek实施回报-R4.md',r)
e=docs/'r4-evidence';sums=(e/'SHA256SUMS').read_bytes();assert sha(sums)=='a7bc43c89cb6e30df1f485c25baae97b70a41cb12b938c47c68cfba8ca0ef48e'
for line in sums.decode('utf-8-sig').splitlines():
 digest,rel=line.split(None,1);rel=rel.strip();assert sha((e/rel).read_bytes())==digest,rel
for p in e.rglob('*'):
 if p.is_file():
  rel=p.relative_to(e);b=p.read_bytes();freeze(root/'submitted-evidence'/rel,b);result['evidence'].append({'path':str(rel),'sha256':sha(b)})
freeze(root/'identity.json',json.dumps(result,ensure_ascii=False,indent=2).encode('utf-8'))
print(json.dumps({'archive_verified':True,'files':len(result['files']),'source_runtime_match':True,'host_sources_match_manifest':True,'binaries':result['binaries'],'evidence_frozen':len(result['evidence']),'report_sha_verified':True},ensure_ascii=False,indent=2))
