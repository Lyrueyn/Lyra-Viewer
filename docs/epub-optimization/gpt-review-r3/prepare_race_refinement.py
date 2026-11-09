from pathlib import Path
root=Path(__file__).parent
s=(root/'review-races.cjs').read_text(encoding='utf-8')
s=s.replace("out=path.join(root,'race-evidence')","out=path.join(root,'race-evidence-v2')")
s=s.replace("async function test(id,fn){const c=", "async function test(id,fn){if(!id.startsWith('C02_')&&!id.startsWith('C03_'))return;const c=")
s=s.replace("await go(p,5);await p.waitForTimeout(160);", "await go(p,5);await p.waitForTimeout(650);")
s=s.replace("await go(p,3);const atRelease=await p.evaluate(()=>{const a=__lyraDebug.paginator.captureAnchor();window.__release();return a;});", "const atRelease=await p.evaluate(async()=>{const input=document.getElementById('pageInput');input.value='3';input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));await Promise.resolve();const a=__lyraDebug.paginator.captureAnchor();window.__release();return a;});")
p=root/'review-races-v2.cjs';assert not p.exists();p.write_text(s,encoding='utf-8')
print('Refined C02 waits out the old input debounce; C03 sends native DOM Enter and releases old ack in one controlled turn.')
