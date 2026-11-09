'use strict';
const fs=require('fs'),path=require('path'),crypto=require('crypto');
const scope=path.resolve(__dirname),root=path.resolve(scope,'..'),old=path.resolve(root,'../gpt-review-r4/host-audit');
if(!scope.endsWith(path.join('gpt-review-r5','host-audit')))throw Error('Wrong scope');
if(fs.existsSync(path.join(scope,'inputs-before.json')))throw Error('Already prepared');
const names=['HostAudit.csproj','UiStubs.cs','Program.cs','harness.cjs','audit.cjs','supplement.cjs','capacity.cjs','boundary.cjs','regression.cjs'];
const inputs=[];function walk(p){for(const e of fs.readdirSync(p,{withFileTypes:true})){const q=path.join(p,e.name);if(e.isDirectory())walk(q);else inputs.push(q);}}
for(const d of ['candidate','host-snapshot','submitted-evidence','submitted-report'])walk(path.join(root,d));
inputs.push(path.join(root,'identity.json'),path.resolve(root,'../GPT验收结论-R4暨DeepSeek返工任务书-R5.md'),...names.map(n=>path.join(old,n)),path.join(old,'宿主状态通信独立验收-R4.md'),path.resolve(root,'../gpt-review-r3/candidate/reader-state.js'));
fs.writeFileSync(path.join(scope,'inputs-before.json'),JSON.stringify(inputs.map(p=>({path:p,sha256:crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')})),null,2));
for(const n of names){let s=fs.readFileSync(path.join(old,n),'utf8').replaceAll('gpt-review-r4','gpt-review-r5').replaceAll('Independent R4','Independent R5').replaceAll('R4 audit','R5 audit').replaceAll('version:2','version:3').replaceAll('protocolVersion:2','protocolVersion:3');
if(n==='harness.cjs')s=s.replace('s.ready=true;s.pageCount=20;return b;','s.ready=true;s.pageCount=20;sandbox.__lyraDebug.paginator.doc={};return b;');
if(n==='audit.cjs'){
s=s.replace("code(conflict)==='PREFERENCE_CONFLICT'","conflict?.ok===true&&conflict.result.applied.includes('fontSize')");
s=s.replace("code(save)==='PREFERENCE_STALE'","save?.ok===true&&save.result.superseded.includes('fontSize')");
s=s.replace('!e.state.modified&&e.state.pageIndex===0','!e.state.modified&&e.state.pageIndex===8&&(await disk(b)).position?.locator?.textOffset===800');
}
if(n==='boundary.cjs')s=s.replace("code(older)==='PREFERENCE_STALE'","older.ok&&older.result.superseded.includes('fontSize')");
fs.writeFileSync(path.join(scope,n),s);
}
fs.writeFileSync(path.join(scope,'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>');
const scan=[];for(const n of names){const text=fs.readFileSync(path.join(scope,n),'utf8');scan.push({file:n,absolutePaths:[...text.matchAll(/[A-Za-z]:[\\/][^\r\n\"'<>]+/g)].map(m=>m[0]),oldReviewLiteral:text.includes('gpt-review-r4'),compileLink:n==='HostAudit.csproj'?text:null});}
fs.writeFileSync(path.join(scope,'path-audit.json'),JSON.stringify({scope,readOnlySources:{root,old},outputPolicy:'All outputs scoped beneath __dirname; fresh run guards; original C# Compile Link relative ../host-snapshot is R5',scan},null,2));
console.log(JSON.stringify({inputs:inputs.length,copied:names.length,pathAudit:scan}));
