from pathlib import Path
root=Path(__file__).parent
s=(root/'review-new-boundaries.cjs').read_text(encoding='utf-8')
s=s.replace("'new-boundary-evidence'","'new-boundary-evidence-v2'")
s=s.replace('async function test(id,fn,bridge=false){','async function test(id,fn,bridge=false){if(!/^D0[456]_/.test(id))return;')
s=s.replace('</manifest><spine>','<item id="shared" href="shared.png" media-type="image/png"/></manifest><spine>')
s=s.replace("return z.generateAsync({type:'nodebuffer'});", "z.file('OPS/shared.png',Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lzcxkAAAAABJRU5ErkJggg==','base64'));return z.generateAsync({type:'nodebuffer'});")
marker=' for(const[k,v]of Object.entries(fixtures))'
assert s.count(marker)==1
s=s.replace(marker,'''
 fixtures.hiddenflow=await book('HIDDENFLOW',['<p>START <span style="display:none">HIDDEN</span> TARGET</p>']);
 fixtures.svgflow=await book('SVGFLOW',['<svg xmlns="http://www.w3.org/2000/svg" width="600" height="100"><text x="0" y="30">START <tspan style="visibility:hidden">HIDDEN </tspan>TARGET</text></svg>']);
 fixtures.shared=await book('SHARED',['<p>OLD_VISIBLE</p><img id="shared" src="shared.png" width="500" height="600"/>','<p>NEW_TARGET</p><img id="shared" src="shared.png" width="500" height="600"/>']);
'''+marker)
new=r'''
  for(const [id,key] of [['D04_HIDDEN_HTML_OFFSET_DIVERGENCE','hiddenflow'],['D05_HIDDEN_SVG_OFFSET_DIVERGENCE','svgflow']]) await test(id,async p=>{await open(p,key);const before=await p.evaluate(async()=>({plain:await __lyraDebug.state.book.getSectionPlainText(0),visible:__lyraDebug.paginator.doc.body.innerText,renderText:__lyraDebug.paginator.content.textContent}));await search(p,'TARGET');await p.locator('.search-result').first().click();await p.waitForFunction(()=>__lyraDebug.state.ready&&!__lyraDebug.state.busy&&__lyraDebug.state.activeSearchResult);const after=await p.evaluate(()=>({visible:__lyraDebug.paginator.doc.body.innerText,renderText:__lyraDebug.paginator.content.textContent,highlights:[...__lyraDebug.paginator.doc.querySelectorAll('.lyra-search-mark')].map(x=>x.textContent),active:__lyraDebug.state.activeSearchResult,status:document.getElementById('statusText').textContent}));await p.screenshot({path:path.join(out,key+'.png')});return{before,after,defect:after.highlights.join('')!=='TARGET',note:'No timing/fault injection. Original source model and prepared rendered document differ after sanitizer removes style.'};});
  await test('D06_FAILED_SHARED_IMAGE_PREPARE_REVOKES_VISIBLE_RESOURCE',async p=>{await open(p,'shared');const before=await p.evaluate(async()=>{const b=__lyraDebug.state.book,img=__lyraDebug.paginator.doc.querySelector('img');const orig=b.sectionTitle.bind(b);b.sectionTitle=async index=>{if(index===1)throw new Error('controlled metadata failure after shared resource acquisition');return orig(index);};return{spine:__lyraDebug.state.spineIndex,text:__lyraDebug.paginator.doc.body.innerText,url:img.src,fetchOK:(await fetch(img.src)).ok,owners:[...b._taskResources.keys()]};});await p.locator('#chapterNext').click();await p.waitForFunction(()=>document.getElementById('errorText').textContent.includes('controlled metadata failure'));const after=await p.evaluate(async()=>{const b=__lyraDebug.state.book,img=__lyraDebug.paginator.doc.querySelector('img');let fetchOK=false;try{fetchOK=(await fetch(img.src)).ok;}catch{}return{spine:__lyraDebug.state.spineIndex,ready:__lyraDebug.state.ready,text:__lyraDebug.paginator.doc.body.innerText,url:img.src,fetchOK,naturalWidth:img.naturalWidth,resources:[...b._resources.keys()],owners:[...b._taskResources.keys()]};});return{before,after,defect:before.fetchOK&&!after.fetchOK&&before.url===after.url,note:'Injected section-title failure only after same-book resource acquisition; displayed old chapter remains, its referenced blob is revoked. Does not assert decoded pixels instantly disappear.'};});
'''
marker=' }finally{fs.writeFileSync'
assert s.count(marker)==1
s=s.replace(marker,new+marker)
p=root/'review-new-boundaries-v2.cjs';assert not p.exists();p.write_text(s,encoding='utf-8')
print('Created three independent R4 visible-model/shared-resource Edge probes.')
