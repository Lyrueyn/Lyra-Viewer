from pathlib import Path
import re
root=Path(__file__).parent;s=(root/'review-new-boundaries.cjs').read_text(encoding='utf-8')
start=re.search(r'\n[ \t]*try\{\n',s);end=re.search(r'\n[ \t]*\}finally\{fs.writeFileSync',s);assert start and end
prefix=s[:start.start()+1].replace("out=path.join(root,'new-boundary-evidence')","out=path.join(root,'new-stage-evidence')").replace("version:2,type:'response'","version:3,type:'response'").replace('protocolVersion:2','protocolVersion:3')
prefix=prefix.replace('for(const[k,v]', '''  fixtures.svgpaint=await book('SVGPAINT',[`<svg xmlns="http://www.w3.org/2000/svg" width="650" height="100"><text id="paint-text" x="10" y="50" fill="white" font-size="28">VISIBLE TARGET</text></svg>`]);
  fixtures.cssorder=await book('CSSORDER',[`<p><span id="visible" style="visibility:hidden;visibility:visible">TARGET</span><span id="hidden" style="visibility:visible;visibility:hidden">HIDDEN</span></p>`]);
  for(const[k,v]''')
suffix=s[end.start()+1:].replace("Real Edge, frozen candidate, controlled delays and optional explicitly local bridge fixture","Real Edge R5, original modules; explicit resource/File/RAF gates; local-only bridge and actual isolated localStorage")
text=prefix+(root/'new-stage-cases.js.txt').read_text(encoding='utf-8')+(root/'new-stage-more-cases.js.txt').read_text(encoding='utf-8')+suffix
p=root/'review-new-stages.cjs';assert not p.exists();p.write_text(text,encoding='utf-8')
print(p)
