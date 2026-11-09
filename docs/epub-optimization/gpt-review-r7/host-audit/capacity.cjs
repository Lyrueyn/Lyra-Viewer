'use strict';
const fs=require('fs'),path=require('path');const mode=process.argv[2]||'ack';const dst=process.argv[2]=(process.env.AUDIT_RUN||path.join(__dirname,'runs/capacity-'+mode));if(!path.resolve(dst).startsWith(__dirname+path.sep)||fs.existsSync(path.join(dst,'results.json')))throw Error('Fresh scoped run required');fs.mkdirSync(path.join(dst,'bin'),{recursive:true});for(const n of['HostAudit.dll','HostAudit.deps.json','HostAudit.runtimeconfig.json'])fs.copyFileSync(path.join(__dirname,'runs/main/bin',n),path.join(dst,'bin',n));
const h=require('./harness.cjs'),{run,wire,results,rpc,record,settle,hosted,prefs,closing,child}=h;
(async()=>{const e=await hosted('capacity');
 if(mode==='ack'){
  for(let i=0;i<4095;i++){const p=e.state.client.grantPreferences(['fontSize'],'prefill-'+i);await settle();await p;}
  e.flags.holdPrefs=true;e.input('fontRange',28);await settle();await e.tick(300);const committed=await prefs();
  e.input('lineRange',2.2);await settle();const count=await rpc({op:'grantCount'});
  e.flags.holdPrefs=false;await e.tick(3000);await closing(e);
  record('C01-ACK-LOSS-AFTER-CAPACITY-PRUNE',!Object.keys(e.state.pendingPrefs).length&&(await prefs()).preferences.lineHeight===2.2,{committed,count,file:await prefs(),pending:Object.keys(e.state.pendingPrefs),acks:e.acks(),requests:e.prefs(),errors:wire.filter(x=>x.direction==='C#->JS'&&x.raw.includes('MISSING_GRANT')),fixture:'4095 uncompleted grants; final font28 really commits but ack held; line grant triggers completed-token prune; exact JS timeout retries original font token'});
 }else{
  for(let i=0;i<4097;i++)e.input('fontRange',i%2?32:18);await settle();await e.tick(300);await closing(e);
  record('C02-4097-UNCOMPLETED-UI-INPUTS',!Object.keys(e.state.pendingPrefs).length,{inputCount:4097,count:await rpc({op:'grantCount'}),file:await prefs(),pending:Object.keys(e.state.pendingPrefs),acks:e.acks(),saveState:e.el('saveStateText').textContent,errors:wire.filter(x=>x.direction==='C#->JS'&&x.raw.includes('GRANT_LIMIT')),fixture:'Stress boundary: no 300ms debounce delivery during 4097 alternating input events; not ordinary human rate'});
 }
 fs.writeFileSync(path.join(run,'results.json'),JSON.stringify({evidenceKind:'Original JS/C# serializers and isolated disk; deliberate grant capacity stress',results},null,2));fs.writeFileSync(path.join(run,'wire.json'),JSON.stringify(wire,null,2));child.stdin.end();
})().catch(e=>{console.error(e);child.stdin.end();process.exitCode=1;});
