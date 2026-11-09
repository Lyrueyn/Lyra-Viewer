/* Original full reader + original full paginator. Only jsdom iframe srcdoc/load, resource-complete and geometry are bridged.
 * Method wrappers observe and delegate; fault/delay injection is after original dependency/DOM execution. No business source rewriting.
 */
'use strict';
const {fs,path,candidate,environment,packedBook,deferred,wait,until,output,recorder}=require('./audit-common.cjs');
const results=[],record=recorder(results);
function setup(){
 const e=environment({reader:true}),{w}=e;w.__LYRA_TEST_MODE__=true;const ledger=[],hostMessages=[],saveCalls=[],hooks={resource:null};let state,p;
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
 let seq=0,revision=0;w.LyraReaderState={ReaderStateClient:class{constructor(){this.sessionId='audit';this.isPersistent=false;this.lastRevision=0;}appReady(){}hello(){return new Promise(()=>{});}async load(){return{};}migrateLegacy(){return{};}async listBookmarks(){return{bookmarks:[],total:0};}bookBegin(){}bookReady(){}bookError(){}nextSequence(){return ++seq;}async grantPreferences(){return{grant:'g'};}async savePreferences(p){return{applied:Object.keys(p)};}async savePosition(bookId,pos,token,sequence,operationId){saveCalls.push(JSON.parse(JSON.stringify({bookId,pos,token,sequence,operationId})));this.lastRevision=++revision;return{revision,positionRevision:revision};}}};
 const hostListeners=[];w.chrome={webview:{addEventListener:(type,fn)=>{if(type==='message')hostListeners.push(fn);},postMessage:m=>hostMessages.push(m)}};
 w.eval(fs.readFileSync(path.join(candidate,'reader.js'),'utf8'));state=w.__lyraDebug.state;p=w.__lyraDebug.paginator;
 const dispatch=(buffer,title,byteGate=null)=>{const input=w.document.getElementById('file');Object.defineProperty(input,'files',{configurable:true,value:[{name:title+'.epub',type:'application/epub+zip',arrayBuffer:async()=>{if(byteGate)await byteGate.promise;return buffer;}}]});input.dispatchEvent(new w.Event('change',{bubbles:true}));};
 const open=async(title,chapters,extras={})=>{dispatch(await packedBook({title,chapters,extras}),title);await until(()=>state.ready&&!state.busy&&state.fileName===title+'.epub','open '+title);return state.book;};
 const click=id=>w.document.getElementById(id).click(),query=(q,input=false)=>{const el=w.document.getElementById('searchInput');el.value=q;el.dispatchEvent(input?new w.Event('input',{bubbles:true}):new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));};
 const search=async q=>{click('findBtn');query(q);await until(()=>w.document.querySelector('.search-result'),'search');};
 return{...e,state,p,ledger,hooks,saveCalls,hostMessages,hostListeners,push,open,dispatch,click,query,search};
}
async function isolated(fn){const e=setup();try{await fn(e);}finally{e.state.book?.destroy();e.dom.window.close();}}

module.exports={setup,isolated};
