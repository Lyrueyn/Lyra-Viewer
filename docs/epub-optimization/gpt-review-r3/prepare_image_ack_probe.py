from pathlib import Path
root=Path(__file__).parent
s=(root/'review-races-v2.cjs').read_text(encoding='utf-8')
s=s.replace("out=path.join(root,'race-evidence-v2')", "out=path.join(root,'image-ack-evidence')")
s=s.replace("if(!id.startsWith('C02_')&&!id.startsWith('C03_'))return;", "if(!id.startsWith('C03_'))return;")
s=s.replace("await go(p,2);await p.waitForFunction(()=>window.__waiting);", "await p.locator('#pageNext').click();await p.waitForFunction(()=>window.__waiting);")
s=s.replace("const input=document.getElementById('pageInput');input.value='3';input.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true}));", "document.getElementById('pageNext').click();")
s=s.replace("next user input is a different pure-image page.", "both user inputs are pageNext clicks; no page-input debounce exists to mask the wrong acknowledgement cleanup.")
p=root/'review-image-ack.cjs';assert not p.exists();p.write_text(s,encoding='utf-8')
