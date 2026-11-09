/* Frozen R4 original browser code. Explicit fault/delay fixtures, isolated origins. */
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE);
const root=__dirname,app=path.resolve('D:/Temp/Lyra-Extension-Epub'),out='F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/r6-evidence/new-stage';fs.mkdirSync(out,{recursive:true});
const Zip=require(path.join(app,'lib/jszip.min.js'));
async function book(title,chapters){const z=new Zip();z.file('mimetype','application/epub+zip');z.file('META-INF/container.xml','<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/book.opf"/></rootfiles></container>');z.file('OPS/book.opf',`<package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>${title}</dc:title></metadata><manifest>${chapters.map((_,i)=>`<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join('')}</manifest><spine>${chapters.map((_,i)=>`<itemref idref="c${i}"/>`).join('')}</spine></package>`);chapters.forEach((body,i)=>z.file(`OPS/c${i}.xhtml`,`<html xmlns="http://www.w3.org/1999/xhtml"><head><title>${title}-${i}</title></head><body>${body}</body></html>`));return z.generateAsync({type:'nodebuffer'});}
const paras=prefix=>Array.from({length:100},(_,i)=>`<p>${prefix} paragraph ${i}: independent visible reading and position preservation. 这是用于验证实际正文的长段落。</p>`).join('');
(async()=>{
 const q='CROSSBOUNDARY',edge=65536+Math.min(8192,Math.max(256,q.length*4+256));
 const fixtures={boundary:await book('BOUNDARY',[`<p>before ${q} after</p>`,`<p>${'a'.repeat(edge-5)}${q} tail</p>`,`<p>${'a'.repeat(edge+30)}${q} tail</p>`]),search:await book('SEARCH',['<p>START DOCUMENT</p>'+paras('START'),'<p>OLDQUERY target</p>'+paras('TARGET')]),normal:await book('NORMAL',[paras('NORMAL'),paras('NEXT')])};
   fixtures.svgpaint=await book('SVGPAINT',[`<svg xmlns="http://www.w3.org/2000/svg" width="650" height="100"><text id="paint-text" x="10" y="50" fill="white" font-size="28">VISIBLE TARGET</text></svg>`]);
  fixtures.cssorder=await book('CSSORDER',[`<p><span id="visible" style="visibility:hidden;visibility:visible">TARGET</span><span id="hidden" style="visibility:visible;visibility:hidden">HIDDEN</span></p>`]);
  for(const[k,v]of Object.entries(fixtures))fs.writeFileSync(path.join(out,k+'.epub'),v);
 const server=http.createServer((req,res)=>{let p=new URL(req.url,'http://local').pathname;if(p.startsWith('/fixtures/')){const b=fixtures[path.basename(p,'.epub')];res.writeHead(b?200:404,{'Content-Type':'application/epub+zip'});return res.end(b);}const f=path.join(app,p==='/'?'index.html':decodeURIComponent(p));if(!f.startsWith(app)||!fs.existsSync(f)||!fs.statSync(f).isFile()){res.writeHead(404);return res.end();}res.writeHead(200,{'Content-Type':({'.js':'application/javascript','.css':'text/css','.html':'text/html'})[path.extname(f)]||'application/octet-stream'});fs.createReadStream(f).pipe(res);});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const base='http://127.0.0.1:'+server.address().port,browser=await chromium.launch({headless:true,executablePath:process.env.LYRA_BROWSER_PATH}),results=[];
 async function test(id,fn,bridge=false){const c=await browser.newContext({viewport:{width:1400,height:900}});await c.addInitScript(()=>window.__LYRA_TEST_MODE__=true);if(bridge)await c.addInitScript(()=>{window.__hostListeners=[];window.__hostPosts=[];window.chrome=window.chrome||{};window.chrome.webview={addEventListener:(type,fn)=>{if(type==='message')window.__hostListeners.push(fn);},postMessage:msg=>{window.__hostPosts.push(msg);if(msg.type==='hello')queueMicrotask(()=>window.__hostListeners.forEach(fn=>fn({data:{protocol:'lyra-reader',version:3,type:'response',requestId:msg.requestId,sessionId:msg.sessionId,ok:true,result:{protocolVersion:3,capabilities:{persistentState:false}}}})));}};window.__deliverHost=data=>window.__hostListeners.forEach(fn=>fn({data}));});const p=await c.newPage();p.setDefaultTimeout(20000);try{const value=await fn(p);results.push({id,...value});console.log(JSON.stringify({id,...value}));}catch(e){results.push({id,probeError:e.message});console.log(JSON.stringify({id,probeError:e.message}));}finally{await c.close();}}
 const ready=p=>p.waitForFunction(()=>window.__lyraDebug?.state.ready&&!window.__lyraDebug?.state.busy);
 async function open(p,k){await p.goto(base+'/index.html?book=fixtures/'+k+'.epub&name='+k+'.epub');await ready(p);}
 async function search(p,q){await p.locator('#findBtn').click();await p.locator('#searchInput').fill(q);await p.locator('#searchInput').press('Enter');await p.waitForFunction(()=>!(/正在/.test(document.getElementById('searchInfo').textContent)));}
  const snapshot = p => p.evaluate(() => {
    const s=__lyraDebug.state, pg=__lyraDebug.paginator;
    const saved=localStorage.getItem('lyra_epub_state_'+s.bookId);
    return {ready:s.ready,busy:s.busy,modified:s.modified,spine:s.spineIndex,preparedIndex:s.prepared?.index,owner:s.prepared?.owner,
      paginatorReady:pg.ready,paginatorSpine:pg.sectionIndex,page:pg.pageIndex,count:pg.pageCount,
      text:pg.doc?.body.innerText.slice(0,100),stable:s.lastStablePosition,saved:saved?JSON.parse(saved):null,
      footer:document.getElementById('pageInfo')?.textContent,finalizing:s.finalizing};
  });
  async function resourceStage(p) {
    await open(p,'search');
    await p.locator('#pageInput').fill('5');await p.locator('#pageInput').press('Enter');
    await p.waitForTimeout(500);await search(p,'OLDQUERY');
    await p.evaluate(()=>{const pg=__lyraDebug.paginator,orig=pg._waitForResources.bind(pg);pg._waitForResources=async()=>{
      if(pg.sectionIndex===1&&!window.__gateUsed){window.__gateUsed=true;window.__waiting=true;await new Promise(r=>window.__release=r);}return orig();
    };});
    await p.locator('#pageNext').click();
    const before=await snapshot(p);
    await p.locator('.search-result').first().click();await p.waitForFunction(()=>window.__waiting);
    return {before,during:await snapshot(p)};
  }
  try {
    for (const kind of ['input','close']) await test('E01_RESOURCE_CANCEL_'+kind.toUpperCase(),async p=>{
      const {before,during}=await resourceStage(p);
      if(kind==='input') await p.locator('#searchInput').fill('NEWQUERY');else await p.locator('#searchClose').click();
      await p.evaluate(()=>window.__release());await p.waitForTimeout(180);
      const cancelled=await snapshot(p);await p.screenshot({path:path.join(out,'resource-cancel-'+kind+'.png')});
      await p.locator('#pageNext').click();await p.waitForTimeout(600);const afterNext=await snapshot(p);
      return {before,during,cancelled,afterNext,defect:cancelled.ready&&(!cancelled.paginatorReady||cancelled.spine!==cancelled.paginatorSpine),
        note:'Original Edge reader/paginator. Only resource-wait completion gated. Cancellation is real UI input/close; next uses real pageNext and localStorage. Full document/position coherence, not only lack of goToPage/highlight.'};
    });
    await test('E02_FINALIZE_AFTER_DOCUMENT_BEFORE_LAYOUT',async p=>{
      const {before,during}=await resourceStage(p);
      await p.evaluate(()=>window.__deliverHost({type:'host.finalize',requestId:'close-after-document',sessionId:__lyraDebug.state.client.sessionId}));
      await p.waitForFunction(()=>window.__hostPosts.some(m=>m.type==='host.finalized'));
      const ack=await p.evaluate(()=>window.__hostPosts.find(m=>m.type==='host.finalized'));const closed=await snapshot(p);
      await p.evaluate(()=>window.__release());await p.waitForTimeout(500);const late=await snapshot(p);
      return {before,during,ack,closed,late,defect:ack.ok&&before.stable?.locator!=null&&closed.saved?.position?.locator==null,
        note:'Real Edge and actual isolated localStorage. Local-only protocol bridge delivers host.finalize, not original C# or native closure. A valid old chapter position is pending when the target document has committed but resources/layout have not.'};
    },true);
    await test('E03_SVG_HIGHLIGHT_DISAPPEARS',async p=>{
      await open(p,'svgpaint');
      const measure=()=>{const d=__lyraDebug.paginator.doc,t=d.querySelector('#paint-text'),m=d.querySelector('.lyra-search-mark');return{
        visible:d.body.innerText,raw:t.textContent,length:t.getComputedTextLength(),bbox:{width:t.getBBox().width,height:t.getBBox().height},
        mark:m?{namespace:m.namespaceURI,text:m.textContent,rects:m.getClientRects().length,width:m.getBoundingClientRect().width}:null};};
      const before=await p.evaluate(measure);await p.screenshot({path:path.join(out,'svg-before.png')});
      await search(p,'TARGET');await p.locator('.search-result').first().click();await p.waitForTimeout(250);
      const after=await p.evaluate(measure);await p.screenshot({path:path.join(out,'svg-after-highlight.png')});
      return {before,after,defect:before.visible.includes('TARGET')&&!after.visible.includes('TARGET'),
        note:'No fault or timing injection. Valid visible inline SVG text, real UI search click. Check browser text geometry/paint, not just the wrapped DOM textContent.'};
    });
    await test('E04_QUERY_LIMIT_MESSAGE',async p=>{
      await open(p,'search');await search(p,'a'.repeat(4097));
      const actual=await p.evaluate(()=>({length:document.getElementById('searchInput').value.length,info:document.getElementById('searchInfo').textContent,status:document.getElementById('statusText')?.textContent,buttons:document.querySelectorAll('.search-result').length}));
      return {actual,defect:!(/4096|过长|太长|长度|QUERY_TOO_LONG/.test(actual.info+' '+(actual.status||''))),note:'Actual 4097-character pasted query; verify new explicit input limit is visible to user.'};
    });
    await test('E05_CSS_VISIBILITY_DECLARATION_ORDER',async p=>{
      await open(p,'cssorder');
      const before=await p.evaluate(async()=>{const d=__lyraDebug.paginator.doc;return {plain:await __lyraDebug.state.book.getSectionPlainText(0),visible:d.body.innerText,
        targetVisibility:d.defaultView.getComputedStyle(d.querySelector('#visible')).visibility,hiddenVisibility:d.defaultView.getComputedStyle(d.querySelector('#hidden')).visibility};});
      await search(p,'TARGET');const target=await p.evaluate(()=>({count:document.querySelectorAll('.search-result').length,status:document.getElementById('searchInfo').textContent}));
      await search(p,'HIDDEN');const hidden=await p.evaluate(()=>({count:document.querySelectorAll('.search-result').length,status:document.getElementById('searchInfo').textContent}));
      await p.screenshot({path:path.join(out,'css-visibility-order.png')});
      return {before,target,hidden,defect:target.count!==1||hidden.count!==0,note:'No fault injection. Actual CSS duplicate declarations follow last valid declaration; model must match displayed content.'};
    });
    for(const method of ['finalize','pagehide']) await test('E06_ANIMATION_'+method.toUpperCase(),async p=>{
      await open(p,'normal');await p.evaluate(()=>{const d=__lyraDebug;d.paginator.setReduceMotion(false);const raf=window.requestAnimationFrame.bind(window),cancel=window.cancelAnimationFrame.bind(window);window.__rafCounter=1000000;window.__rafHeld=new Map();
        window.requestAnimationFrame=fn=>{const id=++window.__rafCounter;window.__rafHeld.set(id,fn);return id;};window.cancelAnimationFrame=id=>{if(!window.__rafHeld.delete(id))cancel(id);};
      });
      await p.locator('#pageNext').click();const during=await p.evaluate(()=>({target:__lyraDebug.paginator.pageIndex,scroll:__lyraDebug.paginator.scrollPos,raf:__lyraDebug.paginator._anim.raf,modified:__lyraDebug.state.modified}));
      if(!during.raf)throw new Error('Probe could not establish pending animation');
      if(method==='finalize'){await p.evaluate(()=>window.__deliverHost({type:'host.finalize',requestId:'animation-close',sessionId:__lyraDebug.state.client.sessionId}));await p.waitForFunction(()=>window.__hostPosts.some(m=>m.type==='host.finalized'));}
      else {await p.evaluate(()=>window.dispatchEvent(new Event('pagehide')));await p.waitForTimeout(200);}
      const after=await snapshot(p),ack=await p.evaluate(()=>window.__hostPosts.find(m=>m.type==='host.finalized')||null);
      await p.reload();await ready(p);const reopened=await snapshot(p);
      return {during,ack,after,reopened,defect:reopened.page!==during.target,note:'Original UI next, real Edge geometry/storage/reload. Explicit controllable rAF clock holds animation before first tick; finalize message is local-only bridge. pagehide is settle-order control, not native close.'};
    },true);
    await test('E07_FILE_BYTES_COMPLETE_AFTER_FINALIZE',async p=>{
      await open(p,'search');await p.evaluate(()=>{const orig=File.prototype.arrayBuffer;File.prototype.arrayBuffer=async function(){if(this.name==='LATE.epub'){window.__waiting=true;await new Promise(r=>window.__release=r);}return orig.call(this);};});
      await p.locator('#file').setInputFiles({name:'LATE.epub',mimeType:'application/epub+zip',buffer:fixtures.normal});await p.waitForFunction(()=>window.__waiting);
      await p.evaluate(()=>window.__deliverHost({type:'host.finalize',requestId:'file-after-close',sessionId:__lyraDebug.state.client.sessionId}));await p.waitForFunction(()=>window.__hostPosts.some(m=>m.type==='host.finalized'));
      const atAck=await snapshot(p),ack=await p.evaluate(()=>window.__hostPosts.find(m=>m.type==='host.finalized'));
      await p.evaluate(()=>window.__release());await p.waitForTimeout(500);const late=await snapshot(p),error=await p.locator('#errorText').innerText();
      return {ack,atAck,late,error,defect:ack.ok&&(atAck.ready!==late.ready||/正文载入失败/.test(error)),note:'File bytes held after genuine file input; original code, local bridge keeps page alive to inspect post-ack work. Does not claim native window continues after actual disposal.'};
    },true);
 }finally{fs.writeFileSync(path.join(out,'results.json'),JSON.stringify({evidenceKind:'Real Edge R5, original modules; explicit resource/File/RAF gates; local-only bridge and actual isolated localStorage',results},null,2));await browser.close();await new Promise(r=>server.close(r));}
})();
