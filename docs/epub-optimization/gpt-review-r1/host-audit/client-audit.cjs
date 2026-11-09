// Original reader-state.js in Node VM with synthetic WebView and in-memory localStorage.
// No browser, desktop, network or user state. Results only in this audit folder.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const source = fs.readFileSync('D:/Temp/Lyra-Extension-Epub/reader-state.js', 'utf8');
const results = [];
function realm({throwWrites=false, handler=null}={}) {
 const data=new Map(), sent=[], statuses=[]; let receive;
 const window={crypto};
 if(handler)window.chrome={webview:{addEventListener(type,cb){receive=cb;},postMessage(msg){sent.push(msg);queueMicrotask(()=>{const response=handler(msg);if(response)receive({data:response});});}}};
 const localStorage={getItem(k){return data.get(k)??null;},setItem(k,v){if(throwWrites)throw new Error('Synthetic QuotaExceededError');data.set(k,String(v));}};
 const context={window,localStorage,crypto,console,URLSearchParams,setTimeout,clearTimeout};
 vm.runInNewContext(source,context,{filename:'reader-state.js'});
 const c=new window.LyraReaderState.ReaderStateClient({sessionId:'audit-session',onStatus:s=>statuses.push(s)});
 return {c,data,sent,statuses};
}
function record(id,defectObserved,details){const row={id,defectObserved,details};results.push(row);console.log(JSON.stringify(row));}
(async()=>{
 const book='sha256:'+'a'.repeat(64);
 const r=realm();
 for(let i=0;i<51;i++)await r.c.addBookmark(book,{id:'bm-'+i,summary:'synthetic'});
 const p1=await r.c.listBookmarks(book,null,50),p2=await r.c.listBookmarks(book,p1.nextCursor,50);
 record('J-LOCAL-CURSOR',p2.bookmarks[0].id===p1.bookmarks[0].id&&p2.nextCursor===p1.nextCursor,{firstPage:{first:p1.bookmarks[0].id,last:p1.bookmarks.at(-1).id,nextCursor:p1.nextCursor,total:p1.total},secondPage:{first:p2.bookmarks[0].id,last:p2.bookmarks.at(-1).id,nextCursor:p2.nextCursor,total:p2.total}});
 for(let i=51;i<501;i++)await r.c.addBookmark(book,{id:'bm-'+i,summary:'synthetic'});
 const persisted=JSON.parse([...r.data.entries()].find(([k])=>k.includes('bookmarks'))[1]);
 record('J-LOCAL-CAP',persisted.length===500&&persisted[0].id==='bm-1',{all501AddsResolved:true,persistedCount:persisted.length,first:persisted[0].id,last:persisted.at(-1).id});
 const failed=realm({throwWrites:true});
 const save=await failed.c.savePosition(book,{spineHref:'s.xhtml',locator:{quote:'unsaved'}});
 const prefs=await failed.c.savePreferences({fontSize:28});
 const bookmark=await failed.c.addBookmark(book,{id:'unsaved-bookmark',summary:'synthetic'});
 const restored=await failed.c.load(book);
 record('J-LOCAL-WRITE-FAIL',save.revision===1&&restored.position===null&&failed.statuses.length===0,{save,prefs,bookmark,restored,statuses:failed.statuses});
 const host=realm({handler(msg){return {protocol:'lyra-reader',version:1,type:'response',requestId:msg.requestId,sessionId:msg.sessionId,...(msg.type==='hello'?{ok:true,result:{capabilities:{persistentState:true}}}:{ok:false,error:{code:'HOST_ERROR',message:'synthetic disk failure'}})};}});
 await host.c.hello();const fallback=await host.c.load(book);let saveError;try{await host.c.savePosition(book,{spineHref:'s.xhtml'},1);}catch(e){saveError={code:e.code,message:e.message};}
 record('J-READ-FALLBACK-MODE',fallback.mode==='local'&&host.c.mode==='host'&&saveError?.code==='HOST_ERROR',{reportedLoadMode:fallback.mode,clientMode:host.c.mode,saveError,sentTypes:host.sent.map(s=>s.type),statuses:host.statuses,localStorageEntryCount:host.data.size});
 const bad=realm({handler(msg){return {protocol:'other-protocol',version:99,type:'not-a-response',requestId:msg.requestId,sessionId:'old-session',ok:true,result:{capabilities:{persistentState:true}}};}});
 await bad.c.hello();const accepted=await bad.c.savePreferences({fontSize:28});
 record('J-RESPONSE-VALIDATION',bad.c.mode==='host',{mode:bad.c.mode,accepted,syntheticResponse:{protocol:'other-protocol',version:99,type:'not-a-response',sessionId:'old-session'},note:'Wrong envelope with matching requestId is accepted; not a claim that random request IDs collide in practice'});
 fs.writeFileSync(path.join(__dirname,'client-results.json'),JSON.stringify({generatedAt:new Date().toISOString(),evidenceKind:'Original JavaScript in isolated Node VM; no DOM/browser',results},null,2));
})().catch(e=>{console.error(e);process.exitCode=1;});
