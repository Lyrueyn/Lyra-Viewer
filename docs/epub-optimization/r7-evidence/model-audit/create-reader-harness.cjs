'use strict';
const fs=require('node:fs'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'audit-r4-reader-stages.cjs'),'utf8');
let helper=source.slice(0,source.indexOf('async function caseNavigation'));
helper=helper.replace('const ledger=[],hostMessages=[],hooks={resource:null};','const ledger=[],hostMessages=[],saveCalls=[],hooks={resource:null};');
helper=helper.replace('async savePosition(){this.lastRevision=++revision;return{revision};}', 'async savePosition(bookId,pos,token,sequence,operationId){saveCalls.push(JSON.parse(JSON.stringify({bookId,pos,token,sequence,operationId})));this.lastRevision=++revision;return{revision,positionRevision:revision};}');
helper=helper.replace('ledger,hooks,hostMessages,hostListeners,push','ledger,hooks,saveCalls,hostMessages,hostListeners,push');
helper+='\nmodule.exports={setup,isolated};\n';
fs.writeFileSync(path.join(__dirname,'reader-harness.cjs'),helper);
