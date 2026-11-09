/* R6 local format unknown-version protection, isolated in-memory localStorage. */
"use strict";
const fs=require('node:fs'), vm=require('node:vm'), path=require('node:path');
const repo='D:/Temp/Lyra-Extension-Epub';
const store=new Map();
const localStorage={
  getItem:k=>store.has(k)?store.get(k):null,
  setItem:(k,v)=>store.set(k,String(v)),
  removeItem:k=>store.delete(k),
};
const ctx={console,TextEncoder,TextDecoder,setTimeout,clearTimeout,Promise,JSON,Date,Math,Object,Array,String,Number,Boolean,RegExp,Error,Map,Set,crypto:{randomUUID:()=> '00000000-0000-4000-8000-'+String(Math.random()).slice(2,14)},localStorage};
ctx.window=ctx;ctx.globalThis=ctx;
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(path.join(repo,'reader-state.js'),'utf8'),ctx,{filename:'reader-state.js'});
const C=ctx.window.LyraReaderState.ReaderStateClient;
const book='sha256:'+('a'.repeat(64));
const bkKey='lyra_epub_bookmarks_'+book;
const posKey='lyra_epub_state_'+book;
const results={};
(async()=>{
  // unknown bookmark version must be protected, not treated as empty healthy
  store.set(bkKey,JSON.stringify({version:999,revision:7,items:[{id:'keep'}],futureData:'retain'}));
  let raw0=store.get(bkKey);
  const c=new C({sessionId:'s'}); let loaded=await c.load(book);
  let corruptWarn=loaded.warnings.some(w=>/版本未知/.test(w));
  let addErr=null; try{await c.addBookmark(book,{id:'new'});}catch(e){addErr=e;}
  results.bookmarkUnknown={warn:corruptWarn,addCode:addErr&&addErr.code,rawUnchanged:store.get(bkKey)===raw0};
  // legal version1 recovers, preserves futureData on add
  store.set(bkKey,JSON.stringify({version:1,revision:7,items:[{id:'keep'}],futureData:'retain'}));
  const c2=new C({sessionId:'s'}); loaded=await c2.load(book);
  await c2.addBookmark(book,{id:'new',summary:'s',locator:{textOffset:1}});
  const saved=JSON.parse(store.get(bkKey));
  results.bookmarkRecover={items:loaded.bookmarkCount,after:saved.items.length,version:saved.version,futureData:saved.futureData};
  // unknown position version protected
  store.set(posKey,JSON.stringify({version:999,revision:3,position:{spineIndex:9},futureData:'keep'})); raw0=store.get(posKey);
  const c3=new C({sessionId:'s'}); loaded=await c3.load(book);
  let posErr=null; try{await c3.savePosition(book,{spineIndex:1},1,1,'op');}catch(e){posErr=e;}
  results.positionUnknown={warn:loaded.warnings.some(w=>/版本未知/.test(w)),saveCode:posErr&&posErr.code,rawUnchanged:store.get(posKey)===raw0};
  // unknown prefs version protected
  store.set('lyra_epub_prefs',JSON.stringify({version:999,preferences:{fontSize:99},futureData:'keep'})); const praw=store.get('lyra_epub_prefs');
  const c4=new C({sessionId:'s'}); loaded=await c4.load(book);
  let prefErr=null; try{await c4.savePreferences({fontSize:22},{fontSize:'local:1'});}catch(e){prefErr=e;}
  results.prefsUnknown={warn:loaded.warnings.some(w=>/版本未知/.test(w)),saveCode:prefErr&&prefErr.code,rawUnchanged:store.get('lyra_epub_prefs')===praw};
  fs.writeFileSync('F:/DSH/LocalMediaPlayer-Workspace/docs/epub-optimization/r6-evidence/local-format/unknown-version-results.json',JSON.stringify(results,null,2));
  console.log(JSON.stringify(results));
})().catch(e=>{console.error(e);process.exitCode=1});
