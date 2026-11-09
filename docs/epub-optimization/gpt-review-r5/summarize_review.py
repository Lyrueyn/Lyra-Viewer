from pathlib import Path
import json
root=Path(__file__).parent
files=['browser-evidence/review-results.json','boundary-evidence/boundary-results.json','anchor-evidence/anchor-result.json','advanced-evidence/results.json','storage-evidence/results.json','image-reopen-evidence/result.json','race-evidence/results.json','race-evidence-v2/results.json','image-ack-evidence/results.json','new-boundary-evidence/results.json','new-boundary-evidence-v2/results.json','svg-visibility-evidence/results.json','new-stage-evidence/results.json','late-image-evidence/result.json','late-image-evidence-8s/result.json','late-lazy-evidence/result.json']
rows=[]
for name in files:
 d=json.loads((root/name).read_text(encoding='utf-8'));rs=d if isinstance(d,list) else d.get('results',[d])
 for r in rs:
  note=''
  if r.get('id')=='R03_FONT_REOPEN_ANCHOR':note='Obsolete textNodePath oracle; independent DOM Range anchor cases are authoritative.'
  if r.get('id')=='B14_FAILED_SAVE_SWITCH_LOSES_PENDING_POSITION':note='Old modified/key-only oracle ignores failedSaves; original-host S03 is authoritative.'
  if r.get('id','').startswith(('B11_LATE_IMAGES','B12_LATE_IMAGES')):note='Images were all complete at ready: ordinary last-page pass, not true post-ready late-resource coverage.'
  if r.get('coverage') is False:note='Coverage not established: no incomplete images at ready; not a product PASS or FAIL.'
  rows.append({'file':name,'id':r.get('id'),'defect':r.get('defect'),'probeError':r.get('probeError'),'note':note})
summary={'observations':len(rows),'rows':rows,'actual_failures':[r for r in rows if r['defect'] is True and not r['note']],'probe_errors':[r for r in rows if r['probeError']]}
p=root/'browser-summary.json';assert not p.exists();p.write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
stages=json.loads((root/'new-stage-evidence/results.json').read_text(encoding='utf-8'))['results']
compact=[]
for r in stages:
 if r.get('probeError'):compact.append(r);continue
 item={'id':r['id'],'defect':r['defect']}
 for key in ['ack','actual','target','hidden','during']:
  if key in r and key!='during':item[key]=r[key]
 if r['id'].startswith('E01'):
  item['cancelled']={k:r['cancelled'].get(k) for k in ['ready','spine','paginatorReady','paginatorSpine','page','count']};item['afterNextPosition']=r['afterNext']['saved'].get('position')
 if r['id'].startswith('E02'):
  item['beforeLocator']=r['before']['stable']['locator'];item['closedPosition']=r['closed']['saved']['position']
 if r['id'].startswith(('E03','E05')):item['before']=r['before'];item['after']=r.get('after')
 if r['id'].startswith('E06'):
  item['during']=r['during'];item['savedPosition']=r['after']['saved']['position'];item['reopenedPage']=r['reopened']['page']
 if r['id'].startswith('E07'):item['atAckReady']=r['atAck']['ready'];item['lateReady']=r['late']['ready'];item['error']=r['error']
 compact.append(item)
(root/'new-stage-evidence/summary.json').write_text(json.dumps(compact,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'observations':len(rows),'new_stage_results':compact},ensure_ascii=False,indent=2))
