from pathlib import Path
import re
root=Path(__file__).parent;s=(root/'review-new-boundaries.cjs').read_text(encoding='utf-8')
start=re.search(r'\n[ \t]*try\{\n',s);end=re.search(r'\n[ \t]*\}finally\{fs.writeFileSync',s);assert start and end
prefix=s[:start.start()+1].replace("out=path.join(root,'new-boundary-evidence')","out=path.join(root,'visibility-evidence')")
prefix=prefix.replace('for(const[k,v]', '''fixtures.cssmix=await book('CSSMIX',[`<p><span id="case1" style="visibility:hidden!important;visibility:visible">IMPORTANTWORD</span></p><p><span id="case2" style="display:none;display:block">DISPLAYWORD</span></p><p><span id="case3" style="visibility:hidden;visibility:invalid">INVALIDWORD</span></p>`]);
 fixtures.svgmark=await book('SVGMARK',[`<svg xmlns="http://www.w3.org/2000/svg" width="650" height="100"><text x="10" y="50" fill="white" font-size="28">START <tspan style="visibility:hidden">HIDDEN</tspan> TARGET</text></svg>`]);
 for(const[k,v]''')
p=root/'review-visibility.cjs';assert not p.exists();p.write_text(prefix+(root/'visibility-cases.txt').read_text(encoding='utf-8')+s[end.start()+1:],encoding='utf-8');print(p)
