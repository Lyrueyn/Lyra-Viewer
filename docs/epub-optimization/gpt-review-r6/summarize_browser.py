from pathlib import Path
import json
root=Path(__file__).parent
load=lambda p:json.loads((root/p).read_text(encoding='utf-8'))
pick=lambda s,ks:{k:s.get(k) for k in ks} if isinstance(s,dict) else s
K=['ready','busy','nav','spine','prepared','pgReady','pgSpine','page','count','finalizing','error']
out={}
nav=load('navigation-evidence/results.json')['results']
for r in nav:
 item={'defect':r.get('defect'),'probeError':r.get('probeError')}
 for k in ['before','after','next','newer','late','atEvent']:
  if k in r:
   s=pick(r[k],K)
   if isinstance(r[k],dict):s['text']=(r[k].get('text') or '')[:24];s['savedOffset']=((r[k].get('saved') or {}).get('position') or {}).get('locator',{}) and (r[k]['saved']['position'].get('locator') or {}).get('textOffset')
   item[k]=s
 if r.get('ack'):item['ack']=r['ack']
 out[r['id']]=item
for r in load('visibility-evidence/results.json')['results']:out[r['id']]={'defect':r.get('defect'),'counts':r.get('counts'),'items':(r.get('before') or {}).get('items'),'marks':(r.get('after') or {}).get('marks')}
stages=load('new-stage-evidence/results.json')['results']
for r in stages:out['R6rerun_'+r['id']]={'defect':r.get('defect'),'probeError':r.get('probeError')}
old=[]
for f in ['browser-evidence/review-results.json','boundary-evidence/boundary-results.json','anchor-evidence/anchor-result.json','advanced-evidence/results.json','storage-evidence/results.json','image-reopen-evidence/result.json','race-evidence/results.json','race-evidence-v2/results.json','image-ack-evidence/results.json','new-boundary-evidence/results.json','new-boundary-evidence-v2/results.json','svg-visibility-evidence/results.json']:
 d=load(f);rs=d if isinstance(d,list) else d.get('results',[d])
 for r in rs:old.append({'file':f,'id':r.get('id'),'defect':r.get('defect'),'probeError':bool(r.get('probeError'))})
out['_legacy']={'count':len(old),'defects':[x for x in old if x['defect'] is True],'probeErrors':[x for x in old if x['probeError']]}
(root/'browser-summary.json').write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(out,ensure_ascii=False,indent=1))
