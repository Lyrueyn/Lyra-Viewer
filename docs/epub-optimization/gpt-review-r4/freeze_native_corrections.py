from pathlib import Path
import hashlib,json
root=Path(__file__).parent;src=root.parent/'r4-evidence-supplement';dest=root/'submitted-native-corrections';dest.mkdir(exist_ok=True)
expected={'CORRECTIONS-A22-A-TABLE.md':'f72a159cd2681f8031093bd2794161c8ef2172583fefbe95ff25bc27729af03e','CORRECTIONS-A22-A-TABLE.sha256':'47d4c852f89cb4b7576e637d205ef830a3cc8e7051f24a24918d48faa539a0be'}
records=[]
for name,digest in expected.items():
 b=(src/name).read_bytes();assert hashlib.sha256(b).hexdigest()==digest
 p=dest/name
 if p.exists():assert p.read_bytes()==b
 else:p.write_bytes(b)
 records.append({'path':name,'sha256':digest,'bytes':len(b)})
p=root/'native-corrections-identity.json';data=json.dumps(records,ensure_ascii=False,indent=2).encode('utf-8')
if p.exists():assert p.read_bytes()==data
else:p.write_bytes(data)
print(json.dumps({'files':len(records),'verified':True,'snapshot':str(dest)},ensure_ascii=False))
