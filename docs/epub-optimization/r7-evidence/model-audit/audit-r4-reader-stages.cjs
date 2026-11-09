/* Original full reader + original full paginator. Only jsdom iframe srcdoc/load, resource-complete and geometry are bridged.
 * Method wrappers observe and delegate; fault/delay injection is after original dependency/DOM execution. No business source rewriting.
 */
'use strict';
const {fs,path,candidate,environment,packedBook,deferred,wait,until,output,recorder}=require('./audit-common.cjs');
const results=[],record=recorder(results);
function setup(){
 const e=environment({reader:true}),{w}=e;w.__LYRA_TEST_MODE__=true;const ledger=[],hostMessages=[],hooks={resource:null};let state,p;
 const push=(stage,extra={})=>ledger.push({stage,book:state?.book?.title,spine:state?.spineIndex,queryGen:state?.searchGen,navGen:state?.navGeneration,openIntent:state?.openIntent,finalizing:state?.finalizing,...extra});
 const frame=w.document.getElementById('chapterFrame');Object.defineProperty(frame,'clientWidth',{value:600});Object.defineProperty(frame,'clientHeight',{value:500});
 Object.defineProperty(w.HTMLImageElement.prototype,'complete',{configurable:true,get(){return true;}});
 let srcdoc='';Object.defineProperty(frame,'srcdoc',{configurable:true,get(){return srcdoc;},set(value){srcdoc=value;const d=frame.contentDocument;d.open();d.write(value);d.close();const root=d.getElementById('lyra-content');for(const img of d.images)Object.defineProperty(img,'complete',{configurable:true,value:true});if(root)Object.defineProperty(root,'scrollWidth',{configurable:true,value:1928});
  const rectFor=el=>({left:Number(el?.closest?.('[data-page]')?.getAttribute('data-page')||0)*664-(p?.scrollPos||0),top:0,width:100,height:30,right:100,bottom:30});
  d.defaultView.Range.prototype.getBoundingClientRect=function(){return rectFor(this.startContainer.nodeType===3?this.startContainer.parentElement:this.startContainer);};d.defaultView.Range.prototype.getClientRects=function(){return [this.getBoundingClientRect()];};
  d.defaultView.Element.prototype.getBoundingClientRect=function(){return rectFor(this);};
  d.caretRangeFromPoint=()=>{const el=root?.querySelector('[data-page="'+(p?.pageIndex||0)+'"]')||root?.querySelector('p');if(!el||!el.firstChild||el.firstChild.nodeType!==3)return null;const r=d.createRange();r.setStart(el.firstChild,0);r.collapse(true);return r;};
  d.elementFromPoint=()=>root?.querySelector('img')||root;
  push('dom-submit',{text:root?.textContent,preparedBook:p?._activePrepared?.book?.title,preparedIndex:p?._activePrepared?.index});queueMicrotask(()=>frame.dispatchEvent(new w.Event('load')));
 }});
 const proto=e.Paginator.prototype;
 for(const name of ['layout','goToPage','locateAnchor','highlightRanges']){const orig=proto[name];proto[name]=function(...args){push(name,{section:this.sectionIndex,arg:name==='goToPage'?args[0]:undefined,text:name==='highlightRanges'?args[0].map(r=>r.toString()).join('|'):undefined});return orig.apply(this,args);};}
 const resource=proto._waitForResources;proto._waitForResources=async function(){const prepared=this._activePrepared;await resource.call(this);if(hooks.resource)await hooks.resource(prepared);};
 const set=proto.setSection;proto.setSection=async function(prepared,opts){push('set-enter',{preparedBook:prepared.book.title,index:prepared.index});const x=await set.call(this,prepared,opts);push('set-return',{preparedBook:prepared.book.title,index:prepared.index});return x;};
 let seq=0,revision=0;w.LyraReaderState={ReaderStateClient:class{constructor(){this.sessionId='audit';this.isPersistent=false;this.lastRevision=0;}appReady(){}hello(){return new Promise(()=>{});}async load(){return{};}migrateLegacy(){return{};}async listBookmarks(){return{bookmarks:[],total:0};}bookBegin(){}bookReady(){}bookError(){}nextSequence(){return ++seq;}async grantPreferences(){return{grant:'g'};}async savePreferences(p){return{applied:Object.keys(p)};}async savePosition(){this.lastRevision=++revision;return{revision};}}};
 const hostListeners=[];w.chrome={webview:{addEventListener:(type,fn)=>{if(type==='message')hostListeners.push(fn);},postMessage:m=>hostMessages.push(m)}};
 w.eval(fs.readFileSync(path.join(candidate,'reader.js'),'utf8'));state=w.__lyraDebug.state;p=w.__lyraDebug.paginator;
 const dispatch=(buffer,title,byteGate=null)=>{const input=w.document.getElementById('file');Object.defineProperty(input,'files',{configurable:true,value:[{name:title+'.epub',type:'application/epub+zip',arrayBuffer:async()=>{if(byteGate)await byteGate.promise;return buffer;}}]});input.dispatchEvent(new w.Event('change',{bubbles:true}));};
 const open=async(title,chapters,extras={})=>{dispatch(await packedBook({title,chapters,extras}),title);await until(()=>state.ready&&!state.busy&&state.fileName===title+'.epub','open '+title);return state.book;};
 const click=id=>w.document.getElementById(id).click(),query=(q,input=false)=>{const el=w.document.getElementById('searchInput');el.value=q;el.dispatchEvent(input?new w.Event('input',{bubbles:true}):new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));};
 const search=async q=>{click('findBtn');query(q);await until(()=>w.document.querySelector('.search-result'),'search');};
 return{...e,state,p,ledger,hooks,hostMessages,hostListeners,push,open,dispatch,click,query,search};
}
async function isolated(fn){const e=setup();try{await fn(e);}finally{e.state.book?.destroy();e.dom.window.close();}}
async function caseNavigation(phase,action){await isolated(async e=>{
 const {w,state,p,ledger,hooks,open,search,query,click,dispatch,push}=e;await open('STAGE',['<p data-page="0">SAFE_START</p>','<p data-page="0">OLDQUERY target</p><p data-page="2">TAIL</p>']);await search('OLDQUERY');
 const gate=deferred(),started=deferred();let released=false;
 if(phase==='prepare'){const read=state.book.readText.bind(state.book);state.book.readText=async path=>{if(path==='OPS/c1.xhtml'){started.resolve();await gate.promise;}return read(path);};}
 else hooks.resource=async prep=>{if(prep.book.title==='STAGE'&&prep.index===1&&!released){started.resolve();await gate.promise;}};
 w.document.querySelector('.search-result').click();await started.promise;
 const nextBuffer=await packedBook({title:'FRESH',chapters:['<p data-page="0">FRESH_TEXT</p>']}),bytes=deferred();
 if(action==='input')query('NEWQUERY',true);
 else if(action==='search-close')click('searchClose');
 else if(action==='open-intent')dispatch(nextBuffer,'FRESH',bytes);
 else if(action==='book-replaced'){dispatch(nextBuffer,'FRESH');await until(()=>state.ready&&state.book.title==='FRESH','new book');}
 else if(action==='host-finalize'){for(const f of e.hostListeners)f({data:{type:'host.finalize',requestId:'audit-final'}});await until(()=>e.hostMessages.some(m=>m.type==='host.finalized'),'host ack');}
 push('invalidation',{action});const cut=ledger.length;released=true;gate.resolve();await wait(75);
 const after=ledger.slice(cut),snapshot={phase,action,currentBook:state.book.title,spine:state.spineIndex,paginatorSection:p.sectionIndex,text:p.content?.textContent,ready:state.ready,finalizing:state.finalizing,highlights:[...p.content?.querySelectorAll('.lyra-search-mark')||[]].map(x=>x.textContent),activeQuery:state.activeSearchResult?.query||null,after,hostMessages:e.hostMessages.slice(),ledgerBefore:ledger.slice(-18,cut)};
 const staleEffects=after.filter(x=>['dom-submit','layout','goToPage','highlightRanges','locateAnchor'].includes(x.stage)&&x.preparedBook!=='FRESH');
 record('N-'+phase+'-'+action,staleEffects.length===0,'Invalidate result navigation before further DOM/layout/location/highlight side effects',snapshot);
 if(action==='open-intent'){bytes.resolve();await until(()=>state.ready&&state.book.title==='FRESH','finish delayed new open');}
 if(action!=='host-finalize'&&action!=='book-replaced'&&action!=='open-intent'){click('pageNext');await wait(30);record('NP-'+phase+'-'+action,state.ready,'Subsequent user page turn remains usable',{book:state.book.title,spine:state.spineIndex,page:p.pageIndex,text:p.content?.textContent});}
 });}
async function main(){
 for(const phase of ['prepare','resources'])for(const action of ['input','search-close','open-intent','book-replaced','host-finalize'])await caseNavigation(phase,action);
 await isolated(async e=>{const{p,state,open,dispatch,ledger,revoked}=e;const old=await open('ROLL-OLD',['<p data-page="0">OLD_HEAD</p><p data-page="2">OLD_TARGET</p><img src="old.png">'],{'OPS/old.png':[1,2]});p.goToPage(2,{animate:false,notify:true});const before=p.captureAnchor(),oldUrl=p.content.querySelector('img').src;const set=p.setSection.bind(p);let injected=false;p.setSection=async(prep,opts)=>{await set(prep,opts);if(prep.book.title==='ROLL-NEW'&&!injected){injected=true;throw Error('after original setSection submitted/laid out NEW document');}};
 dispatch(await packedBook({title:'ROLL-NEW',chapters:['<p data-page="0">NEW_DOCUMENT</p><img src="new.png">'],extras:{'OPS/new.png':[3,4]}}),'ROLL-NEW');await until(()=>injected&&state.ready&&!state.busy&&state.book===old,'original DOM rollback');const after=p.captureAnchor(),url=p.content.querySelector('img').src;
 record('N-ROLLBACK',p.content.textContent.includes('OLD_TARGET')&&!p.content.textContent.includes('NEW_DOCUMENT')&&after.textOffset===before.textOffset&&after.pageIndex===2&&!revoked.includes(url),'Failure after original DOM submit/layout restores old document, text position and image',{before,after,oldUrl,url,revoked:revoked.slice(),resources:[...old._resources.keys()],owners:[...old._taskResources.keys()],ledger});
 });
 await isolated(async e=>{const{state,p,open,click,revoked}=e;await open('SHARED-FAIL',['<p>OLD_VISIBLE</p><img src="same.png">','<p>NEW</p><img src="same.png">'],{'OPS/same.png':[1,2]});const title=state.book.sectionTitle.bind(state.book),url=p.content.querySelector('img').src;state.book.sectionTitle=async i=>{if(i===1)throw Error('controlled title failure after resources');return title(i);};click('chapterNext');await until(()=>!state.busy,'chapter failure');await wait(30);record('N-SHARED-FAIL',!revoked.includes(url),'Failed same-book prepare retains displayed old shared URL',{text:p.content.textContent,spine:state.spineIndex,paginatorSection:p.sectionIndex,ready:state.ready,url,revoked:revoked.slice(),resources:[...state.book._resources.keys()],owners:[...state.book._taskResources.keys()]});});
 output('r4-reader-stage-results.json',results);
}
main().catch(x=>{console.error(x);output('r4-reader-stage-results.json',results,{harnessError:{message:x.message,stack:x.stack}});process.exitCode=1;});
