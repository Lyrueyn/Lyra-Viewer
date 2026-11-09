/* Read-only consistency verification. Does not run product scenarios or mutate outputs. */
'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const files=['model-results.json','reader-flow-results.json','boundary-results.json'];
const results=files.flatMap(f=>JSON.parse(fs.readFileSync(path.join(__dirname,f),'utf8')).results);
const counts=results.reduce((a,r)=>(a[r.verdict]=(a[r.verdict]||0)+1,a),{});
const identity=JSON.parse(fs.readFileSync(path.join(__dirname,'../identity.json'),'utf8'));
const hashChecks=['epub-book.js','search.js','paginator.js','reader.js'].map(f=>({file:f,match:crypto.createHash('sha256').update(fs.readFileSync(path.join(__dirname,'../candidate',f))).digest('hex')===identity.files.find(x=>x.path===f).sha256}));
const summary=JSON.parse(fs.readFileSync(path.join(__dirname,'audit-summary.json'),'utf8'));
const replay=JSON.parse(fs.readFileSync(path.join(__dirname,'submitted-model-rerun.json'),'utf8'));
const report=fs.readFileSync(path.join(__dirname,'解析搜索资源独立验收-R3.md'),'utf8');
const missingLinks=[...report.matchAll(/\]\(<([^>]+)>\)/g)].map(x=>x[1].split('#')[0]).filter(x=>!fs.existsSync(x));
const ok=results.length===34&&counts.PASS===25&&counts.FAIL===6&&counts.OBSERVATION===3&&hashChecks.every(x=>x.match)&&replay.results.length===6&&replay.results.every(x=>x.pass)&&summary.independentCounts.observations===results.length&&missingLinks.length===0;
console.log(JSON.stringify({ok,counts,hashChecks,submittedReplayPass:replay.results.filter(x=>x.pass).length,missingLinks},null,2));
if(!ok)process.exitCode=1;
