from pathlib import Path
import hashlib,json,re,difflib
root=Path(__file__).parent;old=root.parent/'gpt-review-r5'
names=['review-browser.cjs','review-boundaries.cjs','review-anchor.cjs','review-advanced.cjs','review-storage.cjs','review-image-reopen.cjs','review-races.cjs','review-races-v2.cjs','review-image-ack.cjs','review-new-boundaries.cjs','review-new-boundaries-v2.cjs','review-svg-visibility.cjs','review-new-stages.cjs']
records=[]
for name in names:
 b=(old/name).read_bytes();s=b.decode('utf-8');assert not re.search(r'gpt-review-r[1-5]|[A-Z]:[/\\]',s),name
 p=root/name;assert not p.exists();p.write_bytes(b);records.append({'path':name,'unchanged_from':str(old/name),'sha256':hashlib.sha256(b).hexdigest()})
(root/'regression-scripts.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')
for name in ['reader.js','paginator.js','reader-state.js','epub-book.js']:
 diff=''.join(difflib.unified_diff((old/'candidate'/name).read_text(encoding='utf-8').splitlines(True),(root/'candidate'/name).read_text(encoding='utf-8').splitlines(True),fromfile='R5/'+name,tofile='R6/'+name))
 p=root/'source-diffs'/f'{name}.diff';p.parent.mkdir(exist_ok=True);p.write_text(diff,encoding='utf-8')
print({'copied_probes':len(records)})
