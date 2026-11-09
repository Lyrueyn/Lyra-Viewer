from pathlib import Path
root=Path(__file__).parent
names=['review-browser.cjs','review-boundaries.cjs','review-anchor.cjs','review-advanced.cjs','review-storage.cjs','review-image-reopen.cjs','review-races.cjs','review-races-v2.cjs','review-image-ack.cjs']
for name in names:
 s=(root.parent/'gpt-review-r3'/name).read_text(encoding='utf-8')
 p=root/name;assert not p.exists(),str(p);p.write_text(s,encoding='utf-8')
print('Copied nine independent R3 regression probes unchanged into R4 review directory; input/output paths use each script directory.')
