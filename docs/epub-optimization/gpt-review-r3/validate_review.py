from pathlib import Path
import hashlib,json,re
root=Path(__file__).parent;docs=root.parent
report=docs/'GPT验收结论-R3暨DeepSeek返工任务书-R4.md'
text=report.read_text(encoding='utf-8');sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
identity=json.loads((root/'identity.json').read_text(encoding='utf-8'))
links=re.findall(r'\]\(<([^>]+)>\)',text);broken=[]
for link in links:
 name,_,fragment=link.partition('#');p=Path(name)
 if not p.exists():broken.append({'link':link,'reason':'not found'})
 elif fragment.startswith('L') and p.is_file():
  m=re.match(r'L(\d+)',fragment)
  if m:
   try:
    if int(m[1])>len(p.read_text(encoding='utf-8').splitlines()):broken.append({'link':link,'reason':'line out of range'})
   except UnicodeError:broken.append({'link':link,'reason':'nontext line link'})
checks=[]
def verify(p,digest):checks.append({'path':str(p),'ok':p.is_file() and sha(p)==digest.lower()})
for f in identity['files']:
 for base in [root/'candidate',Path(r'D:\Temp\Lyra-Extension-Epub')]:verify(base/f['path'],f['sha256'])
for f in identity['host_files']:
 for base in [root/'host-snapshot',Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')]:verify(base/f['name'],f['sha256'])
verify(docs/'candidates-r3/lyra-ext-epub-0.7.0-candidate.zip',identity['archive_sha256'])
for b in identity['binaries']:verify(Path(b['path']),b['sha256'])
reportsha='da4e957a5c7963714066d766712d6f91d0c9b9cda43e5f68902467fc1284e6b2'
verify(docs/'DeepSeek实施回报-R3.md',reportsha);verify(root/'submitted-report/DeepSeek实施回报-R3.md',reportsha)
for p in (root/'submitted-evidence').rglob('*'):
 if p.is_file():
  q=docs/'r3-evidence'/p.relative_to(root/'submitted-evidence')
  checks.append({'path':str(q),'ok':q.is_file() and q.read_bytes()==p.read_bytes()})
for manifest,folder in [('native-supplement-identity.json','submitted-native-supplement'),('compat-addendum-identity.json','submitted-compat-addendum')]:
 for f in json.loads((root/manifest).read_text(encoding='utf-8')):verify(root/folder/f['path'],f['sha256'])
result={'report':str(report),'report_sha256':sha(report),'report_lines':len(text.splitlines()),'report_utf8_bytes':len(report.read_bytes()),'links_checked':len(links),'broken_links':broken,'frozen_checks':checks,'all_frozen_unchanged':all(c['ok'] for c in checks),'complete':not broken and all(c['ok'] for c in checks)}
target=root/'review-validation.json';i=1
while target.exists():i+=1;target=root/f'review-validation-{i}.json'
target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='frozen_checks'},ensure_ascii=False,indent=2))
print(json.dumps({'validation':str(target),'check_count':len(checks),'failed_checks':[c for c in checks if not c['ok']]},ensure_ascii=False,indent=2))
raise SystemExit(0 if result['complete'] else 1)
