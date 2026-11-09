from pathlib import Path
import hashlib,json
root=Path(__file__).parent;src=root.parent/'r3-evidence-supplement';dst=root/'submitted-compat-addendum'
paths=[p for p in (src/'compat-baseline-c12fe26').rglob('*') if p.is_file()]
paths += [src/n for n in ['compat-identity-CORRECTION.md','native-supplement-results-v2.json','SHA256SUMS']]
rows=[]
for p in paths:
 data=p.read_bytes();rel=p.relative_to(src);q=dst/rel;q.parent.mkdir(parents=True,exist_ok=True)
 assert not q.exists(),str(q);q.write_bytes(data)
 rows.append({'path':str(rel),'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data)})
q=root/'compat-addendum-identity.json';assert not q.exists();q.write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
expected='88fa8b2e0537e535b3430b2d8b6d1c3ff7085f3599cf2cd2813267448939721a'
p=dst/'compat-baseline-c12fe26/extension-source/index.html';data=p.read_bytes();assert hashlib.sha256(data).hexdigest()==expected
print(json.dumps({'files':len(rows),'baseline_index_bytes':len(data),'baseline_index_lines':len(data.splitlines()),'baseline_matches':True},ensure_ascii=False))
