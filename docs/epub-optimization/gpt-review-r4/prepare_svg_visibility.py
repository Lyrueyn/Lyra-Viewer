from pathlib import Path
root=Path(__file__).parent
s=(root/'review-new-boundaries.cjs').read_text(encoding='utf-8').replace("'new-boundary-evidence'","'svg-visibility-evidence'")
s=s.replace('async function test(id,fn,bridge=false){','async function test(id,fn,bridge=false){if(!id.startsWith("D07_"))return;')
s=s.replace(' for(const[k,v]of Object.entries(fixtures))', ''' fixtures.svg=await book('SVG_VISIBILITY',['<svg xmlns="http://www.w3.org/2000/svg" width="600" height="100"><g visibility="hidden"><text id="visible-child" visibility="visible" x="0" y="35">OVERRIDE_VISIBLE</text></g></svg>']);
 for(const[k,v]of Object.entries(fixtures))''')
test=r'''
  await test('D07_SVG_CHILD_VISIBLE_BUT_SEARCH_OMITS',async p=>{await open(p,'svg');const before=await p.evaluate(async()=>{const d=__lyraDebug.paginator.doc,e=d.querySelector('#visible-child');return{plain:await __lyraDebug.state.book.getSectionPlainText(0),innerText:d.body.innerText,visibility:d.defaultView.getComputedStyle(e).visibility,bounds:{width:e.getBoundingClientRect().width,height:e.getBoundingClientRect().height}};});await search(p,'OVERRIDE_VISIBLE');const after=await p.evaluate(()=>({buttons:document.querySelectorAll('.search-result').length,status:document.getElementById('searchInfo').textContent}));await p.screenshot({path:path.join(out,'visible-child.png')});return{before,after,defect:before.visibility==='visible'&&before.bounds.width>0&&after.buttons===0,note:'SVG parent visibility:hidden is inherited but child explicitly overrides to visible; no use references and no timing/fault injection.'};});
'''
s=s.replace(' }finally{fs.writeFileSync',test+' }finally{fs.writeFileSync')
p=root/'review-svg-visibility.cjs';assert not p.exists();p.write_text(s,encoding='utf-8')
