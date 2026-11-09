from pathlib import Path
import re, hashlib, json
root=Path(r'F:\DSH\LocalMediaPlayer-Workspace\docs\epub-optimization')
report=root/'GPT验收结论-R1暨DeepSeek返工任务书-R2.md'
text=report.read_text(encoding='utf-8')
links=re.findall(r'\]\(<([^>]+)>\)',text)
missing=[]
invalid_lines=[]
for target in links:
    raw,_,fragment=target.partition('#')
    p=Path(raw)
    if not p.is_file(): missing.append(target)
    elif fragment.startswith('L') and fragment[1:].isdigit():
        total=len(p.read_text(encoding='utf-8-sig').splitlines())
        if int(fragment[1:])>total: invalid_lines.append(target)
source=Path(r'D:\Temp\Lyra-Extension-Epub')
candidate=root/'gpt-review-r1'/'candidate'
runtime=['extension.json','index.html','reader.css','reader.js','epub-book.js','paginator.js','reader-state.js','search.js','lib/jszip.min.js']
changed=[]
for relative in runtime:
    a=(source/relative).read_bytes();b=(candidate/relative).read_bytes()
    if a!=b: changed.append(relative)
result={'report':str(report),'lines':len(text.splitlines()),'utf8Bytes':len(text.encode('utf-8')),'links':len(links),'missingLinks':missing,'invalidLineLinks':invalid_lines,'runtimeSourcesStillMatchFrozenCandidate':not changed,'changedRuntimeSources':changed,'reportSha256':hashlib.sha256(report.read_bytes()).hexdigest()}
print(json.dumps(result,ensure_ascii=False,indent=2))
(root/'gpt-review-r1'/'review-validation.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
raise SystemExit(1 if missing or invalid_lines or changed else 0)
