'use strict';
// Headless Node VM only. Executes byte-for-byte frozen reader-state.js and reader.js.
// DOM, paginator geometry, WebView transport, storage and time are event doubles.
const fs = require('fs');
const path = require('path');
const vm = require('vm');
const crypto = require('crypto');
const candidate = path.resolve(__dirname, '../candidate');
const sources = Object.fromEntries(['reader-state.js', 'reader.js'].map(n => [n, fs.readFileSync(path.join(candidate, n), 'utf8')]));
const results = [], replay = [];
const copy = o => JSON.parse(JSON.stringify(o));
const drain = async () => { for (let i=0;i<12;i++) await Promise.resolve(); };
function record(id, expected, actual, pass, extra={}) { results.push({id, pass, expected, actual, ...extra}); console.log(`${pass?'PASS':'FAIL'} ${id}`); }
function env({name='A', host=true, controller=true, clock={now:1700000000000}, storage=new Map()}={}) {
  const timers = new Map(), elements = new Map(), listeners = new Map(), hostListeners=[], sent=[];
  let timerId=0, holdPrefs=false, hostLoadError=false, readError=false, writeError=false;
  function el(id) {
    if(elements.has(id))return elements.get(id);
    const events=new Map(), classes=new Set();
    const x={id,value:'',checked:false,disabled:false,textContent:'',innerHTML:'',style:{setProperty(){}},dataset:{},children:[],
      classList:{add(...v){v.forEach(a=>classes.add(a));},remove(...v){v.forEach(a=>classes.delete(a));},contains(v){return classes.has(v);},toggle(v,b){const yes=b==null?!classes.has(v):b;if(yes)classes.add(v);else classes.delete(v);return yes;}},
      addEventListener(t,f){if(!events.has(t))events.set(t,[]);events.get(t).push(f);},
      dispatch(t,e={}){return (events.get(t)||[]).map(f=>f({target:x,preventDefault(){},stopPropagation(){},...e}));},
      append(...xs){x.children.push(...xs);},appendChild(v){x.children.push(v);},focus(){},blur(){},select(){},click(){x.dispatch('click');},scrollTo(){},closest(){return null;}};
    elements.set(id,x);return x;
  }
  class FakeDate extends Date { constructor(...a){super(...(a.length?a:[clock.now]));} static now(){return clock.now;} }
  class Paginator {
    constructor(opts){this.opts=opts;this.doc=null;this.pageW=800;this.pageH=700;this.pageIndex=0;this.pageCount=20;this.anchor={textOffset:10,quote:'synthetic anchor',chapterFraction:.1};}
    captureAnchor(){return copy(this.anchor);} settleForSnapshot(){} setReduceMotion(){} setSelectionMode(){} applyStyle(){} requestLayout(){} clearHighlights(){} cancelAnimation(){}
    goToPage(n,opts){this.pageIndex=n;if(opts?.notify)this.opts.onPageChange(n,this.pageCount,{reason:'go'});}
  }
  const sandbox={console,URLSearchParams,URL,Uint8Array,ArrayBuffer,TextEncoder,Date:FakeDate,crypto:crypto.webcrypto,
    location:{search:`?sessionId=${name}&loadId=load-${name}`,reload(){}},__LYRA_TEST_MODE__:true,
    setTimeout(fn,delay=0){const id=++timerId;timers.set(id,{fn,due:clock.now+delay,delay});return id;},clearTimeout(id){timers.delete(id);},
    requestAnimationFrame(fn){return sandbox.setTimeout(()=>fn(clock.now),16);},cancelAnimationFrame(id){timers.delete(id);},
    localStorage:{getItem(k){if(readError)throw Error('synthetic read failure');return storage.get(k)??null;},setItem(k,v){if(writeError)throw Error('synthetic write failure');storage.set(k,String(v));},removeItem(k){storage.delete(k);}},
    document:{getElementById:el,querySelectorAll(){return [];},createElement:t=>el('created-'+elements.size+'-'+t),createDocumentFragment:()=>el('frag-'+elements.size),documentElement:el('root'),body:el('body'),activeElement:null,addEventListener(){}},
    addEventListener(t,f){if(!listeners.has(t))listeners.set(t,[]);listeners.get(t).push(f);},
    LyraPaginator:{Paginator},LyraEpubBook:{},LyraSearch:{},open(){},atob:s=>Buffer.from(s,'base64').toString('binary')};
  sandbox.window=sandbox;
  function reply(req,result={},ok=true,error=null){for(const f of hostListeners)f({data:{protocol:'lyra-reader',version:1,type:'response',requestId:req.requestId,sessionId:req.sessionId,ok,result,error}});}
  if(host)sandbox.chrome={webview:{addEventListener(t,f){hostListeners.push(f);},postMessage(msg){const req=copy(msg);sent.push(req);
    if(req.type==='hello')queueMicrotask(()=>reply(req,{capabilities:{persistentState:true,preferences:true,bookmarks:true}}));
    else if(req.type==='state.savePreferences'&&!holdPrefs)queueMicrotask(()=>reply(req,{applied:Object.keys(req.preferences)}));
    else if(req.type==='state.load'&&hostLoadError)queueMicrotask(()=>reply(req,{},false,{code:'STATE_UNREADABLE',message:'synthetic host read failure'}));
  }}};
  vm.createContext(sandbox);vm.runInContext(sources['reader-state.js'],sandbox,{filename:'frozen/reader-state.js'});
  if(controller)vm.runInContext(sources['reader.js'],sandbox,{filename:'frozen/reader.js'});
  return {sandbox,el,clock,timers,sent,storage,reply,get state(){return sandbox.__lyraDebug?.state;},get paginator(){return sandbox.__lyraDebug?.paginator;},
    set holdPrefs(v){holdPrefs=v;},set hostLoadError(v){hostLoadError=v;},set readError(v){readError=v;},set writeError(v){writeError=v;},
    async tick(now){clock.now=now;for(const[id,t]of [...timers])if(t.due<=now&&timers.has(id)){timers.delete(id);t.fn();await drain();}},
    dispatchHost(data){hostListeners.forEach(f=>f({data}));},dispatchWindow(t){(listeners.get(t)||[]).forEach(f=>f({}));},
    prefs(){return sent.filter(r=>r.type==='state.savePreferences');},client(){return new sandbox.LyraReaderState.ReaderStateClient({sessionId:name});},
  };
}
(async()=>{
  // Real input handlers and the real 300ms debounce. Changing another field in A postpones its older font change.
  {
    const clock={now:1700000000000},a=env({name:'order-A',clock}),b=env({name:'order-B',clock});await drain();
    a.el('fontRange').value='18';a.el('fontRange').dispatch('input');
    clock.now+=100;b.el('fontRange').value='32';b.el('fontRange').dispatch('input');
    clock.now+=100;a.el('lineRange').value='2.2';a.el('lineRange').dispatch('input');
    await b.tick(1700000000400);await a.tick(1700000000500);
    const reqs=[...b.prefs(),...a.prefs()];replay.push({id:'H-PREF-REAL-INPUT-ORDER',requests:reqs});
    record('J-PREF-SEQUENCE-IS-SEND-TIME','A earlier font change carries older authority than B later font change',reqs,reqs[1].sequence < reqs[0].sequence,{timeline:'A font18 t0; B font32 t100; A lineHeight2.2 t200; B flush400; A flush500',note:'Exact emitted envelopes replayed through frozen C# host.'});
  }
  // Inflight patch references the mutable pending object. New field/value is erased by acknowledgement for old payload.
  {
    const e=env({name:'patch-A'});await drain();e.holdPrefs=true;
    e.el('fontRange').value='18';e.el('fontRange').dispatch('input');await e.tick(1700000000300);
    const first=e.prefs()[0];e.clock.now=1700000000350;e.el('fontRange').value='32';e.el('fontRange').dispatch('input');
    const before=copy(e.state.pendingPrefsPatch);e.reply(first,{applied:['fontSize']});await drain();const after=copy(e.state.pendingPrefsPatch);
    await e.tick(1700000000650);
    record('J-PREF-ACK-ERASES-NEW-PATCH','second change32 remains pending and is emitted', {before,after,uiFont:e.state.prefs.fontSize,sent:e.prefs(),saveLabel:e.el('saveStateText').textContent},e.prefs().length===2);
    replay.push({id:'H-PREF-INFLIGHT-LOST',requests:e.prefs()});
  }
  // Full controller is initialized. Deliver host.finalize through its actual message listeners.
  {
    const e=env({name:'finalize-A'});await drain();e.el('fontRange').value='30';e.el('fontRange').dispatch('input');
    const start=e.sent.length;e.dispatchHost({type:'host.finalize',requestId:'close-exact-id'});await drain();
    const afterFinalize=e.sent.slice(start);e.dispatchWindow('beforeunload');e.dispatchWindow('pagehide');await drain();
    record('J-FINALIZE-HANDLER','host.finalize flushes pending preferences and returns correlated host.finalized',{afterFinalize,afterUnload:e.sent.slice(start),pending:copy(e.state.pendingPrefsPatch)},e.sent.slice(start).some(r=>r.type==='host.finalized'));
    // Normal 300ms timer still works: lack of finalize does not imply every native 1.5s close loses data.
    await e.tick(1700000000300);record('J-PREF-NORMAL-TIMER','regular debounce saves before close timeout',e.prefs(),e.prefs().length===1);
  }
  // Node module checks for old R1 defects and remaining local consistency failures.
  {
    const e=env({host:false,controller:false});const c=e.client(),book='sha256:'+ 'a'.repeat(64);const counts=[];
    for(const target of [0,1,50,51,499,500]){let current=(await c.listBookmarks(book)).total;for(let i=current;i<target;i++)await c.addBookmark(book,{id:'bm'+i,summary:'s'});
      let cur=null,seen=[],pages=0;do{const p=await c.listBookmarks(book,cur,50);seen.push(...p.bookmarks.map(v=>v.id));cur=p.nextCursor;if(++pages>20)throw Error('nonterminating cursor');}while(cur);counts.push({target,total:seen.length,unique:new Set(seen).size,pages});}
    let cap='';try{await c.addBookmark(book,{id:'bm500'});}catch(x){cap=x.code;}
    record('J-LOCAL-CURSOR-CAP','all 0/1/50/51/499/500 read uniquely; 501 rejected with first500 retained',{counts,cap,total:(await c.listBookmarks(book)).total},counts.every(x=>x.target===x.total&&x.total===x.unique)&&cap==='BOOKMARK_LIMIT');
    e.writeError=true;const errors={};for(const[type,fn]of Object.entries({position:()=>c.savePosition(book,{x:1}),preferences:()=>c.savePreferences({fontSize:28}),bookmark:()=>c.addBookmark('sha256:'+'b'.repeat(64),{id:'x'})})){try{await fn();errors[type]='SUCCESS';}catch(x){errors[type]=x.code;}}
    record('J-LOCAL-WRITE-FAIL','all writes reject',errors,Object.values(errors).every(x=>x==='LOCAL_WRITE_FAILED'));
  }
  {
    const e=env({host:false,controller:false});const c=e.client(),book='sha256:'+'c'.repeat(64);
    const bookmark={id:'oversized-local',summary:'汉'.repeat(257),locator:{quote:'汉'.repeat(2000)}};let result;
    try{result=await c.addBookmark(book,bookmark);}catch(x){result={error:x.code};}
    record('J-LOCAL-SINGLE-ITEM-LIMIT','same host limit: 4096 UTF8 bytes and summary<=256 UTF16',{bytes:Buffer.byteLength(JSON.stringify(bookmark)),summaryLength:bookmark.summary.length,accepted:!result.error},!!result.error);
    replay.push({id:'H-BOOKMARK-LOCAL-PARITY',bookmark});
  }
  {
    const storage=new Map([['lyra_epub_prefs',JSON.stringify({preferences:{fontSize:32,theme:'sepia'},fieldSequences:{}})]]);
    const e=env({host:false,controller:false,storage});const c=e.client();const before=storage.get('lyra_epub_prefs');e.readError=true;
    let status;try{status=await c.savePreferences({lineHeight:2.2});}catch(x){status={error:x.code};}e.readError=false;
    record('J-LOCAL-PREF-READ-FAIL','failed read preserves old prefs and rejects',{before,after:storage.get('lyra_epub_prefs'),status},before===storage.get('lyra_epub_prefs'));
  }
  {
    const e=env({host:true,controller:false,name:'degrade'}),c=e.client();await c.hello();e.hostLoadError=true;
    const load=await c.load('sha256:'+'d'.repeat(64));const before=e.sent.length;await c.savePosition('sha256:'+'d'.repeat(64),{p:3});await c.savePreferences({fontSize:28});await c.addBookmark('sha256:'+'d'.repeat(64),{id:'degraded'});
    record('J-DEGRADE-ROUTE','load failure permanently routes subsequent writes local',{loadMode:load.mode,mode:c.mode,requestsAfterLoad:e.sent.slice(before),localKeys:[...e.storage.keys()]},load.mode==='local'&&c.mode==='local'&&e.sent.length===before);
  }
  {
    const e=env({host:true,controller:false,name:'envelope'}),c=e.client();await c.hello();e.holdPrefs=true;
    const p=c.savePreferences({fontSize:26});const req=e.prefs()[0];let settled=false;p.then(()=>settled=true);
    for(const[k,v]of Object.entries({protocol:'wrong',version:99,type:'wrong',sessionId:'old'})){e.dispatchHost({protocol:'lyra-reader',version:1,type:'response',sessionId:c.sessionId,requestId:req.requestId,ok:true,result:{},[k]:v});}
    await drain();const invalidSettled=settled;e.reply(req,{applied:['fontSize']});await p;
    record('J-RESPONSE-ENVELOPE','bad envelopes ignored; valid resolves',{invalidSettled,validSettled:settled},!invalidSettled&&settled);
  }
  {
    const e=env({host:false,controller:false}),c=e.client();const first=c.nextSequence();e.clock.now-=3600000;const second=c.nextSequence();
    record('J-CLOCK-ROLLBACK-SEQUENCE','sequence is strictly increasing after wall clock correction',{first,second},second>first);
    replay.push({id:'H-CLOCK-ROLLBACK',sequences:[first,second]});
  }
  fs.writeFileSync(path.join(__dirname,'host-input.json'),JSON.stringify({evidenceKind:'Raw protocol requests emitted by frozen JS under headless event doubles',scenarios:replay},null,2));
  fs.writeFileSync(path.join(__dirname,'client-results.json'),JSON.stringify({generatedAt:new Date().toISOString(),evidenceKind:'Node VM; original frozen JS; synthetic DOM/time/WebView/paginator; not browser/native',sourceHashes:Object.fromEntries(Object.keys(sources).map(n=>[n,crypto.createHash('sha256').update(sources[n]).digest('hex')])),results},null,2));
  console.log(`Recorded ${results.length} assertions, ${results.filter(x=>!x.pass).length} unmet product expectations. Diagnostic exit is zero when probes completed.`);
})().catch(e=>{console.error(e);process.exitCode=1;});
