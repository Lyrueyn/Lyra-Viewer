from pathlib import Path
import json
root=Path(__file__).parent
def rows(d):
 if isinstance(d,list):return d
 for k in ['results','cases','assertions','observations']:
  if isinstance(d,dict) and isinstance(d.get(k),list):return d[k]
 return []
def verdict(r):
 for k in ['status','verdict','result','classification']:
  v=r.get(k)
  if isinstance(v,str):return v.upper()
 if r.get('probeError') or r.get('harnessError'):return 'PROBE_ERROR'
 for k in ['pass','ok','satisfied']:
  if isinstance(r.get(k),bool):return 'PASS' if r[k] else 'FAIL'
 if isinstance(r.get('defect'),bool):return 'FAIL' if r['defect'] else 'PASS'
 return 'UNKNOWN'
out={}
for base in ['host-audit','model-audit']:
 for p in sorted((root/base).rglob('*results.json')):
  try:d=json.loads(p.read_text(encoding='utf-8-sig'))
  except Exception as e:out[str(p.relative_to(root))]={'readError':str(e)};continue
  rs=rows(d);c={}
  fails=[]
  for r in rs:
   if not isinstance(r,dict):continue
   v=verdict(r);c[v]=c.get(v,0)+1
   if v not in ('PASS','OBS','OBSERVATION'):fails.append({'id':r.get('id') or r.get('name'),'v':v,'note':str(r.get('note') or r.get('reason') or r.get('summary') or '')[:220]})
  out[str(p.relative_to(root))]={'count':len(rs),'verdicts':c,'nonpass':fails}
(root/'partial-audit-index.json').write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding='utf-8')
for k,v in out.items():
 print(k,v.get('count'),v.get('verdicts'))
 for f in v.get('nonpass',[])[:40]:print('   ',f['v'],f['id'],'|',f['note'][:160])
