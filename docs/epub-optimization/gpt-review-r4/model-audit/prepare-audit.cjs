'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const scope=__dirname,root=path.dirname(scope),r3=path.resolve(root,'../gpt-review-r3/model-audit');
const hash=f=>crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
function walk(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(dir,e.name)):[path.join(dir,e.name)]);}
const dirs=['candidate','submitted-report','submitted-evidence','host-snapshot'].map(x=>path.join(root,x));
const inputs=[...dirs.flatMap(walk),...walk(r3),path.join(root,'identity.json'),path.resolve(root,'../GPT验收结论-R3暨DeepSeek返工任务书-R4.md')];
const snapshot={createdAt:new Date().toISOString(),files:inputs.map(file=>({file,size:fs.statSync(file).size,sha256:hash(file)}))};
fs.writeFileSync(path.join(scope,'frozen-before.json'),JSON.stringify(snapshot,null,2));
const comparisons=['audit-models.cjs','audit-reader-flow.cjs','audit-common.cjs'].map(name=>({name,r3Sha256:hash(path.join(r3,name)),submittedSha256:hash(path.join(root,'submitted-evidence/model',name)),byteIdentical:fs.readFileSync(path.join(r3,name)).equals(fs.readFileSync(path.join(root,'submitted-evidence/model',name)))}));
fs.writeFileSync(path.join(scope,'assertion-comparison.json'),JSON.stringify({comparisons,submittedMissingR3BoundaryScript:!fs.existsSync(path.join(root,'submitted-evidence/model/audit-boundaries.cjs')),note:'audit-models and audit-reader-flow exact byte equality proves no changed/deleted assertions in these two supplied scripts. Common helper changes source/output paths; copied R3 identity constants are stale.'},null,2));
for(const f of ['audit-common.cjs','audit-models.cjs','audit-reader-flow.cjs','audit-boundaries.cjs']){
 let text=fs.readFileSync(path.join(r3,f),'utf8');
 if(f==='audit-common.cjs')text=text.replaceAll('ba6f763f9579a808ddc201b82b5867412265c104','04f6ccb80546d6d04fe3c21f4a08775d83bfc0c6').replaceAll('847d8047702e8de6a8dec8b7ae9e3fb60693686400b71aa65c2b134a7270636c','b128b9dfbe9d7a1085cc3505f9b3edc645847cfa24da33958eb26edd685f184b').replace('Independent R3 audit','Independent R4 audit');
 fs.writeFileSync(path.join(scope,f),text);
}
console.log(JSON.stringify({inputs:inputs.length,comparisons},null,2));
