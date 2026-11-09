/* Replay implementation's submitted model assertions without writing to frozen inputs.
 * Only three mechanical substitutions: candidate source path, mkdir destination, JSON output path.
 * The exact transformed script is saved beside this driver for review. No browser or dependency install.
 */
'use strict';
const fs=require('node:fs'),path=require('node:path');
const source=path.resolve(__dirname,'../candidate/tests/r3-model-checks.cjs');
let text=fs.readFileSync(source,'utf8');
const replacements=[
 ['const candidate = path.resolve("D:/Temp/Lyra-Extension-Epub");','const candidate = path.resolve(__dirname, "../candidate");'],
 ['fs.mkdirSync(path.join(candidate, "tests", "artifacts"), { recursive: true });','fs.mkdirSync(__dirname, { recursive: true });'],
 ['path.join(candidate, "tests", "artifacts", "r3-model-results.json")','path.join(__dirname, "submitted-model-rerun.json")'],
];
for(const [oldText,newText]of replacements){if(text.split(oldText).length!==2)throw Error('Expected exactly one substitution: '+oldText);text=text.replace(oldText,newText);}
const dest=path.join(__dirname,'submitted-model-adapted.cjs');
fs.writeFileSync(dest,text);
require(dest);
