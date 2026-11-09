/* Read-only diagnostic for EPUB 0.5.26. Does not edit the extension or user library.
 * Environment: PLAYWRIGHT_MODULE, EPUB_SOURCE, LYRA_BROWSER_PATH.
 * Generates synthetic EPUBs, captures current behavior; observations are not acceptance passes. */
const fs = require('node:fs'), path = require('node:path'), http = require('node:http');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root = process.env.EPUB_SOURCE || 'D:\\Temp\\Lyra-Extension-Epub';
const JSZip = require(path.join(root, 'lib/jszip.min.js'));
const output = path.join(__dirname, 'baseline-artifacts');
fs.mkdirSync(output, { recursive: true });
function content(chapter, extras = '') {
  return `<!DOCTYPE html><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter ${chapter}</title></head><body><h1 id="start">CHAPTER-${chapter}</h1>${extras}` +
    Array.from({length:100},(_,i)=>`<p id="p${i}" data-paragraph="${i}">段落 ${String(i).padStart(3,'0')}：这是用于验证阅读位置的测试文本。我们希望字号变化、窗口变化与下一次打开时，仍然能找到刚才读到的句子。Chapter ${chapter}, paragraph ${i}.</p>${i===50 ? '<h2 id="section-two">SECOND-SECTION</h2>' : ''}`).join('') + '</body></html>';
}
async function epub(kind) {
  const z = new JSZip(); z.file('mimetype','application/epub+zip');
  z.file('META-INF/container.xml','<?xml version="1.0"?><container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="OPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles></container>');
  z.file('OPS/package.opf',`<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Baseline ${kind}</dc:title><dc:identifier id="id">lyra-test-${kind}</dc:identifier><dc:language>zh-CN</dc:language></metadata><manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>${[1,2,3].map(i=>`<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join('')}</manifest><spine>${[1,2,3].map(i=>`<itemref idref="c${i}"/>`).join('')}</spine></package>`);
  const links = kind==='toc' ? '<li><a href="c1.xhtml#start">第一节</a></li><li><a href="c1.xhtml#section-two">第二节</a></li><li><a href="c3.xhtml">第三章</a></li>' : [1,2,3].map(i=>`<li><a href="c${i}.xhtml">第${i}章</a></li>`).join('');
  z.file('OPS/nav.xhtml',`<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body><nav epub:type="toc"><ol>${links}</ol></nav>${kind==='toc'?'<nav epub:type="landmarks"><ol><li><a href="c3.xhtml">辅助入口</a></li></ol></nav>':''}</body></html>`);
  for(let i=1;i<=3;i++) z.file(`OPS/c${i}.xhtml`,content(i, i===1?'<p><a id="note-link" href="#section-two">跳到章内第二节</a></p>':''));
  if(kind==='event') z.file('OPS/c1.xhtml',content(1,'<img src="missing-image.png" onerror="window.__fixtureEventRan = true" alt="benign event fixture"/>'));
  return z.generateAsync({type:'nodebuffer',compression:'DEFLATE'});
}
(async()=>{
  const fixtures = {};
  for(const kind of ['normal','toc','event']) { fixtures[kind]=await epub(kind); fs.writeFileSync(path.join(output,kind+'.epub'),fixtures[kind]); }
  const handler = (req,res)=>{
    const rel = new URL(req.url,'http://localhost').pathname;
    if(rel.startsWith('/fixtures/')) { const buf=fixtures[path.basename(rel,'.epub')]; res.writeHead(buf?200:404,{'Content-Type':'application/epub+zip'});res.end(buf);return; }
    const file = rel==='/index.html'?path.join(root,'index.html'):rel==='/lib/jszip.min.js'?path.join(root,'lib/jszip.min.js'):null;
    if(!file){res.writeHead(404);res.end();return;}
    res.writeHead(200,{'Content-Type':rel.endsWith('.js')?'application/javascript':'text/html; charset=utf-8'});fs.createReadStream(file).pipe(res);
  };
  const servers = [http.createServer(handler), http.createServer(handler)];
  for(const server of servers) await new Promise(r=>server.listen(0,'127.0.0.1',r));
  const bases=servers.map(s=>'http://127.0.0.1:'+s.address().port);
  const browser = await chromium.launch({headless:true,executablePath:process.env.LYRA_BROWSER_PATH});
  const context = await browser.newContext({viewport:{width:1400,height:900},deviceScaleFactor:1});
  await context.addInitScript(()=>{
    window.__rafCalls=0; const original=requestAnimationFrame;
    window.requestAnimationFrame=callback=>original.call(window, time=>{window.__rafCalls++;callback(time);});
    window.__hostMessages=[]; window.chrome=window.chrome||{};
    window.chrome.webview={postMessage:m=>window.__hostMessages.push(m),addEventListener:()=>{}};
  });
  const page=await context.newPage(), observations=[], errors=[];
  page.on('pageerror',e=>errors.push(e.message)); page.on('dialog',d=>d.dismiss());
  async function opened(kind='normal', base=bases[0]) {
    await page.goto(base+'/index.html?book=fixtures/'+kind+'.epub');
    await page.waitForFunction(()=>document.querySelector('#position').textContent.includes('页'));
    await page.waitForTimeout(400);
  }
  async function observe(id,fn) {
    try { const result=await fn(); observations.push({id,...result}); console.log(JSON.stringify({id,...result})); }
    catch(e){ observations.push({id,probeError:e.message}); console.log('PROBE ERROR',id,e.message); }
  }
  function visibleParagraph() {
    const clip=document.querySelector('#pageClip').getBoundingClientRect();
    for(const p of document.querySelectorAll('#view [data-paragraph]')) for(const r of p.getClientRects()) {
      if(r.right>clip.left+2 && r.left<clip.right-2 && r.bottom>clip.top+2 && r.top<clip.bottom-2) return p.dataset.paragraph;
    }
    return null;
  }
  try {
    await observe('E01_TOC_IS_NOT_SPINE',async()=>{
      await opened('toc');
      const toc=await page.locator('.chapter').allTextContents();
      await page.locator('.chapter').nth(1).click();await page.waitForTimeout(350);
      return {toc,spineDocuments:3,chapterCount:toc.length,secondEntryOpens:await page.locator('#view h1').innerText(),position:await page.locator('#position').innerText(),scrollLeft:await page.locator('#view').evaluate(e=>e.scrollLeft)};
    });
    await observe('E02_FRAGMENT_ONLY_LINK',async()=>{
      await opened();const before=await page.locator('#position').innerText();
      await page.locator('#note-link').click();await page.waitForTimeout(350);
      return {before,after:await page.locator('#position').innerText(),scrollLeft:await page.locator('#view').evaluate(e=>e.scrollLeft)};
    });
    await observe('E03_PAGE_PROGRESS_NOT_SAVED',async()=>{
      await opened(); await page.locator('#nextBtn').click();await page.waitForTimeout(400);
      for(let i=0;i<4;i++) await page.keyboard.press('ArrowRight'); await page.waitForTimeout(900);
      const before=await page.locator('#position').innerText(), stored=await page.evaluate(()=>({...localStorage}));
      await page.reload();await page.waitForTimeout(850);
      return {before,after:await page.locator('#position').innerText(),stored};
    });
    await observe('E04_RANDOM_ORIGIN_STORAGE',async()=>{
      await opened();await page.locator('#nextBtn').click();await page.waitForTimeout(350);
      await page.evaluate(()=>document.querySelector('[data-bg="#F5F0E6"]').click());
      const before=await page.locator('#position').innerText();
      await opened('normal',bases[1]);
      return {before,after:await page.locator('#position').innerText(),theme:await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--reader-bg').trim()),note:'Two origins in one browser context simulate host origin isolation; not a live WebView2 restart.'};
    });
    await observe('E05_FONT_REFLOW_LOSES_LOCATION',async()=>{
      await opened();for(let i=0;i<4;i++)await page.keyboard.press('ArrowRight');await page.waitForTimeout(700);
      const before=await page.evaluate(visibleParagraph),beforePage=await page.locator('#position').innerText();
      await page.locator('#fontUp').click();await page.locator('#fontUp').click();await page.waitForTimeout(350);
      return {beforeParagraph:before,afterParagraph:await page.evaluate(visibleParagraph),beforePage,afterPage:await page.locator('#position').innerText()};
    });
    await observe('E06_ANIMATION_IDLE_WORK',async()=>{
      await opened(); await page.keyboard.press('ArrowRight'); await page.waitForTimeout(1100);
      const first=await page.evaluate(()=>({calls:window.__rafCalls,left:document.querySelector('#view').scrollLeft}));
      await page.waitForTimeout(1000);
      const second=await page.evaluate(()=>({calls:window.__rafCalls,left:document.querySelector('#view').scrollLeft}));
      return {first,second,callbacksWhileStationary:second.calls-first.calls};
    });
    await observe('E07_SLOW_CHAPTER_RACE',async()=>{
      await opened();
      await page.evaluate(()=>{
        const original=JSZip.prototype.file;
        JSZip.prototype.file=function(...args){const f=original.apply(this,args);if(f&&f.name==='OPS/c2.xhtml'&&!f.__delayed){const read=f.async;f.async=async function(...a){const value=await read.apply(this,a);await new Promise(r=>setTimeout(r,850));return value;};f.__delayed=true;}return f;};
      });
      await page.locator('.chapter').nth(1).click();await page.waitForTimeout(200);await page.locator('.chapter').nth(2).click();await page.waitForTimeout(1250);
      return {activeChapter:await page.locator('.chapter.active').innerText(),bodyHeading:await page.locator('#view h1').innerText(),position:await page.locator('#position').innerText()};
    });
    await observe('E08_SELECTION_TURNS_PAGE',async()=>{
      await opened(); await page.evaluate(()=>{document.querySelector('#selectToggle').click();});
      const before=await page.locator('#position').innerText();
      const box=await page.locator('#view p[data-paragraph="0"]').boundingBox();
      await page.mouse.move(box.x+55,box.y+18);await page.mouse.down();await page.mouse.move(box.x+230,box.y+18,{steps:12});await page.mouse.up();await page.waitForTimeout(500);
      return {before,after:await page.locator('#position').innerText(),selection:await page.evaluate(()=>getSelection().toString())};
    });
    await observe('E09_NARROW_LAYOUT_CLIPS',async()=>{
      await page.setViewportSize({width:760,height:800}); await opened();
      const metrics=await page.evaluate(()=>{const a={};for(const id of ['main','pageFrame','pageClip','view']){const r=document.getElementById(id).getBoundingClientRect();a[id]={x:r.x,right:r.right,width:r.width};}return a;});
      await page.screenshot({path:path.join(output,'narrow-baseline.png')});
      await page.setViewportSize({width:1400,height:900});return metrics;
    });
    await observe('E10_ACTIVE_CONTENT_NOT_REMOVED',async()=>{
      await opened('event');return {benignImageEventExecuted:await page.evaluate(()=>!!window.__fixtureEventRan),onerrorAttributeRemains:await page.locator('#view img').getAttribute('onerror')};
    });
    await observe('E11_HOST_RETRY_AFTER_BAD_BOOK',async()=>{
      await page.goto(bases[0]+'/index.html');
      return await page.evaluate(async()=>{
        let firstError='';try{await window.lyraParseBookData(new Uint8Array([1,2,3]).buffer,'broken.epub',3);}catch(e){firstError=e.message;}
        const data=await (await fetch('fixtures/normal.epub')).arrayBuffer();await window.lyraParseBookData(data,'normal.epub',data.byteLength);
        return {firstError,title:document.querySelector('#bookTitle').textContent,chapters:document.querySelectorAll('.chapter').length};
      });
    });
    fs.writeFileSync(path.join(output,'observations.json'),JSON.stringify({baseline:'c12fe26275cdec67922cccb64d9b5f2b4c269cd2',observations,errors},null,2));
  } finally {await browser.close();for(const server of servers)await new Promise(r=>server.close(r));}
})().catch(error=>{console.error(error);process.exitCode=1;});
