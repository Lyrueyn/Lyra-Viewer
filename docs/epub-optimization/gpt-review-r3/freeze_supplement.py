from pathlib import Path
import hashlib,json
root=Path(__file__).parent
source=root.parent/'r3-evidence-supplement';target=root/'submitted-native-supplement'
rows=[]
for p in source.rglob('*'):
 if not p.is_file():continue
 rel=p.relative_to(source);data=p.read_bytes();dest=target/rel;dest.parent.mkdir(parents=True,exist_ok=True)
 if dest.exists():assert dest.read_bytes()==data,str(dest)
 else:dest.write_bytes(data)
 rows.append({'path':str(rel),'sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data)})
manifest=root/'native-supplement-identity.json';content=json.dumps(rows,ensure_ascii=False,indent=2).encode('utf-8')
if manifest.exists():assert manifest.read_bytes()==content
else:manifest.write_bytes(content)
print(json.dumps({'files':len(rows),'frozen_at':str(target),'source_unchanged':True},ensure_ascii=False))
