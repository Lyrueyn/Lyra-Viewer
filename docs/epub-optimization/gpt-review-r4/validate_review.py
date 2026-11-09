from pathlib import Path
import hashlib,json,re
root=Path(__file__).parent;docs=root.parent
report=docs/'GPT验收结论-R4暨DeepSeek返工任务书-R5.md'
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
 for base in [root/'candidate',Path(r'D:\Temp\Lyra-Extension-Epub'),Path(r'D:\Temp\lyra_epub_r4_candidate\extensions\epub')]:verify(base/f['path'],f['sha256'])
for f in identity['host_files']:
 for base in [root/'host-snapshot',Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')]:verify(base/f['name'],f['sha256'])
verify(docs/'candidates-r4/lyra-ext-epub-0.8.0-candidate.zip',identity['archive_sha256'])
for b in identity['binaries']:verify(Path(b['path']),b['sha256'])
for base in [Path(r'D:\Temp\lyra_epub_host_r4'),Path(r'D:\Temp\lyra_epub_r4_candidate')]:verify(base/'WinUIGui.exe','d0f1bb58b07d0b6aeaeb463c841914d9594d0a803e3831fc272a6a5ffed3b335')
reportsha='3168031da48b7c4d127f3a5102641aa9e324cbefd6fd8e8fb0e20da5573af207'
verify(docs/'DeepSeek实施回报-R4.md',reportsha);verify(root/'submitted-report/DeepSeek实施回报-R4.md',reportsha)
for f in identity['evidence']:
 for base in [root/'submitted-evidence',docs/'r4-evidence']:verify(base/f['path'],f['sha256'])
native_manifest=root/'native-supplement-identity.json'
if native_manifest.exists():
 for f in json.loads(native_manifest.read_text(encoding='utf-8')):verify(root/'submitted-native-supplement'/f['path'],f['sha256'])
for f in json.loads((root/'native-corrections-identity.json').read_text(encoding='utf-8')):verify(root/'submitted-native-corrections'/f['path'],f['sha256'])
# Prior accepted frozen inputs are checked against their recorded identities, not against source now legitimately on R4.
r3=root.parent/'gpt-review-r3';old=json.loads((r3/'identity.json').read_text(encoding='utf-8'))
for f in old['files']:verify(r3/'candidate'/f['path'],f['sha256'])
for f in old['host_files']:verify(r3/'host-snapshot'/f['name'],f['sha256'])
verify(docs/'candidates-r3/lyra-ext-epub-0.7.0-candidate.zip',old['archive_sha256'])
for manifest,folder in [('native-supplement-identity.json','submitted-native-supplement'),('compat-addendum-identity.json','submitted-compat-addendum')]:
 for f in json.loads((r3/manifest).read_text(encoding='utf-8')):verify(r3/folder/f['path'],f['sha256'])
placeholders=[s for s in ['签发状态：原生补充与最终校验待完成','待写入最终原生补充核对结果'] if s in text]
result={'report':str(report),'report_sha256':sha(report),'report_lines':len(text.splitlines()),'report_utf8_bytes':len(report.read_bytes()),'links_checked':len(links),'broken_links':broken,'unfinished_markers':placeholders,'frozen_checks':checks,'all_frozen_unchanged':all(c['ok'] for c in checks),'complete':not broken and not placeholders and all(c['ok'] for c in checks)}
target=root/'review-validation.json';i=1
while target.exists():i+=1;target=root/f'review-validation-{i}.json'
target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='frozen_checks'},ensure_ascii=False,indent=2))
print(json.dumps({'validation':str(target),'check_count':len(checks),'failed_checks':[c for c in checks if not c['ok']]},ensure_ascii=False,indent=2))
raise SystemExit(0 if result['complete'] else 1)
