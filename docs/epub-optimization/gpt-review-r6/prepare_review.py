from pathlib import Path,PurePosixPath
import hashlib,json,zipfile,subprocess
root=Path(__file__).parent;docs=root.parent;source=Path(r'D:\Temp\Lyra-Extension-Epub');host=Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui');runtime=Path(r'D:\Temp\lyra_r6_candidate')
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
def freeze(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():assert p.read_bytes()==b,str(p)
 else:p.write_bytes(b)
archive=docs/'candidates-r6/lyra-ext-epub-0.10.0-candidate.zip';assert sha(archive.read_bytes())=='f2b2741a166749e7108e010151a46246ac2df40bf1f0861050e8e12d612c535c';assert archive.stat().st_size==98840
e=docs/'r6-evidence';manifest=load(e/'r6-manifest.json');assert sha((e/'r6-manifest.json').read_bytes())=='0560435393d8b2449d1effc191bb95e9438ec5a1971cf7d0f386a64c618ea988'
head=subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip();branch=subprocess.check_output(['git','-C',str(source),'branch','--show-current'],text=True).strip();status=subprocess.check_output(['git','-C',str(source),'status','--porcelain'],text=True)
assert head==manifest['commit']=='99bd102f4c9b4c1a92668d04e2e12260e523ad94' and branch=='r6' and not status
result={'commit':head,'branch':branch,'clean':not status,'archive_sha256':sha(archive.read_bytes()),'files':[],'host_files':[],'binaries':[],'evidence':[]}
with zipfile.ZipFile(archive) as z:
 for i in z.infolist():
  if i.is_dir():continue
  rel=PurePosixPath(i.filename.replace('\\','/'));assert not rel.is_absolute() and '..' not in rel.parts
  b=z.read(i.filename);p=Path(*rel.parts);assert (source/p).read_bytes()==b,str(rel);assert(runtime/'extensions/epub'/p).read_bytes()==b,'runtime '+str(rel)
  if str(rel) in manifest['modules']:assert sha(b)==manifest['modules'][str(rel)]
  freeze(root/'candidate'/p,b);result['files'].append({'path':str(rel),'sha256':sha(b),'matches_source':True,'matches_runtime':True})
for p in (source/'tests').glob('*.cjs'):freeze(root/'candidate/tests'/p.name,p.read_bytes())
for name in ['ExtensionHostWindow.xaml','ExtensionHostWindow.xaml.cs','ReaderProtocol.cs','ReaderStateStore.cs']:
 b=(host/name).read_bytes();freeze(root/'host-snapshot'/name,b);result['host_files'].append({'name':name,'sha256':sha(b),'provenance':'live source frozen; submitted manifest has no host source hashes/build mapping'})
for base in [Path(r'D:\Temp\lyra_epub_host_r6'),runtime]:
 for name,key in [('WinUIGui.dll','dll'),('WinUIGui.exe','exe')]:
  p=base/name;b=p.read_bytes();assert sha(b)==manifest['host'][key];result['binaries'].append({'path':str(p),'sha256':sha(b)})
r=(e/'DeepSeek实施回报-R6.md').read_bytes();assert sha(r)=='ef7c834f1d247e67314e1bb883bd331f005260eff3d2d779b53c239727ba45a5';freeze(root/'submitted-report/DeepSeek实施回报-R6.md',r)
assert sha((e/'SHA256SUMS').read_bytes())=='1e8d60eaf12661f3de45de46f059d3f573701317b2fa9ac1d317dfa69408a3a6'
n=0
for line in (e/'SHA256SUMS').read_text(encoding='utf-8-sig').splitlines():
 if not line.strip():continue
 d,rel=line.split(None,1);rel=rel.strip().removeprefix('*');p=(e/rel).resolve();p.relative_to(e.resolve());assert sha(p.read_bytes())==d.lower(),rel;n+=1
for p in sorted(e.rglob('*')):
 if p.is_file():
  b=p.read_bytes();rel=p.relative_to(e);freeze(root/'submitted-evidence'/rel,b);result['evidence'].append({'path':str(rel),'sha256':sha(b)})
freeze(root/'identity.json',json.dumps(result,ensure_ascii=False,indent=2).encode())
prior=[]
def check(p,d):prior.append({'path':str(p),'sha256':d,'ok':p.is_file() and sha(p.read_bytes())==d.lower()})
r5=docs/'gpt-review-r5';old=load(r5/'identity.json')
for f in old['files']:check(r5/'candidate'/f['path'],f['sha256'])
for f in old['host_files']:check(r5/'host-snapshot'/f['name'],f['sha256'])
for f in old['evidence']:
 check(r5/'submitted-evidence'/f['path'],f['sha256']);check(docs/'r5-evidence'/f['path'],f['sha256'])
for key in ['supplement','addendum']:
 for f in load(r5/f'native-{key}-identity.json'):
  check(r5/f'submitted-native-{key}'/f['path'],f['sha256']);check(docs/('r5-evidence-supplement' if key=='supplement' else 'r5-evidence-supplement-addendum')/f['path'],f['sha256'])
for f in load(r5/'prior-integrity.json')['checks']:check(Path(f['path']),f['expected_sha256'])
for f in load(r5/'host-audit/files.json'):check(r5/'host-audit'/f['path'],f['sha256'])
for base in [docs/'gpt-review-r4/model-audit',r5/'model-audit']:
 for f in load(base/'deliverables-sha256.json')['files']:check(base/f['file'],f['sha256'])
check(docs/'GPT验收结论-R5暨DeepSeek返工任务书-R6.md','5498220f4b78b457c23fd8b3e90c447e38b7fab474da5e196628fdc31ee4f75c')
check(docs/'candidates-r5/lyra-ext-epub-0.9.0-candidate.zip',old['archive_sha256'])
freeze(root/'prior-integrity.json',json.dumps({'checks':prior,'all_unchanged':all(x['ok'] for x in prior)},ensure_ascii=False,indent=2).encode())
print(json.dumps({'files':len(result['files']),'host_sources':len(result['host_files']),'evidence':len(result['evidence']),'sum_entries':n,'prior_checks':len(prior),'failures':[x for x in prior if not x['ok']]},ensure_ascii=False));assert all(x['ok'] for x in prior)
