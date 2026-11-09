'use strict';
const fs=require('fs'),path=require('path'),crypto=require('crypto');
const scope=__dirname,root=path.resolve(scope,'..'),old=path.resolve(root,'../gpt-review-r5/host-audit');
if(!scope.endsWith(path.join('gpt-review-r6','host-audit')))throw Error('Wrong scope');
if(fs.existsSync(path.join(scope,'inputs-before.json')))throw Error('Already prepared');
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const files=[];function walk(p){for(const e of fs.readdirSync(p,{withFileTypes:true})){const q=path.join(p,e.name);if(e.isDirectory())walk(q);else files.push(q);}}
for(const d of ['candidate','host-snapshot','submitted-evidence','submitted-report'])walk(path.join(root,d));
walk(old);
files.push(path.join(root,'identity.json'),path.resolve(root,'../GPT验收结论-R5暨DeepSeek返工任务书-R6.md'),path.resolve(root,'../gpt-review-r3/candidate/reader-state.js'),path.resolve(root,'../gpt-review-r4/candidate/reader-state.js'));
fs.writeFileSync(path.join(scope,'inputs-before.json'),JSON.stringify(files.map(p=>({path:p,sha256:hash(p)})),null,2));
const names=['HostAudit.csproj','UiStubs.cs','Program.cs','harness.cjs','audit.cjs','supplement.cjs','capacity.cjs','boundary.cjs','regression.cjs','extended.cjs','completion-boundary.cjs','close-boundary.cjs','protocol-boundary.cjs'];
const scan=[];
for(const n of names){const before=fs.readFileSync(path.join(old,n),'utf8');let s=before.replaceAll('gpt-review-r5','gpt-review-r6').replaceAll('Independent R5','Independent R6').replaceAll('R5 audit','R6 audit').replaceAll('Frozen R5 original','Frozen R6 original').replaceAll('Original R5 business','Original R6 business').replaceAll('frozen R5 C#','frozen R6 C#');
// R6 checks paginator.ready before capturing stable positions: supply that property on geometry double only.
if(n==='harness.cjs')s=s.replace('this.doc=null;this.pageIndex=0;','this.doc=null;this.ready=true;this.pageIndex=0;');
// B03 changed requirement: capacity now recycles superseded incomplete grants. Test safe result rather than require removed GRANT_LIMIT behavior.
if(n==='boundary.cjs')s=s.replace("all&&code(blocked)==='GRANT_LIMIT'&&older.ok&&older.result.superseded.includes('fontSize')&&current.ok&&freed.ok","all&&blocked.ok&&older.ok&&older.result.superseded.includes('fontSize')&&current.ok&&freed.ok");
if(n==='protocol-boundary.cjs')s=s.replace("P01-R4-V2-CLIENT-ON-R5","P01-R4-V2-CLIENT-ON-R6").replace("!hello&&c.mode==='local'","!hello&&c.mode==='local'&&(await rpc({op:'snapshot',name:e.name})).ready").replace('new original host silently ignores v2 lifecycle','R6 must accept trusted v2 lifecycle');
fs.writeFileSync(path.join(scope,n),s);scan.push({file:n,source:path.join(old,n),sourceHash:hash(path.join(old,n)),outputHash:hash(path.join(scope,n)),absolutePaths:[...s.matchAll(/[A-Za-z]:[\\/][^\r\n\"'<>]+/g)].map(m=>m[0]),oldWriteLiteral:s.includes('gpt-review-r5')});}
fs.writeFileSync(path.join(scope,'NuGet.Config'),'<configuration><packageSources><clear /></packageSources></configuration>');
for(const d of ['.dotnet','.nuget','.temp','runs/main/bin','obj'])fs.mkdirSync(path.join(scope,d),{recursive:true});
const prior=[];for(const g of fs.readdirSync(path.join(old,'runs'))){const p=path.join(old,'runs',g,'results.json');if(!fs.existsSync(p))continue;const d=JSON.parse(fs.readFileSync(p));const w=path.join(old,'runs',g,'wire.json');prior.push({group:g,results:d.results,wireCount:fs.existsSync(w)?JSON.parse(fs.readFileSync(w)).length:0,wireSha256:fs.existsSync(w)?hash(w):null});}
fs.writeFileSync(path.join(scope,'prior-results-read.json'),JSON.stringify(prior,null,2));
fs.writeFileSync(path.join(scope,'path-audit.json'),JSON.stringify({scope,readOnlyRoots:[root,old],scan,policy:'All run paths scoped; Compile Links ../host-snapshot; no product changes; old input wire/results read and hashed; K02 overflow read-only retained, not rerun'},null,2));
console.log(JSON.stringify({inputs:files.length,copied:names.length,priorGroups:prior.map(g=>({group:g.group,cases:g.results.length,wire:g.wireCount}))}));
