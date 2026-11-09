/* R6 A11: real Edge ready-after image geometry. Gate is an explicit HTMLImageElement.complete override in the
 * reader iframe; no host fake, no business code change. Measures stages and checks no permanent blank/return-to-zero. */
"use strict";
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE);
const repo=path.resolve('D:/Temp/Lyra-Extension-Epub');
const out='F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/r6-evidence-supplement/a11';
fs.mkdirSync(out,{recursive:true});
const JSZip=require(path.join(repo,'lib/jszip.min.js'));
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/lzcxkAAAAABJRU5ErkJggg==','base64');
async function book(){const z=new JSZip();z.file('mimetype','application/epub+zip');z.file('META-INF/container.xml','<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OPS/book.opf"/></rootfiles></container>');
 const paras=Array.from({length:120},(_,i)=>`<p>A11 paragraph ${i}: ready-after image geometry and stable reading anchor. 这是用于迟到图片重排的正文。</p>`).join('');
 const body=`<h1>A11 READY AFTER IMAGE</h1><div id="latebox"><img id="lateimg" src="pic.png" width="400" height="240"/></div>${paras}`;
 z.file('OPS/c0.xhtml',`<html xmlns="http://www.w3.org/1999/xhtml"><head><title>A11</title></head><body>${body}</body></html>`);z.file('OPS/pic.png',png);
 z.file('OPS/book.opf','<package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>A11 ready after image</dc:title></metadata><manifest><item id="c0" href="c0.xhtml" media-type="application/xhtml+xml"/><item id="pic" href="pic.png" media-type="image/png"/></manifest><spine><itemref idref="c0"/></spine></package>');
 return z.generateAsync({type:'nodebuffer'});}
(async()=>{
 const buf=await book();
 const server=http.createServer((req,res)=>{const u=new URL(req.url,'http://local').pathname;if(u==='/fixtures/a11.epub'){res.writeHead(200,{'Content-Type':'application/epub+zip'});return res.end(buf);}const f=path.resolve(repo,u==='/'?'index.html':decodeURIComponent(u).replace(/^\/+/, ''));if(!f.startsWith(repo)||!fs.existsSync(f)||!fs.statSync(f).isFile()){res.writeHead(404);return res.end();}res.writeHead(200,{'Content-Type':({'.js':'application/javascript','.css':'text/css','.html':'text/html'})[path.extname(f)]||'application/octet-stream'});fs.createReadStream(f).pipe(res);});
 await new Promise(r=>server.listen(0,'127.0.0.1',r));const base='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({headless:true,executablePath:process.env.LYRA_BROWSER_PATH});const ctx=await browser.newContext({viewport:{width:1400,height:900}});
 await ctx.addInitScript(()=>{window.__LYRA_TEST_MODE__=true;window.__imgGate={ready:false,releasedAt:0,installed:0};
 const gate=()=>{try{const f=document.getElementById('chapterFrame');if(!f||!f.contentDocument)return;const imgs=f.contentDocument.images;for(const img of imgs){if(!img.__gated){img.__gated=true;Object.defineProperty(img,'complete',{configurable:true,get:()=>window.__imgGate.ready});window.__imgGate.installed++;}}}catch{}};
 setInterval(gate,2);window.addEventListener('load',()=>setInterval(gate,2));});
 const page=await ctx.newPage();page.setDefaultTimeout(30000);
 const t0=Date.now();await page.goto(base+'/index.html?book=fixtures/a11.epub&name=a11.epub');
 await page.waitForFunction(()=>__lyraDebug?.state.client?.mode==='local',null,{timeout:10000}).catch(()=>{});
 // install gate inside the chapter iframe before first image load: override complete=false on all imgs
 await page.waitForFunction(()=>__lyraDebug?.paginator?.doc?.images?.length>0,null,{timeout:10000});
 await page.evaluate(()=>{const d=__lyraDebug.paginator.doc;for(const img of d.images){Object.defineProperty(img,'complete',{configurable:true,get:()=>window.__imgGate.ready});}});
 await page.waitForFunction(()=>__lyraDebug.state.ready===true,null,{timeout:10000});
 const readyAt=Date.now();
 const before=await page.evaluate(()=>({ready:__lyraDebug.state.ready,busy:__lyraDebug.state.busy,page:__lyraDebug.paginator.pageIndex,count:__lyraDebug.paginator.pageCount,nodes:__lyraDebug.paginator.content.getElementsByTagName('*').length,imgComplete:__lyraDebug.paginator.doc.images[0]?.complete,imgW:__lyraDebug.paginator.doc.images[0]?.naturalWidth,stable:__lyraDebug.state.lastStablePosition?.locator?.textOffset,gate:window.__imgGate}));
 // release after ready: geometry changes (image becomes real 480x480), dispatch load
 await page.evaluate(()=>{const img=__lyraDebug.paginator.doc.images[0];img.style.width='480px';img.style.height='480px';window.__imgGate.ready=true;window.__imgGate.releasedAt=performance.now();try{img.dispatchEvent(new Event('load'));}catch{}});
 await page.waitForTimeout(600);
 const after=await page.evaluate(()=>({ready:__lyraDebug.state.ready,busy:__lyraDebug.state.busy,page:__lyraDebug.paginator.pageIndex,count:__lyraDebug.paginator.pageCount,nodes:__lyraDebug.paginator.content.getElementsByTagName('*').length,imgComplete:__lyraDebug.paginator.doc.images[0]?.complete,imgW:__lyraDebug.paginator.doc.images[0]?.naturalWidth,stable:__lyraDebug.state.lastStablePosition?.locator?.textOffset,scroll:__lyraDebug.paginator.scrollPos}));
 await page.waitForTimeout(1000);const rest=await page.evaluate(()=>({ready:__lyraDebug.state.ready,busy:__lyraDebug.state.busy,page:__lyraDebug.paginator.pageIndex,count:__lyraDebug.paginator.pageCount,nodes:__lyraDebug.paginator.content.getElementsByTagName('*').length,imgComplete:__lyraDebug.paginator.doc.images[0]?.complete,imgW:__lyraDebug.paginator.doc.images[0]?.naturalWidth}));
 const result={evidenceKind:'Real Edge; explicit image.complete gate in reader iframe; no fake host; no business code change',t0,readyAt,totalMs:Date.now()-t0,before,after,rest,defect:!after.ready||!after.imgComplete||after.nodes<10||after.count<1||rest.ready!==true};
 fs.writeFileSync(path.join(out,'a11-results.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result));
 await browser.close();await new Promise(r=>server.close(r));
})().catch(e=>{console.error(e);process.exitCode=1});
