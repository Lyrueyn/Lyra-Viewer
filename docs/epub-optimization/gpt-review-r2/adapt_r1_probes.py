from pathlib import Path
here=Path(__file__).parent
for name in ['review-browser.cjs','review-boundaries.cjs','review-anchor.cjs']:
 text=(here.parent/'gpt-review-r1'/name).read_text(encoding='utf-8')
 text=text.replace('page=await context.newPage();','await context.addInitScript(()=>{window.__LYRA_TEST_MODE__=true;});page=await context.newPage();')
 text=text.replace('page=await ctx.newPage();','await ctx.addInitScript(()=>{window.__LYRA_TEST_MODE__=true;});page=await ctx.newPage();')
 text=text.replace("const page=await browser.newPage({viewport:{width:1400,height:900}});await page.goto", "const page=await browser.newPage({viewport:{width:1400,height:900}});await page.addInitScript(()=>{window.__LYRA_TEST_MODE__=true;});await page.goto")
 text=text.replace('/* Independent R1 review. Uses only the extracted candidate and synthetic books. */','/* R1 regression probes adapted only to enable R2 test diagnostics; candidate product code remains untouched. */')
 target=here/name
 if target.exists():raise RuntimeError('Do not overwrite existing probe')
 target.write_text(text,encoding='utf-8')
print('Prepared three isolated R1 regression probes with R2 diagnostic switch.')
