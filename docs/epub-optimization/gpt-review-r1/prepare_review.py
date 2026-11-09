import pathlib, hashlib, zipfile, json, shutil, re
base=pathlib.Path(__file__).parent
source=pathlib.Path(r'D:\Temp\Lyra-Extension-Epub')
archive=base.parent/'candidates'/'lyra-ext-epub-0.6.0-candidate.zip'
report=base.parent/'DeepSeek实施回报-R1.md'
destination=base/'candidate'
results={'archive_sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'files':[]}
assert results['archive_sha256']=='48a4c79058eb1822cb962741650cd404ed58472cb45e3b4c27098fde2321d90e'
with zipfile.ZipFile(archive) as z:
    for item in z.infolist():
        if item.is_dir(): continue
        rel=pathlib.PurePosixPath(item.filename)
        assert not rel.is_absolute() and '..' not in rel.parts
        target=destination/pathlib.Path(*rel.parts)
        data=z.read(item.filename)
        target.parent.mkdir(parents=True,exist_ok=True)
        if target.exists(): assert target.read_bytes()==data
        else: target.write_bytes(data)
        original=source/pathlib.Path(*rel.parts)
        results['files'].append({'path':item.filename,'sha256':hashlib.sha256(data).hexdigest(),'matches_source':original.is_file() and original.read_bytes()==data})
extras=[f for f in results['files'] if not f['matches_source']]
assert all(f['path']=='lib/lib/jszip.min.js' and f['sha256']==hashlib.sha256((source/'lib'/'jszip.min.js').read_bytes()).hexdigest() for f in extras),extras
results['archive_extra_duplicate_files']=extras
for test in (source/'tests').glob('*.cjs'):
    target=destination/'tests'/test.name
    target.parent.mkdir(parents=True,exist_ok=True)
    if not target.exists(): shutil.copyfile(test,target)
    else: assert target.read_bytes()==test.read_bytes()
report_text=report.read_text(encoding='utf-8')
results['host_files']=[]
for name in ['ExtensionHostWindow.xaml','ExtensionHostWindow.xaml.cs','ReaderProtocol.cs','ReaderStateStore.cs']:
    file=pathlib.Path(r'F:\DSH\LocalMediaPlayer-Workspace\app\WinUIGui')/name
    digest=hashlib.sha256(file.read_bytes()).hexdigest()
    expected=re.search(r'([0-9A-F]{64})  '+re.escape(name)+r'\s',report_text)
    assert expected and digest==expected.group(1).lower(),name
    results['host_files'].append({'name':name,'sha256':digest,'matches_report':True})
(base/'identity.json').write_text(json.dumps(results,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'candidate':str(destination),'archive_verified':True,'archive_files':len(results['files']),'runtime_source_files_match':True,'extra_duplicate_files':[f['path'] for f in extras],'host_sources_match_report':True},ensure_ascii=False))
