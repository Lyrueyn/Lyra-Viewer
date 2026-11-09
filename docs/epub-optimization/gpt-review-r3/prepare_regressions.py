from pathlib import Path
root=Path(__file__).parent
for name in ['review-browser.cjs','review-boundaries.cjs','review-anchor.cjs','review-advanced.cjs','review-storage.cjs','review-image-reopen.cjs']:
 s=(root.parent/'gpt-review-r2'/name).read_text(encoding='utf-8')
 # Preserve R3 preference metadata through the test's delayed transport shim.
 if name=='review-advanced.cjs':
  s=s.replace('c.savePreferences=async p=>{','c.savePreferences=async(p,...metadata)=>{')
  s=s.replace('await orig(frozen);window.__saveStarted','await orig(frozen,...metadata);window.__saveStarted')
  s=s.replace('return orig(frozen);','return orig(frozen,...metadata);')
  s=s.replace('pending:__lyraDebug.state.pendingPrefsPatch','pending:__lyraDebug.state.pendingPrefs')
 t=root/name
 assert not t.exists(), str(t)
 t.write_text(s,encoding='utf-8')
print('Copied six R2 regression scripts to R3 review, preserving R3 preference metadata in delay shim.')
