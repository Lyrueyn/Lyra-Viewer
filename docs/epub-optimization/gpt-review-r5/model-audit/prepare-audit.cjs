'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const scope=__dirname,root=path.dirname(scope),old=path.resolve(root,'../gpt-review-r4/model-audit');
const hash=f=>crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
function walk(d){return fs.readdirSync(d,{withFileTypes:true}).flatMap(x=>x.isDirectory()?walk(path.join(d,x.name)):[path.join(d,x.name)]);}
const inputs=[...['candidate','host-snapshot','submitted-evidence','submitted-report'].flatMap(x=>walk(path.join(root,x))),...walk(old),path.join(root,'identity.json'),path.resolve(root,'../GPT验收结论-R4暨DeepSeek返工任务书-R5.md')];
function save(name,data){const dest=path.resolve(scope,name);if(!dest.startsWith(scope+path.sep))throw Error('Write scope violation');if(fs.existsSync(dest))throw Error('Refuse overwrite during preparation: '+dest);fs.writeFileSync(dest,typeof data==='string'?data:JSON.stringify(data,null,2));}
save('frozen-before.json',{createdAt:new Date().toISOString(),files:inputs.map(file=>({file,size:fs.statSync(file).size,sha256:hash(file)}))});
const names=['audit-common.cjs','audit-models.cjs','audit-reader-flow.cjs','audit-boundaries.cjs','audit-r4-boundaries.cjs','audit-r4-reader-stages.cjs','audit-r4-anchor-cancel.cjs'];
const copies=[];for(const name of names){let text=fs.readFileSync(path.join(old,name),'utf8');if(name==='audit-common.cjs')text=text.replaceAll('04f6ccb80546d6d04fe3c21f4a08775d83bfc0c6','76ced4c17efa4526fbc8c00e3fff5261959a7fe7').replaceAll('b128b9dfbe9d7a1085cc3505f9b3edc645847cfa24da33958eb26edd685f184b','60cc97bf2721bb0239122275b307833d871548c985a56068e649f3d3a1517b80').replace('Independent R4 audit','Independent R5 audit');save(name,text);copies.push({name,source:path.join(old,name),oldHash:hash(path.join(old,name)),newHash:hash(path.join(scope,name)),byteIdentical:fs.readFileSync(path.join(old,name)).equals(fs.readFileSync(path.join(scope,name)))});}
save('write-path-review.json',{scope,allowedWrites:'Only this directory, including scripts, reports, JSON, console logs; all source roots read-only.',scriptAudit:'All seven legacy scripts read. Candidate resolves ../candidate. Results use __dirname. No old absolute output paths. Only common identity constants updated.',inputs:inputs.length,copies});
console.log(JSON.stringify({inputs:inputs.length,copies},null,2));
