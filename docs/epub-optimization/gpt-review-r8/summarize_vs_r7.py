from pathlib import Path
import json,sys
root=Path(__file__).parent;r6=root.parent/'gpt-review-r7'
def load(p):
 try:return json.loads(p.read_text(encoding='utf-8-sig'))
 except Exception as e:return {'_err':str(e)}
def rows(d):
 if isinstance(d,list):return d
 if isinstance(d,dict):
  for k in ('results','cases','probes'):
   if isinstance(d.get(k),list):return d[k]
 return []
def verdict(x):
 if not isinstance(x,dict):return '?'
 if x.get('probeError') or x.get('error') and 'defect' not in x:return 'PROBE_ERROR'
 if 'defect' in x:return 'DEFECT' if x['defect'] else 'ok'
 for k in ('pass','ok'):
  if k in x:return 'ok' if x[k] else 'FAIL'
 if 'verdict' in x:return str(x['verdict'])
 return '?'
def index(base):
 out={}
 for p in sorted(base.glob('*-evidence*/*.json')):
  for x in rows(load(p)):
   if isinstance(x,dict):
    i=x.get('id') or x.get('name') or x.get('case')
    if i:out[(p.parent.name,i)]=(verdict(x),x)
 return out
a,b=index(r6),index(root)
summary={'r7_total':len(b),'by_verdict':{}}
for k,(v,_) in b.items():summary['by_verdict'][v]=summary['by_verdict'].get(v,0)+1
changes=[];bad=[]
for k in sorted(set(a)|set(b)):
 va=a.get(k,('-',None))[0];vb=b.get(k,('-',None))[0]
 if va!=vb:changes.append(f'{k[0]}/{k[1]}: R6={va} -> R7={vb}')
 if vb not in('ok','-'):
  x=b[k][1];bad.append({'k':f'{k[0]}/{k[1]}','v':vb,'detail':{kk:x[kk] for kk in list(x)[:8] if kk not in('id',)}})
print(json.dumps(summary,ensure_ascii=False))
print('--- verdict changes'); print('\n'.join(changes))
print('--- non-ok in R7')
for x in bad:print(json.dumps(x,ensure_ascii=False)[:900])

