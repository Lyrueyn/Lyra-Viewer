from pathlib import Path,PurePosixPath
import hashlib,zipfile,json
root=Path(__file__).parent;docs=root.parent;source=Path(r'D:\Temp\Lyra-Extension-Epub');host=Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui');runtime=Path(r'D:\Temp\lyra_epub_r5_candidate')
sha=lambda b:hashlib.sha256(b).hexdigest()
def freeze(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():assert p.read_bytes()==b,str(p)
 else:p.write_bytes(b)
archive=docs/'candidates-r5/lyra-ext-epub-0.9.0-candidate.zip'
result={'archive_sha256':sha(archive.read_bytes()),'files':[],'host_files':[],'binaries':[],'evidence':[]}
assert result['archive_sha256']=='60cc97bf2721bb0239122275b307833d871548c985a56068e649f3d3a1517b80'
assert archive.stat().st_size==95702
manifest=json.loads((docs/'r5-evidence/r5-manifest.json').read_text(encoding='utf-8-sig'))
with zipfile.ZipFile(archive) as z:
 for item in z.infolist():
  if item.is_dir():continue
  rel=PurePosixPath(item.filename.replace('\\','/'));assert not rel.is_absolute() and '..' not in rel.parts
  b=z.read(item.filename);p=Path(*rel.parts);assert(source/p).read_bytes()==b,str(rel)
  assert(runtime/'extensions/epub'/p).read_bytes()==b,'runtime '+str(rel)
  freeze(root/'candidate'/p,b);result['files'].append({'path':str(rel),'sha256':sha(b),'matches_source':True,'matches_runtime':True})
for p in (source/'tests').glob('*.cjs'):freeze(root/'candidate/tests'/p.name,p.read_bytes())
for name in ['ExtensionHostWindow.xaml','ExtensionHostWindow.xaml.cs','ReaderProtocol.cs','ReaderStateStore.cs']:
 b=(host/name).read_bytes();assert sha(b)==manifest['host']['sourceHashes'][name]
 freeze(root/'host-snapshot'/name,b);result['host_files'].append({'name':name,'sha256':sha(b)})
for base in [Path(r'D:\Temp\lyra_epub_host_r5'),runtime]:
 for name,digest in [('WinUIGui.dll','202954e5930fcdc7a50bd96975b84a12cbf7aa4375ac7334832d28a45f5f9480'),('WinUIGui.exe','d0f1bb58b07d0b6aeaeb463c841914d9594d0a803e3831fc272a6a5ffed3b335')]:
  p=base/name;assert sha(p.read_bytes())==digest;result['binaries'].append({'path':str(p),'sha256':digest})
r=(docs/'DeepSeek实施回报-R5.md').read_bytes();assert sha(r)=='47074ac5a43e1da58814dfefad8eb256415c7ae25a5b63bfdc072d5b95cfba2e';freeze(root/'submitted-report/DeepSeek实施回报-R5.md',r)
e=docs/'r5-evidence';sums=(e/'SHA256SUMS').read_bytes();assert sha(sums)=='bbd2060ff760c9e7b78bb4aab0151a1f54a9310f85321d3ca9dcd9f2911afc88'
result['checksum_receipt_discrepancy']={'claimed':'BBD2060FF760C9E7B78BB4BAA0151A1F54A9310F85321D3CA9DCD9F2911AFC88','actual':sha(sums),'note':'Claimed and actual differ at BAA/AAB; freeze actual file and verify every listed entry.'}
sums_count=0
for line in sums.decode('utf-8-sig').splitlines():
 if not line.strip():continue
 digest,rel=line.split(None,1);rel=rel.strip().removeprefix('*');p=(e/rel).resolve();p.relative_to(e.resolve());assert sha(p.read_bytes())==digest.lower(),rel;sums_count+=1
for p in e.rglob('*'):
 if p.is_file():
  rel=p.relative_to(e);b=p.read_bytes();freeze(root/'submitted-evidence'/rel,b);result['evidence'].append({'path':str(rel),'sha256':sha(b)})
freeze(root/'identity.json',json.dumps(result,ensure_ascii=False,indent=2).encode('utf-8'))
# Verify prior immutable snapshots plus the restored original R4 evidence; source legitimately moved to R5 is excluded.
prior=[]
def verify(p,d):prior.append({'path':str(p),'ok':p.is_file() and sha(p.read_bytes())==d.lower(),'expected_sha256':d.lower()})
for roundno in [3,4]:
 oldroot=docs/f'gpt-review-r{roundno}';old=json.loads((oldroot/'identity.json').read_text(encoding='utf-8'))
 for f in old['files']:verify(oldroot/'candidate'/f['path'],f['sha256'])
 for f in old['host_files']:verify(oldroot/'host-snapshot'/f['name'],f['sha256'])
 for f in old.get('evidence',[]):
  verify(oldroot/'submitted-evidence'/f['path'],f['sha256'])
  if roundno==4:verify(docs/'r4-evidence'/f['path'],f['sha256'])
 for mn,folder in [('native-supplement-identity.json','submitted-native-supplement'),('compat-addendum-identity.json','submitted-compat-addendum'),('native-corrections-identity.json','submitted-native-corrections')]:
  if (oldroot/mn).exists():
   for f in json.loads((oldroot/mn).read_text(encoding='utf-8')):verify(oldroot/folder/f['path'],f['sha256'])
verify(docs/'candidates-r4/lyra-ext-epub-0.8.0-candidate.zip','b128b9dfbe9d7a1085cc3505f9b3edc645847cfa24da33958eb26edd685f184b')
verify(docs/'GPT验收结论-R4暨DeepSeek返工任务书-R5.md','76cfe3aa0c9c7a0fcd8b7e76ebcf259d0116e0a5e2c5797d8af293c68baafdaa')
freeze(root/'prior-integrity.json',json.dumps({'checks':prior,'all_unchanged':all(c['ok'] for c in prior)},ensure_ascii=False,indent=2).encode('utf-8'))
print(json.dumps({'files':len(result['files']),'host_sources':len(result['host_files']),'evidence_frozen':len(result['evidence']),'submitted_sums_verified':sums_count,'prior_checks':len(prior),'prior_unchanged':all(c['ok'] for c in prior),'prior_failures':[c for c in prior if not c['ok']]},ensure_ascii=False,indent=2))
assert all(c['ok'] for c in prior),'prior evidence mismatch'
