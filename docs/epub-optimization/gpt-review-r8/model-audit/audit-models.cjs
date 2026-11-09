/* Independent R3 model audit; run node audit-models.cjs. Exit 2 = completed with product FAILs. */
'use strict';
const {performance}=require('node:perf_hooks');
const {environment,fakeBook,packedBook,deferred,wait,output,recorder}=require('./audit-common.cjs');
const results=[],record=recorder(results),LIMIT=8*1024*1024;
async function main(){
 const e=environment(),{w,Book,Search,Paginator,created,revoked}=e;
 try{
  const counts=[];
  for(const texts of [['needle '.repeat(1000)],['needle '.repeat(1001)],['needle '.repeat(1000),'needle']]){const r=await new Search(fakeBook(texts)).search('needle');counts.push({expected:texts.join('').match(/needle/g).length,total:r.total,count:r.results.length,truncated:r.truncated,scanned:r.scannedSections});}
  record('S01',counts.every(r=>r.total===r.expected&&r.count===Math.min(1000,r.expected)&&r.truncated===(r.expected>1000)),'Exact 1000, same/cross chapter 1001',counts);
  const cap=[];
  for(const texts of [['a'.repeat(LIMIT+1),'UNIQUE_LATER'],['pre','a'.repeat(LIMIT+1),'UNIQUE_LATER']]){
   const reads=[],b=fakeBook(texts);b.getSectionPlainText=async i=>{reads.push(i);return texts[i];};const s=new Search(b),r=await s.search('UNIQUE_LATER');
   cap.push({total:r.total,scanned:r.scannedSections,sections:r.totalSections,cacheTruncated:r.cacheTruncated,truncated:r.truncated,cacheChars:s._chars,cached:s._sections.map(x=>({index:x.index,length:x.length,retainedChars:x.text?.length||0})),reads});
  }
  record('S02',cap.every(r=>r.total===1&&r.scanned===r.sections&&r.cacheChars<=LIMIT),'Over-limit first/middle chapter still scans later chapter; cap unit is UTF-16 code units',cap);
  let s=new Search(fakeBook(['needle']));await s.ensureIndex();const hot=s.search('needle');s.cancel();let err='';try{await hot;}catch(x){err=x.message;}record('S03',/取消/.test(err),'Hot cache query observes immediate cancellation',{error:err});
  let gate=deferred(),started=deferred();s=new Search({spine:[{}],getSectionPlainText:async()=>{started.resolve();await gate.promise;return 'old new';},sectionTitle:async()=>''});
  const old=s.search('old');await started.promise;s.cancel();const fresh=s.search('new');gate.resolve();const settled=await Promise.allSettled([old,fresh]);record('S04',settled[0].status==='rejected'&&settled[1].value?.total===1,'Cancelled cold build not reused by new query',{statuses:settled.map(x=>x.status),errors:settled.map(x=>x.reason?.message),freshTotal:settled[1].value?.total});
  const yieldCases=[];
  for(const [name,texts,q]of [['dense',['x '.repeat(LIMIT/2)],'x'],['zero-match',['a'.repeat(LIMIT)],'missing'],['one-end-match',['a'.repeat(LIMIT-6)+'needle'],'needle'],['many-zero-match',Array(64).fill('a'.repeat(131072)),'missing']]){
   s=new Search(fakeBook(texts));await s.ensureIndex();let timerRan=false;const timer=setTimeout(()=>{timerRan=true;s.cancel();},0),t=performance.now();let cancelled=false,res=null;
   try{res=await s.search(q);}catch(x){cancelled=/取消/.test(x.message);}clearTimeout(timer);
   yieldCases.push({name,totalChars:texts.reduce((a,x)=>a+x.length,0),cancelled,timerRanAtReturn:timerRan,total:res?.total,elapsedMs:+(performance.now()-t).toFixed(3)});
  }
  record('S05',yieldCases[0].cancelled,'Dense long chapter scan yields',yieldCases[0]);
  record('S06',yieldCases.slice(1).every(x=>x.cancelled),'Sparse/no-match hot scan yields independently of number of matches',yieldCases.slice(1));
  const errorsBook=fakeBook(['a','b','needle']);errorsBook.getSectionPlainText=async i=>{if(i===1)throw Error('controlled bad chapter');return ['a','b','needle'][i];};const errorsRes=await new Search(errorsBook).search('needle');record('S07',errorsRes.total===1&&errorsRes.readErrors===1&&errorsRes.truncated&&errorsRes.scannedSections===3,'Read failure counted, remaining sections still searched',{total:errorsRes.total,readErrors:errorsRes.readErrors,truncated:errorsRes.truncated,scanned:errorsRes.scannedSections});
  // Real ZIP/DOM parsing above cap, including images; search must not allocate resource URLs.
  let b=await Book.open(await packedBook({chapters:['<p>'+'a'.repeat(LIMIT+1)+'</p><img src="p.png">','<p>REAL_LATER</p>'],extras:{'OPS/p.png':new Uint8Array([1])}}),'big.epub');const createBefore=created.length;const realReads=[],plain=b.getSectionPlainText.bind(b);b.getSectionPlainText=async i=>{realReads.push(i);return plain(i);};s=new Search(b);const real=await s.search('REAL_LATER');record('S08',real.total===1&&s._chars<=LIMIT&&created.length===createBefore,'Real frozen parser + ZIP oversized chapter coverage and no search resource allocation',{total:real.total,scanned:real.scannedSections,cacheChars:s._chars,reads:realReads,createdDelta:created.length-createBefore,resources:b._resources.size,taskSets:b._taskResources.size});b.destroy();
  // R2 resource positives and task crossovers.
  b=new Book({},new ArrayBuffer(0),'audit.epub');gate=deferred();b.readBytes=()=>gate.promise;const beforeDestroy=created.length;const pending=b._resourceUrl('image.png');b.destroy();gate.resolve(new Uint8Array([1]));const late=await pending;record('R01',late===''&&b._resources.size===0&&created.length===beforeDestroy,'Destroy while bytes pending prevents allocation',{url:late,resources:b._resources.size,createdDelta:created.length-beforeDestroy});
  b=new Book({},new ArrayBuffer(0),'audit.epub');let reads=0;b.readBytes=async()=>{reads++;return new Uint8Array([1]);};const beforeDedup=created.length;const urls=await Promise.all([b._resourceUrl('same.png'),b._resourceUrl('same.png'),b._resourceUrl('same.png')]);record('R02',reads===1&&new Set(urls).size===1&&created.length-beforeDedup===1,'True concurrent resource pending deduplication',{reads,urls,createdDelta:created.length-beforeDedup});b.destroy();
  b=await Book.open(await packedBook({chapters:['<img src="a.png">','<img src="b.png">','<img src="c.png">'],extras:{'OPS/a.png':[1],'OPS/b.png':[2],'OPS/c.png':[3]}}),'ownership.epub');
  let originalRead=b.readBytes.bind(b);gate=deferred();started=deferred();let startedCount=0;b.readBytes=async p=>{if(!p.endsWith('c.png')){if(++startedCount===2)started.resolve();await gate.promise;}return originalRead(p);};const aa=b.prepareSection(0),bb=b.prepareSection(1);await started.promise;const cc=await b.prepareSection(2);b.releaseResourcesExcept(cc.resources);gate.resolve();const ab=await Promise.all([aa,bb]);ab.forEach(x=>b.releaseTask(x.owner));record('R03',b._resources.size===1&&b._resourcePending.size===0&&b._taskResources.size===1,'Stale A/B finish after C; actual task cleanup bounds live resources and task metadata',{current:cc.resources,paths:[...b._resources.keys()],pending:b._resourcePending.size,taskSets:b._taskResources.size});b.destroy();
  const sharing=[];
  for(const order of ['old-byte-pending','new-byte-pending-old-text-pending']){
   b=await Book.open(await packedBook({chapters:['<img src="same.png">','<img src="same.png">'],extras:{'OPS/same.png':[1,2,3]}}),'shared.epub');
   const byteGate=deferred(),byteStarted=deferred(),textGate=deferred(),textStarted=deferred();originalRead=b.readBytes.bind(b);let n=0;
   b.readBytes=async p=>{n++;byteStarted.resolve();await byteGate.promise;return originalRead(p);};const originalText=b.readText.bind(b);
   if(order.startsWith('new-'))b.readText=async p=>{if(p==='OPS/c0.xhtml'){textStarted.resolve();await textGate.promise;}return originalText(p);};
   const a=b.prepareSection(0);if(order.startsWith('new-'))await textStarted.promise;else await byteStarted.promise;
   const c=b.prepareSection(1);await byteStarted.promise;textGate.resolve();await wait(5);byteGate.resolve();const [pa,pc]=await Promise.all([a,c]);b.releaseTask(pa.owner);b.releaseResourcesExcept(pc.resources);
   sharing.push({order,reads:n,currentHtml:pc.html,currentPaths:pc.resources,resourcePaths:[...b._resources.keys()],taskSets:b._taskResources.size,pending:b._resourcePending.size,liveUrl:pc.html.match(/blob:[^\"]+/)?.[0],revokedCurrent:revoked.includes(pc.html.match(/blob:[^\"]+/)?.[0])});b.destroy();
  }
  record('R04',sharing.every(x=>x.currentPaths.length===1&&x.resourcePaths.length===1&&!x.revokedCurrent&&x.taskSets===1&&x.pending===0),'Same-book old/new pending shared image survives both interleavings',sharing);
  b=await Book.open(await packedBook({chapters:['<img src="same.png">','<img src="other.png">'],extras:{'OPS/same.png':[1],'OPS/other.png':[2]}}),'stress.epub');let last=null;const stressStart={created:created.length,revoked:revoked.length};const samples=[];
  for(let i=0;i<201;i++){const p=await b.prepareSection(i%2);if(last)b.releaseTask(last.owner);b.releaseResourcesExcept(p.resources);last=p;if(i%50===0)samples.push({transitions:i,resources:b._resources.size,taskSets:b._taskResources.size,pending:b._resourcePending.size,createdMinusRevoked:(created.length-stressStart.created)-(revoked.length-stressStart.revoked)});}
  record('R05',samples.every(x=>x.resources===1&&x.taskSets===1&&x.pending===0&&x.createdMinusRevoked===1),'200 sequential image chapter transitions settle with one live resource/task',samples);b.destroy();
  b=await Book.open(await packedBook(),'failure.epub');b.readText=async()=>{throw Error('controlled text read failure');};for(let i=0;i<40;i++){try{await b.prepareSection(0);}catch{}}
  record('R06',b._taskResources.size<=1,'Failed prepare before returned owner does not retain unbounded task sets',{attempts:40,taskSets:b._taskResources.size,resources:b._resources.size,pending:b._resourcePending.size,owners:[...b._taskResources.keys()]});b.destroy();
  // Paths, HTML/SVG resource href and fragment.
  b=await Book.open(await packedBook({chapters:['<img src="pic%20a.png"><img src="pic%2520a.png"><img src="view.svg#one"><svg xmlns="http://www.w3.org/2000/svg"><image href="pic%20a.png"/><image xmlns:xlink="http://www.w3.org/1999/xlink" xlink:href="pic%2520a.png"/><image href="view.svg#two"/></svg>'],extras:{'OPS/pic a.png':[1,2,3],'OPS/pic%20a.png':[9,8,7],'OPS/view.svg':'<svg xmlns="http://www.w3.org/2000/svg"><view id="one"/><view id="two"/></svg>'}}),'paths.epub');
  const refs=['pic%20a.png','pic%2520a.png','%E4%B8%AD%E6%96%87.png','hash%23percent%25.png','../%2e%2e/out.png','bad%2Fname.png'].map(href=>({href,...b.resolveReference('OPS/c0.xhtml',href)}));const bytes1=Array.from(await b.readBytes(refs[0].path)),bytes2=Array.from(await b.readBytes(refs[1].path));const prep=await b.prepareSection(0);
  record('P01',JSON.stringify(bytes1)==='[1,2,3]'&&JSON.stringify(bytes2)==='[9,8,7]'&&refs[4].kind==='invalid'&&refs[5].kind==='invalid'&&prep.html.includes('#one')&&prep.html.includes('#two')&&prep.resources.length===6,'One decode distinct complete bytes, HTML/SVG href/xlink and fragments',{refs,bytes1,bytes2,html:prep.html,resources:prep.resources,boundary:'Does not prove SVG view painting or valid image decodability.'});b.destroy();
  // Shared model / real DOM Range offsets. Geometry is deliberately not asserted.
  const body='<p>甲<span>乙</span>丙 😀 甲<span>乙</span>丙</p><p>Hello  \n <em>world</em></p><p>cross</p><p>block</p><p>non&nbsp;breaking</p><p>A<span hidden>HIDDEN_SECRET</span>B</p><aside hidden>FOOTNOTE_SECRET</aside><details><summary>Summary</summary>CLOSED_SECRET</details><details open><summary>Open summary</summary>OPEN_VISIBLE</details><svg xmlns="http://www.w3.org/2000/svg"><defs><text>SVG_DEF_SECRET</text></defs><desc>SVG_DESC_SECRET</desc><text>SVG_VISIBLE</text></svg>';
  b=await Book.open(await packedBook({chapters:[body]}),'text.epub');const prepared=await b.prepareSection(0),root=w.document.createElement('div');root.innerHTML=prepared.html;const p=new Paginator({frame:w.document.createElement('iframe')});p.doc=w.document;p.content=root;s=new Search(b);const checks=[];
  for(const q of ['甲乙丙','😀','Hello world','cross block','non breaking']){const res=await s.search(q);checks.push({q,hits:res.results.map(x=>({offset:x.offset,length:x.length,modelSlice:p.getTextModel().text.slice(x.offset,x.offset+x.length),rangeText:p.rangeAtTextOffset(x.offset,x.length).toString()}))});}
  record('T01',(await b.getSectionPlainText(0))===p.getTextModel().text&&checks[0].hits.length===2&&checks[0].hits.every(x=>x.rangeText==='甲乙丙')&&checks[1].hits[0].length===2&&checks[3].hits[0].rangeText==='crossblock','Shared UTF-16 model / duplicates / inline / NBSP / virtual block separators',{checks,note:'Range.toString omits virtual model newlines; this is expected.'});
  const visible=[];for(const q of ['HIDDEN_SECRET','FOOTNOTE_SECRET','CLOSED_SECRET','SVG_DEF_SECRET','SVG_DESC_SECRET','AB','OPEN_VISIBLE','SVG_VISIBLE']){const r=await s.search(q);visible.push({q,total:r.total,range:r.results[0]?p.rangeAtTextOffset(r.results[0].offset,r.results[0].length).toString():null});}
  record('T02',visible.slice(0,5).every(x=>x.total===0)&&visible.slice(5).every(x=>x.total===1),'Basic hidden/details/SVG defs/desc model semantics fixed',{queries:visible,note:'AB Range spans hidden DOM text, but visible model is AB; no pixel claim.'});p.destroy();b.destroy();
  const edgeBody='<p id="brcase">A<br hidden>B</p><svg xmlns="http://www.w3.org/2000/svg"><g visibility="hidden"><text>SVG_HIDDEN_VISIBILITY</text></g><g display="none"><text>SVG_HIDDEN_DISPLAY</text></g><symbol id="unused"><text>SVG_UNUSED_SYMBOL</text></symbol><text>SVG_ACTUAL_TEXT</text></svg><p>V<span aria-hidden="true">VISUALLY_PRESENT</span>W</p>';
  b=await Book.open(await packedBook({chapters:[edgeBody]}),'semantics.epub');s=new Search(b);const edge=[];for(const q of ['AB','SVG_HIDDEN_VISIBILITY','SVG_HIDDEN_DISPLAY','SVG_UNUSED_SYMBOL','SVG_ACTUAL_TEXT','VISUALLY_PRESENT']){const r=await s.search(q);edge.push({q,total:r.total});}
  record('T03',edge[0].total===1&&edge.slice(1,4).every(x=>x.total===0),'Hidden br and retained SVG display/visibility/symbol semantics',{queries:edge,plain:await b.getSectionPlainText(0),preparedHtml:(await b.prepareSection(0)).html,boundary:'DOM semantics and unchanged attributes, not actual Edge visibility. aria-hidden-only exclusion is recorded as policy observation, not a separate FAIL.'});b.destroy();
  // Classify encryption via real open/init. Font existence and exact MIME are separate from decrypting bytes.
  const idpf='http://www.idpf.org/2008/embedding',enc=(algo,uri)=>`<encryption><EncryptedData><EncryptionMethod Algorithm="${algo}"/><CipherData><CipherReference URI="${uri}"/></CipherData></EncryptedData></encryption>`;
  const cases=[
   {name:'valid-IDPF-font',algo:idpf,uri:'OPS/font.otf',mime:'font/otf',exists:true,expected:true},
   {name:'unknown-lookalike',algo:'https://invalid.example/idpf/aes',uri:'OPS/font.otf',mime:'font/otf',exists:true,expected:false},
   {name:'IDPF-XHTML',algo:idpf,uri:'OPS/c0.xhtml',expected:false},
   {name:'Adobe-font',algo:'http://ns.adobe.com/pdf/enc#RC',uri:'OPS/font.otf',mime:'font/otf',exists:true,expected:false},
   {name:'missing-font-entry',algo:idpf,uri:'OPS/font.otf',mime:'font/otf',exists:false,expected:false},
   {name:'nonfont-MIME-containing-font',algo:idpf,uri:'OPS/font.otf',mime:'application/x-not-a-font',exists:true,expected:false},
   {name:'nonfont-MIME-containing-woff',algo:idpf,uri:'OPS/font.otf',mime:'text/x-woff-comment',exists:true,expected:false},
   {name:'unknown-target',algo:idpf,uri:'OPS/unknown.otf',expected:false},
   {name:'root-relative-font',algo:idpf,uri:'font.otf',mime:'font/otf',exists:true,expected:false},
  ];
  const encResults=[];for(const c of cases){let opened=false,error=null,fontFlag=false;try{const x=await Book.open(await packedBook({encryption:enc(c.algo,c.uri),manifestExtra:c.mime?`<item id="font" href="font.otf" media-type="${c.mime}"/>`:'',extras:c.exists?{'OPS/font.otf':new Uint8Array([0,1,2,3])}:{}}),c.name+'.epub');opened=true;fontFlag=x.fontObfuscated;x.destroy();}catch(x){error={code:x.code,message:x.message};}encResults.push({...c,opened,fontFlag,error});}
  record('E01',encResults.slice(0,4).every(x=>x.opened===x.expected),'Exact supported algorithm and ordinary nonfont checks',encResults.slice(0,4));
  record('E02',encResults.slice(4,8).every(x=>x.opened===x.expected),'Target must exist in ZIP and MIME must actually identify font',encResults.slice(4,8));
  record('E03','OBSERVATION','CipherReference non-root fallback permits URI absent at root',{case:encResults[8],note:'Spec-relative interpretation requires policy review; not separately counted as confirmed FAIL.'});
  // Sanitizer independent of reader outer protocol boundary.
  const links='<a id="js" href="javascript:void(0)">JS</a><a id="data" href="data:text/plain,x">DATA</a><a id="file" href="file:///audit">FILE</a><a id="https" href="https://example.invalid/ok">HTTPS</a><a id="forged" href="https://example.invalid/real" data-lyra-external="javascript:x">FORGED</a><div data-lyra-external="javascript:x">D</div><svg xmlns="http://www.w3.org/2000/svg"><a id="svg" xlink:href="javascript:x" xmlns:xlink="http://www.w3.org/1999/xlink"><text>X</text></a><a id="svgok" href="https://example.invalid/svg"><text>OK</text></a></svg><script>x</script><img onerror="x">';
  b=await Book.open(await packedBook({chapters:[links]}),'links.epub');root.innerHTML=(await b.prepareSection(0)).html;const info=Array.from(root.querySelectorAll('a')).map(a=>({id:a.id,href:a.getAttribute('href'),xlink:a.getAttribute('xlink:href'),external:a.getAttribute('data-lyra-external')}));record('P02',info.filter(x=>['js','data','file','svg'].includes(x.id)).every(x=>!x.href&&!x.xlink&&!x.external)&&!root.querySelector('div[data-lyra-external],script,[onerror]')&&info.find(x=>x.id==='forged').external==='https://example.invalid/real','Sanitizer protocol check, SVG links and forged control attributes',info);b.destroy();
 }finally{e.dom.window.close();output('model-results.json',results);}
}
main().catch(x=>{console.error(x);process.exitCode=1;});
