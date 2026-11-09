'use strict';
const fs=require('node:fs'),path=require('node:path'),cp=require('node:child_process');
const scripts=['audit-models.cjs','audit-reader-flow.cjs','audit-boundaries.cjs','audit-r4-boundaries.cjs','audit-r4-reader-stages.cjs','audit-r4-anchor-cancel.cjs'];
const results=[];for(const script of scripts){const log=path.join(__dirname,script+'.log'),fd=fs.openSync(log,'w');try{const r=cp.spawnSync(process.execPath,[path.join(__dirname,script)],{cwd:__dirname,stdio:['ignore',fd,fd]});results.push({script,status:r.status,signal:r.signal,error:r.error?.message,log});console.log(script+' exit '+r.status);}finally{fs.closeSync(fd);}}
fs.writeFileSync(path.join(__dirname,'regression-runs.json'),JSON.stringify(results,null,2));
