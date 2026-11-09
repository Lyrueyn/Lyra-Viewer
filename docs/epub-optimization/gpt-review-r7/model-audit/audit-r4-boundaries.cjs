/* R4 independent additions: all business modules loaded in full, unchanged. */
'use strict';
const {environment,fakeBook,packedBook,deferred,wait,output,recorder}=require('./audit-common.cjs');
const results=[],record=recorder(results);
const escape=s=>s.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
function reference(text,q){if(q.length>65536&&q===text)return[{offset:0,length:q.length}];const re=new RegExp(q.trim().split(/\s+/).map(escape).join('\\s+'),'gi');return [...text.matchAll(re)].map(m=>({offset:m.index,length:m[0].length}));}
async function main(){const e=environment(),{w,Book,Search,Paginator,revoked}=e;try{
 const q='NEEDLE',edge=65536+q.length*4+256;
 const boundary=[];
 for(const offset of [65533,edge-q.length,edge-q.length+1,edge-1,edge,2*edge-2]){
  const text='x'.repeat(offset)+q+'z'.repeat(16),r=await new Search(fakeBook([text])).search(q);boundary.push({offset,edge,expected:reference(text,q),actual:r.results.map(({offset,length})=>({offset,length})),total:r.total,truncated:r.truncated});
 }
 record('X01',boundary.every(x=>JSON.stringify(x.actual)===JSON.stringify(x.expected)),'True chunk-boundary matches must not disappear',boundary);
 const variants=[
  {name:'emoji split surrogate',q:'😀Z',text:'x'.repeat(65536+268-1)+'😀Ztail'},
  {name:'whitespace span exceeds nominal overlap',q:'left right',text:'x'.repeat(65000)+'left'+' '.repeat(2000)+'right'},
  {name:'query exceeds complete 64KiB+8192 window',q:'a'.repeat(74000)+'Z',text:'a'.repeat(74000)+'Z'},
  {name:'cross-window repetition count',q:'aaa',text:'a'.repeat((65536+268)*2+3)},
  {name:'expected nonoverlapping within one window',q:'aba',text:'ababa aba'},
  {name:'ordinary case fold offsets',q:'needle',text:'İx NEedLE ß ẞ 😀 NEEDLE'},
  {name:'simple ECMAScript fold sigma',q:'σ',text:'Σ σ ς'},
 ];
 const v=[];for(const c of variants){const expected=reference(c.text,c.q);let r,error;try{r=await new Search(fakeBook([c.text])).search(c.q);}catch(x){error=x.message.slice(-160);}v.push({name:c.name,queryUnits:c.q.length,textUnits:c.text.length,expectedTotal:expected.length,actualTotal:r?.total,expectedHead:expected.slice(0,3),actualHead:r?.results.slice(0,3).map(({offset,length})=>({offset,length})),expectedTail:expected.slice(-3),truncated:r?.truncated,error,pass:!error&&r.total===expected.length&&JSON.stringify(r.results.map(({offset,length})=>({offset,length})))===JSON.stringify(expected.slice(0,1000))});}
 record('X02',[v[0],v[1],v[3]].every(x=>x.pass),'Emoji, whitespace and repeated count at chunk boundaries',[v[0],v[1],v[3]]);
 record('X11','OBSERVATION','Query longer than chunk meets engine regexp-size limit',{...v[2],note:'74001 UTF16 literal query rejects with native Regular expression too large. This is a query-length boundary, not proof of chunk-only loss; UI behavior untested here.'});
 record('X03',v.slice(4).every(x=>x.pass),'Nonoverlap and case-insensitive UTF16 offsets retain original regexp semantics',v.slice(4));
 // Dense-but-spaced 1001 case: exactly one needle crosses the real boundary.
 const prefix='NEEDLE '.repeat(999),text=prefix+'x'.repeat(edge-3-prefix.length)+'NEEDLE'+' x NEEDLE';
 const r=await new Search(fakeBook([text])).search('NEEDLE');record('X04',r.total===1001&&r.results.length===1000&&r.truncated,'Boundary loss must not make 1001 look like exactly 1000',{expected:reference(text,'NEEDLE').length,total:r.total,retained:r.results.length,truncated:r.truncated,last:r.results.slice(-2).map(x=>({offset:x.offset,length:x.length}))});
 // Exact minimal reproduction through real ZIP, parser and original search.
 const b=await Book.open(await packedBook({chapters:['<p>'+'x'.repeat(edge-4)+'NEEDLE</p>']}),'boundary.epub');const plain=await b.getSectionPlainText(0),zipResult=await new Search(b).search('NEEDLE');record('X05',zipResult.total===1,'Real ZIP parser crossing actual scan boundary',{edge,expectedOffset:plain.indexOf('NEEDLE'),total:zipResult.total,scanned:zipResult.scannedSections,truncated:zipResult.truncated});b.destroy();
 // Compare cold searchable text with actual sanitized display model, no CSS geometry claim.
 const styleCases=[['HTML inline display','<p>START <span style="display:none">HIDDEN</span> TARGET</p>'],['hidden br inline','<p>A<br style="display:none">B</p>'],['SVG inline visibility','<svg><text style="visibility:hidden">HIDDEN</text><text>TARGET</text></svg>']];
 const models=[];for(const[name,html]of styleCases){const book=await Book.open(await packedBook({chapters:[html]}),name+'.epub'),prep=await book.prepareSection(0),root=w.document.createElement('div');root.innerHTML=prep.html;const cold=await book.getSectionPlainText(0),display=w.LyraEpubBook.buildTextModel(root).text;const p=new Paginator({frame:w.document.createElement('iframe')});p.doc=w.document;p.content=root;const found=await new Search(book).search('TARGET');models.push({name,cold,display,prepared:prep.html,equal:cold===display,searchOffset:found.results[0]?.offset,range:found.results[0]?p.rangeAtTextOffset(found.results[0].offset,found.results[0].length).toString():null});p.destroy();book.destroy();}
 record('X06',models.every(x=>x.equal),'Search and display share text after publisher style removal',models);
 const svg='<svg><title>SVG_TITLE</title><defs><text id="t">USE_TEMPLATE</text></defs><symbol id="s"><text>SYMBOL_TEMPLATE</text></symbol><use href="#t"/><g visibility="hidden"><text visibility="visible">OVERRIDE_VISIBLE</text></g><text aria-hidden="true">ARIA_VISIBLE</text><text>DIRECT_VISIBLE</text></svg>';
 const sb=await Book.open(await packedBook({chapters:[svg]}),'svg.epub'),sp=await sb.prepareSection(0),st=await sb.getSectionPlainText(0);record('X07','OBSERVATION','SVG use and child visibility override policy boundary',{plain:st,preparedHtml:sp.html,titleExcluded:!st.includes('SVG_TITLE'),symbolExcluded:!st.includes('SYMBOL_TEMPLATE'),useTemplateExcluded:!st.includes('USE_TEMPLATE'),visibleOverrideExcluded:!st.includes('OVERRIDE_VISIBLE'),ariaRetained:st.includes('ARIA_VISIBLE'),note:'use is explicitly unsupported; child visibility=visible overriding inherited hidden needs real Edge geometry. Do not equate these boundaries.'});sb.destroy();
 // Shared image already belongs to visible current chapter; prepare-next fails after acquiring shared URL.
 const rb=await Book.open(await packedBook({chapters:['<p>OLD</p><img src="shared.png">','<p>NEW</p><img src="shared.png">'],extras:{'OPS/shared.png':[1,2]}}),'shared-fail.epub'),old=await rb.prepareSection(0),url=old.html.match(/blob:[^"]+/)[0],title=rb.sectionTitle.bind(rb);rb.sectionTitle=async i=>{if(i===1)throw Error('controlled title failure after shared image');return title(i);};let fail='';try{await rb.prepareSection(1);}catch(x){fail=x.message;}record('X08',!revoked.includes(url)&&rb._resources.has('OPS/shared.png'),'Failed next prepare must keep previously displayed shared image',{failure:fail,oldOwner:old.owner,currentTask:rb._currentTaskId,url,revokedCurrent:revoked.includes(url),resources:[...rb._resources.keys()],taskOwners:[...rb._taskResources.keys()]});rb.destroy();
 // Font validation positives/negatives including exact target encoded once and mixed target.
 const alg='http://www.idpf.org/2008/embedding',enc=refs=>'<encryption>'+refs.map(uri=>`<EncryptedData><EncryptionMethod Algorithm="${alg}"/><CipherData><CipherReference URI="${uri}"/></CipherData></EncryptedData>`).join('')+'</encryption>';
 const ec=[
 {name:'encoded literal percent exists',uri:'OPS/f%2520.otf',href:'f%2520.otf',zip:'OPS/f%20.otf',mime:'font/otf',expected:true},
 {name:'encoded space exists',uri:'OPS/f%20.otf',href:'f%20.otf',zip:'OPS/f .otf',mime:'font/otf',expected:true},
 {name:'mime case with parameter',uri:'OPS/f.otf',href:'f.otf',zip:'OPS/f.otf',mime:'FONT/OTF; test=1',expected:true},
 {name:'mixed font and XHTML',uri:'OPS/f.otf',href:'f.otf',zip:'OPS/f.otf',mime:'font/otf',extra:'OPS/c0.xhtml',expected:false},
 {name:'root traversal target',uri:'../OPS/f.otf',href:'f.otf',zip:'OPS/f.otf',mime:'font/otf',expected:false},
 {name:'encoded slash target',uri:'OPS%2Ff.otf',href:'f.otf',zip:'OPS/f.otf',mime:'font/otf',expected:false},
 ];const er=[];for(const c of ec){let book,error;try{book=await Book.open(await packedBook({encryption:enc([c.uri,...(c.extra?[c.extra]:[])]),manifestExtra:`<item id="f" href="${c.href}" media-type="${c.mime}"/>`,extras:{[c.zip]:[0,1,2,3]}}),c.name+'.epub');}catch(x){error={code:x.code,message:x.message};}er.push({...c,opened:!!book,fontFlag:book?.fontObfuscated,error});book?.destroy();}
 record('X09',er.slice(0,4).every(x=>x.opened===x.expected),'Exact font MIME and encoded actual ZIP targets, mixed non-font rejection',er.slice(0,4));
 record('X10','OBSERVATION','Encryption URI fallback reinterprets root-invalid relative path',{cases:er.slice(4),note:'../OPS/f.otf fails root normalization, then OPF-directory fallback succeeds; encoded slash rejected. Same broader path-policy family as inherited E03. No filesystem escape or decryption inferred; root-only strictness needs explicit acceptance policy.'});
 }finally{e.dom.window.close();output('r4-boundary-results.json',results);}}
main().catch(x=>{console.error(x);output('r4-boundary-results.json',results,{harnessError:{message:x.message,stack:x.stack}});process.exitCode=1;});
