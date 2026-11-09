from pathlib import Path
import hashlib,json,re
root=Path(__file__).parent; docs=root.parent
report=docs/'GPT验收结论-R2暨DeepSeek返工任务书-R3.md'
text=report.read_text(encoding='utf-8')
identity=json.loads((root/'identity.json').read_text(encoding='utf-8'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
links=re.findall(r'\]\(<([^>]+)>\)',text)
broken=[]
for link in links:
 name,_,fragment=link.partition('#')
 p=Path(name)
 if not p.exists():broken.append({'link':link,'reason':'not found'})
 elif fragment.startswith('L') and p.is_file():
  m=re.match(r'L(\d+)',fragment)
  if m:
   try:
    lines=p.read_text(encoding='utf-8').splitlines()
    if int(m[1])>len(lines):broken.append({'link':link,'reason':'line out of range'})
   except UnicodeError:broken.append({'link':link,'reason':'nontext line link'})
checks=[]
for f in identity['files']:
 for base in [root/'candidate',Path(r'D:\Temp\Lyra-Extension-Epub')]:
  p=base/f['path'];checks.append({'path':str(p),'ok':sha(p)==f['sha256']})
for f in identity['host_files']:
 for base in [root/'host-snapshot',Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')]:
  p=base/f['name'];checks.append({'path':str(p),'ok':sha(p)==f['sha256']})
archive=docs/'candidates-r2'/'lyra-ext-epub-0.6.1-candidate.zip'
checks.append({'path':str(archive),'ok':sha(archive)==identity['archive_sha256']})
for b in identity['binaries']:checks.append({'path':b['path'],'ok':sha(Path(b['path']))==b['sha256']})
checks.append({'path':'submitted R2 report snapshot','ok':(root/'submitted-report'/'DeepSeek实施回报-R2.md').read_bytes()==(docs/'DeepSeek实施回报-R2.md').read_bytes()})
result={'report':str(report),'report_sha256':sha(report),'report_lines':len(text.splitlines()),'report_utf8_bytes':len(report.read_bytes()),'links_checked':len(links),'broken_links':broken,'frozen_checks':checks,'all_frozen_unchanged':all(c['ok']for c in checks),'complete':not broken and all(c['ok']for c in checks)}
target=root/'review-validation.json'
assert not target.exists(), 'Preserve validation evidence'
target.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='frozen_checks'},ensure_ascii=False,indent=2))
raise SystemExit(0 if result['complete'] else 1)
