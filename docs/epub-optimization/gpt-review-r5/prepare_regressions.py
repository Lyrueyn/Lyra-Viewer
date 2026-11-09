from pathlib import Path
import re,json,hashlib
root=Path(__file__).parent
names=['review-browser.cjs','review-boundaries.cjs','review-anchor.cjs','review-advanced.cjs','review-storage.cjs','review-image-reopen.cjs','review-races.cjs','review-races-v2.cjs','review-image-ack.cjs','review-new-boundaries.cjs','review-new-boundaries-v2.cjs','review-svg-visibility.cjs']
records=[]
for folder,name in [(root.parent/'gpt-review-r4',n) for n in names]+[(root.parent/'gpt-review-r2',n) for n in ['review-late-images.cjs','review-late-images-8s.cjs']]:
 s=(folder/name).read_text(encoding='utf-8');assert not re.search(r'gpt-review-r[1-4]|[A-Z]:[/\\]',s),name
 p=root/name;assert not p.exists(),str(p);p.write_text(s,encoding='utf-8');records.append({'script':name,'unchanged_from':str(folder/name),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()})
 for_read=root/'probe-sources-readable'/name.replace('.cjs','.txt');for_read.parent.mkdir(exist_ok=True);for_read.write_text(s.replace(';',';\n'),encoding='utf-8')
(root/'regression-scripts.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
# Check R4 independently authored model artifacts as well as previously frozen submission evidence.
r4=root.parent/'gpt-review-r4/model-audit';m=json.loads((r4/'deliverables-sha256.json').read_text(encoding='utf-8'));checks=[]
for f in m['files']:
 p=r4/f['file'];checks.append({'file':str(p),'ok':hashlib.sha256(p.read_bytes()).hexdigest()==f['sha256']})
(root/'prior-model-integrity.json').write_text(json.dumps(checks,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'copied_probes':len(records),'r4_model_checks':len(checks),'r4_model_unchanged':all(c['ok'] for c in checks),'failed':[c for c in checks if not c['ok']]},ensure_ascii=False))
assert all(c['ok'] for c in checks)
