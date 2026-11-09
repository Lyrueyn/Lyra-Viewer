/* Independent R5 audit helpers. Only outputs under __dirname. No browser, network, install or product mutation. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const {JSDOM}=require(process.env.JSDOM_MODULE || 'C:/Users/Administrator/deepseek-harness/node_modules/.pnpm/jsdom@29.1.1_@noble+hashes@2.3.0/node_modules/jsdom');
const candidate=path.resolve(__dirname,'../candidate');
const JSZip=require(path.join(candidate,'lib/jszip.min.js'));
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return{promise,resolve,reject};};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function until(f,label='condition',timeout=5000){const t=Date.now();while(!f()){if(Date.now()-t>timeout)throw Error('Timed out: '+label);await wait(5);}}
function environment({reader=false}={}){
 const dom=new JSDOM(reader?fs.readFileSync(path.join(candidate,'index.html'),'utf8'):'<!doctype html><body></body>',{url:'https://audit.invalid/index.html',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,created=[],revoked=[],blobs=new Map();w.JSZip=JSZip;
 Object.defineProperty(w.crypto,'subtle',{value:crypto.webcrypto.subtle});
 w.URL.createObjectURL=blob=>{const url='blob:audit-'+(created.length+1);created.push({url,size:blob.size,type:blob.type});blobs.set(url,blob);return url;};
 w.URL.revokeObjectURL=url=>revoked.push(url);
 for(const f of ['epub-book.js','search.js','paginator.js'])w.eval(fs.readFileSync(path.join(candidate,f),'utf8'));
 return{dom,w,created,revoked,blobs,Book:w.LyraEpubBook.EpubBook,Search:w.LyraSearch.SearchEngine,Paginator:w.LyraPaginator.Paginator};
}
function fakeBook(texts){return{bookId:'audit-book',spine:texts.map((_,i)=>({index:i})),getSectionPlainText:async i=>texts[i],sectionTitle:async i=>'C'+i};}
async function packedBook({title='Audit',chapters=['<p>BODY</p>'],encryption=null,extras={},manifestExtra='',nav=null}={}){
 const z=new JSZip();z.file('META-INF/container.xml','<container><rootfile full-path="OPS/package.opf"/></container>');
 z.file('OPS/package.opf',`<package><metadata><title>${title}</title></metadata><manifest>${chapters.map((_,i)=>`<item id="c${i}" href="c${i}.xhtml" media-type="application/xhtml+xml"/>`).join('')}${manifestExtra}${nav?'<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>':''}</manifest><spine>${chapters.map((_,i)=>`<itemref idref="c${i}"/>`).join('')}</spine></package>`);
 chapters.forEach((body,i)=>z.file(`OPS/c${i}.xhtml`,`<html><body>${body}</body></html>`));
 if(encryption)z.file('META-INF/encryption.xml',encryption);
 if(nav)z.file('OPS/nav.xhtml',`<html><body><nav epub:type="toc"><ol>${nav}</ol></nav></body></html>`);
 for(const [name,bytes]of Object.entries(extras))z.file(name,bytes);
 return z.generateAsync({type:'nodebuffer'});
}
function output(name,results,extra={}){
 const hashes=Object.fromEntries(['epub-book.js','search.js','paginator.js','reader.js'].map(f=>[f,crypto.createHash('sha256').update(fs.readFileSync(path.join(candidate,f))).digest('hex')]));
 const data={generatedAt:new Date().toISOString(),candidate,identity:{expectedCommit:'76ced4c17efa4526fbc8c00e3fff5261959a7fe7',expectedPackageSha256:'60cc97bf2721bb0239122275b307833d871548c985a56068e649f3d3a1517b80',hashes},runtime:{node:process.version,jsdom:require('C:/Users/Administrator/deepseek-harness/node_modules/.pnpm/jsdom@29.1.1_@noble+hashes@2.3.0/node_modules/jsdom/package.json').version},evidenceKind:'Frozen original JavaScript loaded unmodified; jsdom DOM/Range and real JSZip. Controlled async dependencies and geometry doubles disclosed per case. Not browser visibility, SVG painting or native desktop evidence.',...extra,results};
 fs.writeFileSync(path.join(__dirname,name),JSON.stringify(data,null,2));
 console.log('SUMMARY '+results.map(x=>x.id+':'+x.verdict).join(' '));
 process.exitCode=results.some(x=>x.verdict==='FAIL')?2:0;
}
function recorder(results){return(id,pass,title,detail)=>{const r={id,verdict:typeof pass==='string'?pass:pass?'PASS':'FAIL',title,detail};results.push(r);console.log(JSON.stringify(r));};}
module.exports={fs,path,candidate,JSZip,deferred,wait,until,environment,fakeBook,packedBook,output,recorder};
