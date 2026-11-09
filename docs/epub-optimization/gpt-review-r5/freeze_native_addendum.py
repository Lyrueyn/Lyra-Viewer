from pathlib import Path
import hashlib,json,re
root=Path(__file__).parent;src=root.parent/'r5-evidence-supplement-addendum';dest=root/'submitted-native-addendum'
sha=lambda b:hashlib.sha256(b).hexdigest()
def freeze(p,b):
 p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists():assert p.read_bytes()==b,str(p)
 else:p.write_bytes(b)
checks=0
sums=src/'SHA256SUMS'
if sums.exists():
 for line in sums.read_text(encoding='utf-8-sig').splitlines():
  if not line.strip():continue
  digest,rel=line.split(None,1);rel=rel.strip().removeprefix('*')
  assert re.fullmatch('[0-9a-fA-F]{64}',digest),line
  p=(src/rel).resolve();p.relative_to(src.resolve())
  assert sha(p.read_bytes())==digest.lower(),rel
  checks+=1
records=[]
for p in sorted(src.rglob('*')):
 if not p.is_file():continue
 b=p.read_bytes();rel=p.relative_to(src);freeze(dest/rel,b);records.append({'path':str(rel),'sha256':sha(b),'bytes':len(b)})
assert records,'empty supplement'
freeze(root/'native-addendum-identity.json',json.dumps(records,ensure_ascii=False,indent=2).encode('utf-8'))
print(json.dumps({'files':len(records),'submitted_sums_verified':checks,'snapshot':str(dest),'identity_sha256':sha((root/'native-addendum-identity.json').read_bytes())},ensure_ascii=False,indent=2))
