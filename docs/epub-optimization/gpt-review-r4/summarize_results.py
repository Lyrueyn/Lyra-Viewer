from pathlib import Path
import json
root=Path(__file__).parent
inputs=['browser-evidence/review-results.json','boundary-evidence/boundary-results.json','anchor-evidence/anchor-result.json','advanced-evidence/results.json','storage-evidence/results.json','image-reopen-evidence/result.json','race-evidence/results.json','race-evidence-v2/results.json','image-ack-evidence/results.json','new-boundary-evidence/results.json','new-boundary-evidence-v2/results.json','svg-visibility-evidence/results.json']
rows=[]
for rel in inputs:
 p=root/rel;obj=json.loads(p.read_text(encoding='utf-8'))
 items=obj if isinstance(obj,list) else obj.get('results',[obj])
 for x in items:
  if not isinstance(x,dict):continue
  legacy=''
  if str(x.get('id','')).startswith('R03_'):legacy='Obsolete test references removed textNodePath; B01 uses independent DOM Range and is authoritative.'
  if str(x.get('id','')).startswith('B14_'):legacy='Legacy oracle inspects modified/old saved key but not failedSaves; use C02 refinement and original-host recovery tests.'
  rows.append({'file':rel,'id':x.get('id'),'defect':x.get('defect'),'probeError':x.get('probeError'),'legacy_note':legacy})
result={'observations':len(rows),'flagged_defects':[x for x in rows if x['defect']],'probe_errors':[x for x in rows if x['probeError']],'rows':rows}
t=root/'browser-summary-final.json';assert not t.exists();t.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='rows'},ensure_ascii=False,indent=2))
