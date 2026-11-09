/* R5 supplement A23: real Edge + generated 200-image-chapter book, 10 book switches.
 * Measures browser DOM/iframe nodes, blob URL/task ledgers, and renderer JS heap via CDP.
 * This is browser renderer evidence, not native WinUI process memory.
 */
"use strict";
const fs=require('node:fs'), path=require('node:path'), http=require('node:http');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const repo=path.resolve('D:/Temp/Lyra-Extension-Epub');
const outDir='F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/r5-evidence-supplement/a23';
fs.mkdirSync(outDir,{recursive:true});
const JSZip=require(path.join(repo,'lib/jszip.min.js'));
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lzcxkAAAAABJRU5ErkJggg==','base64');
function book(title,n,image=false){const z=new JSZip();z.file('mimetype','application/epub+zip');z.file('META-INF/container.xml','<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/book.opf"/></rootfiles></container>');
 const items=[],refs=[];for(let i=0;i<n;i++){items.push(`<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`);refs.push(`<itemref idref="c${i}"/>`);z.file(`OPS/c${i}.xhtml`,`<html xmlns="http://www.w3.org/1999/xhtml"><head><title>c${i}</title></head><body>${image?`<div><img src="pic.png" width="480" height="480"/></div>`:`<p>${title} chapter ${i} visible reading text R5 A23.</p>`}</body></html>`)}
 if(image){items.push('<item id="pic" href="pic.png" media-type="image/png"/>');z.file('OPS/pic.png',png)}
 z.file('OPS/book.opf',`<package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>${title}</dc:title></metadata><manifest>${items.join('')}</manifest><spine>${refs.join('')}</spine></package>`);
 return z.generateAsync({type:'nodebuffer'});}
(async()=>{
 const big=await book('A23-IMAGES-200',200,true); const books=[]; for(let i=0;i<10;i++) books.push(await book('A23-SWITCH-'+i,3,false));
 const fixtures={big,books};
 const server=http.createServer((req,res)=>{const p=new URL(req.url,'http://local').pathname;if(p.startsWith('/fixtures/')){const n=path.basename(p,'.epub');let b=null;if(n==='big')b=big;else{const m=/^b(\d+)$/.exec(n);if(m)b=books[Number(m[1])]}res.writeHead(b?200:404,{'Content-Type':'application/epub+zip'});return res.end(b);}const f=path.resolve(repo,p==='/'?'index.html':decodeURIComponent(p).replace(/^\/+/, ''));if(!f.startsWith(repo)||!fs.existsSync(f)||!fs.statSync(f).isFile()){res.writeHead(404);res.end();return;}res.writeHead(200,{'Content-Type':({'.js':'application/javascript','.css':'text/css','.html':'text/html'})[path.extname(f)]||'application/octet-stream'});fs.createReadStream(f).pipe(res);});
 await new Promise(r=>server.listen(0,'127.0.0.1',r)); const base='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.env.LYRA_BROWSER_PATH});
 const context=await browser.newContext({viewport:{width:1400,height:900}}); await context.addInitScript(()=>window.__LYRA_TEST_MODE__=true);
 const page=await context.newPage(); const session=await context.newCDPSession(page); await session.send('Performance.enable').catch(()=>{});
 const snap=async(label)=>{const m=await session.send('Performance.getMetrics').catch(()=>({metrics:[]}));const metrics=Object.fromEntries((m.metrics||[]).map(x=>[x.name,x.value]));return await page.evaluate(({label,metrics})=>{const s=__lyraDebug.state,p=__lyraDebug.paginator,b=s.book;return{label,spine:s.spineIndex,ready:s.ready,busy:s.busy,book:s.bookId,uiNodes:document.getElementsByTagName('*').length,iframeNodes:p.content?p.content.getElementsByTagName('*').length:0,iframeCount:document.querySelectorAll('iframe').length,resourceCount:b?b._resources.size:0,taskSets:b?b._taskResources.size:0,pending:b?b._resourcePending.size:0,blobUrls:b?[...b._resources.values()].map(x=>x.url):[],metrics:{JSHeapUsedSize:metrics.JSHeapUsedSize||0,LayoutCount:metrics.LayoutCount||0,TaskDuration:metrics.TaskDuration||0}}},{label,metrics});};
 const results={evidenceKind:'Real Edge browser renderer + generated EPUBs; CDP Performance JS heap; not native WinUI process memory',book200:[],switches:[],rest:[]};
 try{
  await page.goto(base+'/index.html?book=fixtures/big.epub&name=big.epub'); await page.waitForFunction(()=>window.__lyraDebug?.state.ready===true,null,{timeout:20000}); await page.waitForTimeout(300);
  results.book200.push(await snap('start'));
  const started=Date.now();
  for(let i=0;i<200;i++){
   await page.evaluate(()=>{const b=__lyraDebug.state.book; if(!b)return; if(__lyraDebug.state.spineIndex<b.spine.length-1) document.getElementById('chapterNext').click(); else document.getElementById('pageNext').click();});
   await page.waitForFunction(()=>window.__lyraDebug?.state.ready===true && window.__lyraDebug?.state.busy===false,null,{timeout:15000}).catch(()=>{});
   if(i===49||i===99||i===149||i===199) results.book200.push(await snap('after-'+(i+1)));
  }
  results.book200ElapsedMs=Date.now()-started;
  await page.waitForTimeout(1000); results.rest.push(await snap('static-1s'));
  for(let i=0;i<10;i++){
   await page.goto(base+'/index.html?book=fixtures/b'+i+'.epub&name=b'+i+'.epub'); await page.waitForFunction(()=>window.__lyraDebug?.state.ready===true,null,{timeout:20000}); await page.waitForTimeout(200);
   results.switches.push(await snap('book-'+i));
  }
  await page.waitForTimeout(1000); results.rest.push(await snap('after-switches-static'));
 }finally{fs.writeFileSync(path.join(outDir,'results.json'),JSON.stringify(results,null,2));console.log('SUMMARY',JSON.stringify({book200:results.book200.map(x=>({label:x.label,spine:x.spine,nodes:x.iframeNodes,res:x.resourceCount,tasks:x.taskSets,heap:x.metrics.JSHeapUsedSize})),switches:results.switches.length,rest:results.rest.map(x=>({label:x.label,res:x.resourceCount,tasks:x.taskSets,heap:x.metrics.JSHeapUsedSize})),elapsed:results.book200ElapsedMs}));await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1});
