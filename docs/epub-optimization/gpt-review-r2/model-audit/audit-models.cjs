/* Independent R2 module audit: executes frozen original modules, controlled dependencies only.
 * No browser/desktop/network, no installation, no product writes. DOM evidence is not layout evidence.
 * Run: node audit-models.cjs
 */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { performance } = require('node:perf_hooks');
const { JSDOM } = require('C:/Users/Administrator/deepseek-harness/node_modules/.pnpm/jsdom@29.1.1_@noble+hashes@2.3.0/node_modules/jsdom');
const candidate = path.resolve(__dirname, '../candidate');
const JSZip = require(path.join(candidate, 'lib/jszip.min.js'));
const results = [];
const deferred = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };
const wait = ms => new Promise(r => setTimeout(r, ms));
function record(id, verdict, title, detail) { const r = { id, verdict, title, detail }; results.push(r); console.log(JSON.stringify(r)); }
function environment({reader = false} = {}) {
  const html = reader ? fs.readFileSync(path.join(candidate, 'index.html'), 'utf8') : '<!doctype html><html><body></body></html>';
  const dom = new JSDOM(html, { url: 'https://audit.invalid/index.html', runScripts: 'outside-only', pretendToBeVisual: true });
  const w = dom.window, created = [], revoked = [];
  w.JSZip = JSZip;
  w.URL.createObjectURL = blob => { const url = 'blob:audit-' + (created.length + 1); created.push({ url, size: blob.size, type: blob.type }); return url; };
  w.URL.revokeObjectURL = url => revoked.push(url);
  for (const f of ['epub-book.js', 'search.js', 'paginator.js']) w.eval(fs.readFileSync(path.join(candidate, f), 'utf8'));
  if (reader) {
    w.__LYRA_TEST_MODE__ = true;
    // Freeze initialization before openFromUrl; real reader UI bindings and paginator constructor run.
    w.LyraReaderState = { ReaderStateClient: class { constructor(){ this.sessionId='audit'; this.isPersistent=false; } appReady(){} hello(){ return new Promise(()=>{}); } } };
    w.eval(fs.readFileSync(path.join(candidate, 'reader.js'), 'utf8'));
  }
  return { dom, w, created, revoked, Book:w.LyraEpubBook.EpubBook, Search:w.LyraSearch.SearchEngine, Paginator:w.LyraPaginator.Paginator };
}
function fakeBook(texts) { return { bookId:'audit-book', spine:texts.map((_, index)=>({index})), getSectionPlainText:async i=>texts[i], sectionTitle:async i=>'C'+i }; }
async function packedBook({ chapters=['<p>BODY</p>'], encryption=null, extras={}, manifestExtra='', nav=null } = {}) {
  const z = new JSZip();
  z.file('META-INF/container.xml', '<container><rootfiles><rootfile full-path="OPS/package.opf"/></rootfiles></container>');
  z.file('OPS/package.opf', `<package><metadata><title>Audit</title></metadata><manifest>${chapters.map((_,i)=>`<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join('')}${manifestExtra}${nav?'<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>':''}</manifest><spine>${chapters.map((_,i)=>`<itemref idref="c${i}"/>`).join('')}</spine></package>`);
  chapters.forEach((body,i)=>z.file(`OPS/c${i}.xhtml`, `<html><body>${body}</body></html>`));
  if(encryption) z.file('META-INF/encryption.xml', encryption);
  if(nav) z.file('OPS/nav.xhtml', `<html><body><nav epub:type="toc"><ol>${nav}</ol></nav></body></html>`);
  for(const [name, bytes] of Object.entries(extras)) z.file(name, bytes);
  return z.generateAsync({type:'nodebuffer'});
}
async function main() {
  const e = environment(); const {w,Book,Search,Paginator,created,revoked} = e;
  let b = new Book({},new ArrayBuffer(0),'audit.epub'); let gate=deferred();
  b.readBytes=()=>gate.promise; const pending=b._resourceUrl('image.png'); b.destroy(); gate.resolve(new Uint8Array([1])); const late=await pending;
  record('M01', b._resources.size===0 && created.length===revoked.length?'PASS':'FAIL','Read pending at destroy cannot register late resource',{url:late,resources:b._resources.size,created:[...created],revoked:[...revoked]});
  b=new Book({},new ArrayBuffer(0),'audit.epub');let reads=0;b.readBytes=async()=>{reads++;return new Uint8Array([1]);};
  const urls=await Promise.all([b._resourceUrl('same.png'),b._resourceUrl('same.png'),b._resourceUrl('same.png')]);b.destroy();
  record('M02',new Set(urls).size===1 && reads===1 && created.length===revoked.length?'PASS':'FAIL','Concurrent same-path loads share URL',{reads,urls,created:[...created],revoked:[...revoked]});
  let s=new Search(fakeBook(['needle '.repeat(1000),'needle']));let r=await s.search('needle');
  record('M03',r.total===1000&&!r.truncated?'FAIL':'PASS','1001 matches across chapter boundary',{knownMatches:1001,total:r.total,resultCount:r.results.length,truncated:r.truncated});
  const readSections=[];const big=fakeBook(['a'.repeat(8*1024*1024+1),'UNIQUE_LATER']);big.getSectionPlainText=async i=>{readSections.push(i);return i===0?'a'.repeat(8*1024*1024+1):'UNIQUE_LATER';};s=new Search(big);r=await s.search('UNIQUE_LATER');
  record('M04',r.total===0?'FAIL':'PASS','Oversize first chapter suppresses all later chapters',{readSections,total:r.total,truncated:r.truncated,chars:r.chars});
  s=new Search(fakeBook(['needle']));await s.ensureIndex();const hot=s.search('needle');s.cancel();r=await hot;
  record('M05',r.total>0?'FAIL':'PASS','Cancel between hot query start and continuation',{total:r.total});
  gate=deferred();const started=deferred();s=new Search({spine:[{}],getSectionPlainText:async()=>{started.resolve();await gate.promise;return 'old new';},sectionTitle:async()=>''});
  const old=s.search('old');await started.promise;s.cancel();const fresh=s.search('new');gate.resolve();const settled=await Promise.allSettled([old,fresh]);
  record('M06',settled[1].status==='rejected'?'FAIL':'PASS','Immediate restart joins cancelled index promise',{statuses:settled.map(x=>x.status),errors:settled.map(x=>x.reason?.message||null)});
  s=new Search(fakeBook(['x '.repeat(4*1024*1024)]));await s.ensureIndex();let timerRan=false;const timer=setTimeout(()=>{timerRan=true;s.cancel();},0);const t0=performance.now();r=await s.search('x');const elapsed=performance.now()-t0;
  record('M07',!timerRan?'FAIL':'PASS','One hot chapter scan has no event-loop cancellation point',{elapsedMs:Math.round(elapsed),total:r.total,results:r.results.length,cancellationTimerRanAtReturn:timerRan});clearTimeout(timer);
  b=new Book({},new ArrayBuffer(0),'paths.epub');const refs=['pic%20a.png','pic%2520a.png','%E4%B8%AD%E6%96%87.png','hash%23percent%25.png','../%2e%2e/out.png','%2e%2e/%2e%2e/out.png','bad%2Fname.png'];const paths=refs.map(href=>({href,...b.resolveReference('OPS/c.xhtml',href)}));
  record('M08',paths[4].kind==='invalid'&&paths[5].kind==='invalid'&&paths[0].path==='OPS/pic a.png'&&paths[1].path==='OPS/pic%20a.png'?'PASS':'FAIL','Single decode, encoded dot rejection, Chinese/hash/percent paths',{paths});
  b=await Book.open(await packedBook({chapters:['<img src="pic%20a.png"><img src="pic%2520a.png"><img src="view.svg#two">'],extras:{'OPS/pic a.png':new Uint8Array([1,2,3]),'OPS/pic%20a.png':new Uint8Array([9,8,7]),'OPS/view.svg':'<svg xmlns="http://www.w3.org/2000/svg"/>'}}),'paths.epub');
  const aBytes=Array.from(await b.readBytes(b.resolveReference('OPS/c0.xhtml','pic%20a.png').path));const pBytes=Array.from(await b.readBytes(b.resolveReference('OPS/c0.xhtml','pic%2520a.png').path));const prep=await b.prepareSection(0);
  record('M09',JSON.stringify(aBytes)==='[1,2,3]'&&JSON.stringify(pBytes)==='[9,8,7]'&&prep.html.includes('#two')?'PASS':'FAIL','ZIP exact entries remain distinct; SVG fragment retained',{aBytes,pBytes,html:prep.html});b.destroy();
  // Real prepareSection calls, with only byte reads for old pictures delayed.
  b=await Book.open(await packedBook({chapters:['<img src="slow-a.png">','<img src="slow-b.png">','<img src="current.png">'],extras:{'OPS/slow-a.png':new Uint8Array([1]),'OPS/slow-b.png':new Uint8Array([2]),'OPS/current.png':new Uint8Array([3])}}),'ownership.epub');
  const originalRead=b.readBytes.bind(b);gate=deferred();const slowStarted=deferred();let startedCount=0;b.readBytes=async p=>{if(p.includes('slow-')){if(++startedCount===2)slowStarted.resolve();await gate.promise;}return originalRead(p);};
  const staleA=b.prepareSection(0),staleB=b.prepareSection(1);await slowStarted.promise;const current=await b.prepareSection(2);b.releaseResourcesExcept(current.resources);const atCommit=[...b._resources.keys()];gate.resolve();await Promise.all([staleA,staleB]);const afterStale=[...b._resources.keys()];
  record('M10',afterStale.length>current.resources.length?'FAIL':'PASS','Stale same-book sections recreate unused URLs after current commit',{currentPaths:current.resources,resourcesAtCommit:atCommit,resourcesAfterStaleTasks:afterStale,pending:b._resourcePending.size});b.destroy();
  const body='<p>甲<span>乙</span>丙 😀 甲<span>乙</span>丙</p><p>Hello  \n <em>world</em></p><p>cross</p><p>block</p><p>A<span hidden>HIDDEN_SECRET</span>B</p><aside hidden epub:type="footnote">FOOTNOTE_SECRET</aside><details><summary>Summary</summary>CLOSED_SECRET</details><svg xmlns="http://www.w3.org/2000/svg"><defs><text id="unused">SVG_DEF_SECRET</text></defs><desc>SVG_DESC_SECRET</desc><text>SVG_VISIBLE</text></svg>';
  b=await Book.open(await packedBook({chapters:[body]}),'text.epub');const prepared=await b.prepareSection(0);const root=w.document.createElement('div');root.innerHTML=prepared.html;const indexed=await b.getSectionPlainText(0);const model=w.LyraEpubBook.buildTextModel(root);const p=new Paginator({frame:w.document.createElement('iframe')});p.doc=w.document;p.content=root;s=new Search(b);
  const checks=[];for(const q of ['甲乙丙','😀','Hello world','cross block']){const res=await s.search(q);checks.push({q,results:res.results.map(x=>({offset:x.offset,length:x.length,rangeText:p.rangeAtTextOffset(x.offset,x.length).toString()}))});}
  record('M11',checks[0].results.length===2&&checks[0].results.every(x=>x.rangeText==='甲乙丙')&&checks[1].results[0].length===2&&indexed===model.text?'PASS':'FAIL','Current offset mapping preserves duplicates, inline tags, UTF-16 and whitespace',{textModelsEqual:indexed===model.text,checks});
  const semantic=[];for(const q of ['HIDDEN_SECRET','FOOTNOTE_SECRET','CLOSED_SECRET','SVG_DEF_SECRET','SVG_DESC_SECRET','AB']){const res=await s.search(q);semantic.push({q,total:res.total,rangeText:res.results[0]?p.rangeAtTextOffset(res.results[0].offset,res.results[0].length).toString():null});}
  record('M12',semantic.slice(0,5).some(x=>x.total>0)||semantic[5].total===0?'FAIL':'PASS','Invisible text remains searchable and breaks visible adjacency',{queries:semantic,hiddenStillPresent:root.querySelector('[hidden]')!==null,preparedHtml:prepared.html});p.destroy();b.destroy();
  const links='<a id="js" href="javascript:window.__benign=1">JS</a><a id="data" href="data:text/plain,x">DATA</a><a id="file" href="file:///audit">FILE</a><a id="https" href="https://example.invalid/ok">HTTPS</a><a id="forged" href="https://example.invalid/real" data-lyra-external="javascript:x">FORGED</a><div data-lyra-external="javascript:x">D</div><svg xmlns="http://www.w3.org/2000/svg"><a id="svg" xlink:href="javascript:x" xmlns:xlink="http://www.w3.org/1999/xlink"><text>X</text></a><a id="svgok" xlink:href="https://example.invalid/svg" xmlns:xlink="http://www.w3.org/1999/xlink"><text>OK</text></a></svg><script>x</script><img onerror="x">';
  b=await Book.open(await packedBook({chapters:[links]}),'links.epub');const clean=await b.prepareSection(0);root.innerHTML=clean.html;const linkInfo=Array.from(root.querySelectorAll('a')).map(a=>({id:a.id,href:a.getAttribute('href'),xlink:a.getAttribute('xlink:href'),external:a.getAttribute('data-lyra-external')}));
  record('M13',linkInfo.filter(x=>['js','data','file','svg'].includes(x.id)).every(x=>!x.href&&!x.xlink&&!x.external)&&!root.querySelector('div[data-lyra-external]')&&!root.querySelector('script,[onerror]')?'PASS':'FAIL','Parser sanitizer rejects dangerous HTML and SVG link protocols',{links:linkInfo,html:clean.html});b.destroy();
  const enc=(algorithm,uri)=>`<encryption><EncryptedData><EncryptionMethod Algorithm="${algorithm}"/><CipherData><CipherReference URI="${uri}"/></CipherData></EncryptedData></encryption>`;
  const fontCases=[['valid-idpf-font','http://www.idpf.org/2008/embedding','OPS/font.otf',true],['aes-body','http://www.w3.org/2001/04/xmlenc#aes256-cbc','OPS/c0.xhtml',false],['lookalike-idpf-body','https://invalid.example/idpf/aes256','OPS/c0.xhtml',false],['lookalike-font-body','https://invalid.example/font/custom','OPS/c0.xhtml',false],['idpf-applied-to-body','http://www.idpf.org/2008/embedding','OPS/c0.xhtml',false],['standard-adobe-font','http://ns.adobe.com/pdf/enc#RC','OPS/font.otf',true]];
  const encResults=[];for(const [name,algorithm,uri,expectedOpen] of fontCases){let opened=false,error=null;try{const book=await Book.open(await packedBook({encryption:enc(algorithm,uri),extras:{'OPS/font.otf':new Uint8Array([0,1,2,3])},manifestExtra:'<item id="font" href="font.otf" media-type="font/otf"/>'}),name+'.epub');opened=true;book.destroy();}catch(err){error={code:err.code,message:err.message};}encResults.push({name,algorithm,uri,expectedOpen,opened,error});}
  record('M14',encResults.some(x=>x.expectedOpen!==x.opened)?'FAIL':'PASS','Encryption classification checks substring, not algorithm and encrypted resource',{cases:encResults,boundary:'Malformed/unsupported declarations exercise classification; no decryption or DRM bypass asserted. Adobe support scope should be specified.'});
  b=await Book.open(await packedBook({chapters:['<p>A</p>','<p>B</p>'],nav:'<li><span>第一部</span><ol><li><a href="c0.xhtml">第一章</a></li></ol></li><li><a href="c1.xhtml">第二章</a></li>'}),'nav.epub');
  record('M15',b.tocFlat[0].group&&b.tocFlat[1].depth===1?'PASS':'FAIL','Unlinked EPUB3 navigation group retains hierarchy',{toc:b.tocFlat.map(x=>({title:x.title,path:x.path,depth:x.depth,group:!!x.group}))});b.destroy();
  e.dom.window.close();
  // Reader probes use original complete reader.js and real event registrations, no source instrumentation.
  let ui=environment({reader:true});let book=fakeBook(['needle']);let uiReads=0;book.getSectionPlainText=async()=>{uiReads++;return 'needle';};ui.w.__lyraDebug.state.book=book;ui.w.__lyraDebug.state.bookId=book.bookId;
  ui.w.document.getElementById('findBtn').click();const input=ui.w.document.getElementById('searchInput');input.value='needle';input.dispatchEvent(new ui.w.Event('input'));ui.w.document.getElementById('searchClose').click();await wait(400);
  record('M16',uiReads>0?'FAIL':'PASS','Closing search does not cancel queued input debounce',{searchOpen:ui.w.__lyraDebug.state.searchOpen,sectionReads:uiReads,resultButtons:ui.w.document.querySelectorAll('.search-result').length,searchInfo:ui.w.document.getElementById('searchInfo').textContent});
  const opened=[];ui.w.open=url=>{opened.push(url);return null;};ui.w.__lyraDebug.paginator.onLink({kind:'external',href:'javascript:window.__benignProbe=1'});
  record('M17',opened.length?'FAIL':'PASS','Reader outer external-open callback has no protocol validation',{opened,boundary:'Direct controller callback injection proves missing second check only; does not prove sanitizer bypass, script execution, or sandbox escape.'});ui.dom.window.close();
  ui=environment({reader:true});book=fakeBook(['a'.repeat(8*1024*1024+1),'UNIQUE_LATER']);ui.w.__lyraDebug.state.book=book;ui.w.__lyraDebug.state.bookId=book.bookId;ui.w.document.getElementById('findBtn').click();const input2=ui.w.document.getElementById('searchInput');input2.value='UNIQUE_LATER';input2.dispatchEvent(new ui.w.Event('keydown',{bubbles:true}));input2.dispatchEvent(new ui.w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));await wait(40);
  record('M18',ui.w.document.getElementById('searchInfo').textContent==='没有找到匹配内容'?'FAIL':'PASS','Reader zero-results message hides unscanned range',{searchInfo:ui.w.document.getElementById('searchInfo').textContent,resultButtons:ui.w.document.querySelectorAll('.search-result').length});ui.dom.window.close();
  const hashes=Object.fromEntries(['epub-book.js','search.js','paginator.js','reader.js'].map(f=>[f,crypto.createHash('sha256').update(fs.readFileSync(path.join(candidate,f))).digest('hex')]));
  const output={generatedAt:new Date().toISOString(),candidate,identity:{expectedCommit:'c96c912ea1ab1e9a7a311620663ea4f6634a3d18',expectedPackageSha256:'65cf0784ad4a006ba2444d1a5b08ef2c354b116e71325dfcc5e9a2c194170063',hashes},evidenceKind:'Original frozen JavaScript executed in Node 24 with installed jsdom DOM, real JSZip, controlled async byte/text dependencies; no browser layout or native desktop.',results};
  fs.writeFileSync(path.join(__dirname,'model-results.json'),JSON.stringify(output,null,2));
  console.log('OBSERVATIONS '+results.map(x=>x.id+':'+x.verdict).join(' '));
}
main().catch(error=>{fs.writeFileSync(path.join(__dirname,'harness-error.json'),JSON.stringify({message:error.message,stack:error.stack},null,2));console.error(error);process.exitCode=1;});
