/* Additional original-reader lifecycle checks with a controlled paginator layout dependency.
 * Real reader events, parser/search, JSZip; no browser layout claims. Output stays in this directory.
 */
'use strict';
const fs=require('node:fs'),path=require('node:path');
const {JSDOM}=require('C:/Users/Administrator/deepseek-harness/node_modules/.pnpm/jsdom@29.1.1_@noble+hashes@2.3.0/node_modules/jsdom');
const candidate=path.resolve(__dirname,'../candidate');const JSZip=require(path.join(candidate,'lib/jszip.min.js'));
const wait=ms=>new Promise(r=>setTimeout(r,ms));const results=[];
const dom=new JSDOM(fs.readFileSync(path.join(candidate,'index.html'),'utf8'),{url:'https://audit.invalid/index.html',runScripts:'outside-only',pretendToBeVisual:true});
const w=dom.window;w.JSZip=JSZip;w.__LYRA_TEST_MODE__=true;
for(const file of ['epub-book.js','search.js','paginator.js'])w.eval(fs.readFileSync(path.join(candidate,file),'utf8'));
// Geometry and iframe loading are not part of these checks. Capture all real navigation requests.
const navigation=[];const proto=w.LyraPaginator.Paginator.prototype;
proto.setSection=async function(prepared){navigation.push({index:prepared.index,path:prepared.path,html:prepared.html});this.doc=w.document;this.content=w.document.createElement('div');this.content.innerHTML=prepared.html;this.pageCount=1;this.pageIndex=0;this.ready=true;this.invalidateTextModel();};
proto.captureAnchor=()=>({textOffset:0,quote:'',chapterFraction:0});
proto.pageForRange=()=>0;
proto.highlightRanges=function(ranges){this.auditHighlightText=ranges.map(r=>r.toString());};
// Keep original clearHighlights and rangeAtTextOffset. Suppress unrelated persistence only.
w.LyraReaderState={ReaderStateClient:class{constructor(){this.sessionId='audit';this.isPersistent=false;}appReady(){}hello(){return new Promise(()=>{});}async load(){return{};}migrateLegacy(){return{};}async listBookmarks(){return{bookmarks:[],total:0};}bookBegin(){}bookReady(){}bookError(){}async savePreferences(){}async savePosition(){}}};
w.eval(fs.readFileSync(path.join(candidate,'reader.js'),'utf8'));
async function pack(title,text){const zip=new JSZip();zip.file('META-INF/container.xml','<container><rootfile full-path="OPS/package.opf"/></container>');zip.file('OPS/package.opf',`<package><metadata><title>${title}</title></metadata><manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c"/></spine></package>`);zip.file('OPS/c.xhtml',`<html><body><p>${text}</p></body></html>`);return zip.generateAsync({type:'nodebuffer'});}
async function open(title,text){const buffer=await pack(title,text);const file={name:title+'.epub',type:'application/epub+zip',arrayBuffer:async()=>buffer};const input=w.document.getElementById('file');Object.defineProperty(input,'files',{configurable:true,value:[file]});input.dispatchEvent(new w.Event('change',{bubbles:true}));for(let i=0;i<100;i++){if(w.__lyraDebug.state.ready&&w.__lyraDebug.state.fileName===file.name)return;await wait(10);}throw Error('controlled open did not complete '+title);}
function query(value){const input=w.document.getElementById('searchInput');input.value=value;input.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));}
async function main(){
await open('OLD','OLDONLY');w.document.getElementById('findBtn').click();query('OLDONLY');await wait(30);const before={book:w.__lyraDebug.state.book.title,results:w.document.querySelectorAll('.search-result').length};
await open('FRESH','NEWNEWNEWNEW');w.document.getElementById('findBtn').click();const retained={book:w.__lyraDebug.state.book.title,query:w.document.getElementById('searchInput').value,results:w.document.querySelectorAll('.search-result').length,text:w.document.getElementById('searchResults').textContent};
w.document.querySelector('.search-result')?.click();await wait(40);results.push({id:'M19',verdict:retained.results?'FAIL':'PASS',title:'Old book result buttons survive actual open-file switch and operate on new book',detail:{before,retained,highlightedInNewBook:w.__lyraDebug.paginator.auditHighlightText,status:w.document.getElementById('statusText').textContent,navigation:navigation.map(x=>({...x})),boundary:'Original reader openFile/openBookBuffer/search/click code; only paginator iframe/layout and state storage dependencies replaced. No pixel assertion.'}});
// Same original UI, deliberately controlled text-index timing to observe change-input invalidation.
await open('PENDING','OLDQUERY NEWQUERY');let release;let began;const begun=new Promise(r=>began=r);const gate=new Promise(r=>release=r);const originalPlain=w.__lyraDebug.state.book.getSectionPlainText.bind(w.__lyraDebug.state.book);w.__lyraDebug.state.book.getSectionPlainText=async index=>{began();await gate;return originalPlain(index);};w.document.getElementById('findBtn').click();query('OLDQUERY');await begun;const inp=w.document.getElementById('searchInput');inp.value='NEWQUERY';inp.dispatchEvent(new w.Event('input',{bubbles:true}));release();await wait(40);w.document.querySelector('.search-result')?.click();await wait(40);results.push({id:'M20',verdict:w.__lyraDebug.paginator.auditHighlightText?.includes('OLDQUERY')?'FAIL':'PASS',title:'Changing input does not invalidate previous query until debounce runs',detail:{input:inp.value,searchInfo:w.document.getElementById('searchInfo').textContent,results:Array.from(w.document.querySelectorAll('.search-result')).map(x=>x.textContent),highlighted:w.__lyraDebug.paginator.auditHighlightText,queryIsPendingDebounce:true}});
dom.window.close();fs.writeFileSync(path.join(__dirname,'reader-flow-results.json'),JSON.stringify({generatedAt:new Date().toISOString(),candidate,evidenceKind:'Original reader event/lifecycle execution with controlled paginator layout and state client; no browser.',results},null,2));results.forEach(x=>console.log(JSON.stringify(x)));}
main().catch(e=>{console.error(e);dom.window.close();process.exitCode=1;});
