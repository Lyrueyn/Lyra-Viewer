from pathlib import Path
import re
root=Path(__file__).parent;s=(root/'review-new-boundaries.cjs').read_text(encoding='utf-8')
start=re.search(r'\n[ \t]*try\{\n',s);end=re.search(r'\n[ \t]*\}finally\{fs.writeFileSync',s);assert start and end
prefix=s[:start.start()+1].replace("out=path.join(root,'new-boundary-evidence')","out=path.join(root,'navigation-evidence')").replace("version:2,type:'response'","version:3,type:'response'").replace('protocolVersion:2','protocolVersion:3')
assert "'<p>OLDQUERY target</p>'+paras('TARGET')" in prefix
prefix=prefix.replace("'<p>OLDQUERY target</p>'+paras('TARGET')","'<p>OLDQUERY target</p>'+paras('TARGET'),'<p>FRESH DOCUMENT</p>'+paras('FRESH')")
suffix=s[end.start()+1:].replace('Real Edge, frozen candidate, controlled delays and optional explicitly local bridge fixture','Real Edge frozen R6, resource/prepare gates and explicit exception injection; actual isolated localStorage; local-only finalize bridge')
p=root/'review-navigation.cjs';assert not p.exists();p.write_text(prefix+(root/'navigation-cases.txt').read_text(encoding='utf-8')+suffix,encoding='utf-8');print(p)
