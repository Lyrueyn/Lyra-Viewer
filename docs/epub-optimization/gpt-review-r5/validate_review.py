from pathlib import Path
import hashlib,json,re,subprocess,datetime,zipfile
root=Path(__file__).parent;docs=root.parent
report=docs/'GPT验收结论-R5暨DeepSeek返工任务书-R6.md'
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
checks=[]
def verify(p,d,group):
 p=Path(p);checks.append({'group':group,'path':str(p),'ok':p.is_file() and sha(p)==d.lower()})
identity=load(root/'identity.json')
source=Path(r'D:\Temp\Lyra-Extension-Epub');runtime=Path(r'D:\Temp\lyra_epub_r5_candidate');host=Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')
archive=docs/'candidates-r5/lyra-ext-epub-0.9.0-candidate.zip';verify(archive,identity['archive_sha256'],'candidate-zip')
for f in identity['files']:
 for base,group in [(root/'candidate','frozen-candidate'),(source,'live-source'),(runtime/'extensions/epub','runtime-extension')]:verify(base/f['path'],f['sha256'],group)
for f in identity['host_files']:
 for base,group in [(root/'host-snapshot','frozen-host'),(host,'live-host')]:verify(base/f['name'],f['sha256'],group)
for f in identity['binaries']:verify(f['path'],f['sha256'],'runtime-binary')
for f in identity['evidence']:
 verify(root/'submitted-evidence'/f['path'],f['sha256'],'frozen-submission');verify(docs/'r5-evidence'/f['path'],f['sha256'],'original-submission')
for p in [root/'submitted-report/DeepSeek实施回报-R5.md',docs/'DeepSeek实施回报-R5.md']:verify(p,'47074ac5a43e1da58814dfefad8eb256415c7ae25a5b63bfdc072d5b95cfba2e','r5-report')
for key in ['supplement','addendum']:
 for f in load(root/f'native-{key}-identity.json'):
  verify(root/f'submitted-native-{key}'/f['path'],f['sha256'],f'frozen-native-{key}')
  verify(docs/('r5-evidence-supplement' if key=='supplement' else 'r5-evidence-supplement-addendum')/f['path'],f['sha256'],f'original-native-{key}')
for f in load(root/'prior-integrity.json')['checks']:verify(f['path'],f['expected_sha256'],'prior-rounds')
for f in load(docs/'gpt-review-r4/model-audit/deliverables-sha256.json')['files']:verify(docs/'gpt-review-r4/model-audit'/f['file'],f['sha256'],'r4-model-original')
for f in load(root/'host-audit/files.json'):verify(root/'host-audit'/f['path'],f['sha256'],'r5-host-deliverables')
for f in load(root/'model-audit/deliverables-sha256.json')['files']:verify(root/'model-audit'/f['file'],f['sha256'],'r5-model-deliverables')
text=report.read_text(encoding='utf-8');targets=re.findall(r'\]\(<([^>]+)>\)',text)
links=[{'target':t,'ok':Path(t.split('#')[0]).exists()} for t in targets]
head=subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()
branch=subprocess.check_output(['git','-C',str(source),'branch','--show-current'],text=True).strip()
status=subprocess.check_output(['git','-C',str(source),'status','--porcelain'],text=True)
assert head=='76ced4c17efa4526fbc8c00e3fff5261959a7fe7' and branch=='r5' and not status,(head,branch,status)
# Compare actual serialized before/restart state, excluding PowerShell provider metadata.
a08=root/'submitted-native-supplement/native/a08';before=load(a08/'09-combined-state-46fbd875a7978c82b7eecbb775844269176cf1abba525090fda9ba65b4ad7925.json')
after_identity=load(a08/'10-combined-app-restart-identity.json');after=json.loads(after_identity['bookState']['value'])
prefs=[]
for k,v in after_identity.items():
 if isinstance(v,dict) and isinstance(v.get('value'),str):
  try:d=json.loads(v['value'])
  except ValueError:continue
  if isinstance(d,dict) and ('preferences' in d or 'lineHeight' in d):prefs.append({'key':k,'lineHeight':d.get('preferences',d).get('lineHeight')})
a08_result={'before_after_state_equal':before==after,'before_offset':before['position']['locator']['textOffset'],'after_offset':after['position']['locator']['textOffset'],'pageIndex':after['position']['locator']['pageIndex'],'bookmarks':len(after['bookmarks']),'restart_pid':after_identity['pid'],'restart_preferences':prefs}
assert a08_result['before_after_state_equal'] and a08_result['after_offset']==1758 and a08_result['bookmarks']==1,a08_result
summary={'verifiedAt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'report':str(report),'report_sha256':sha(report),'report_bytes':report.stat().st_size,'report_lines':len(text.splitlines()),'source':{'head':head,'branch':branch,'clean':not status},'link_count':len(links),'all_links_exist':all(x['ok'] for x in links),'integrity_checks':len(checks),'all_inputs_unchanged':all(x['ok'] for x in checks),'a08_combined':a08_result,'failed_checks':[x for x in checks if not x['ok']],'bad_links':[x for x in links if not x['ok']],'checks':checks,'links':links}
(root/'review-validation.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in summary.items() if k not in ['checks','links']},ensure_ascii=False,indent=2))
assert summary['all_links_exist'] and summary['all_inputs_unchanged']
