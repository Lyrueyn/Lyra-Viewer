from pathlib import Path
root=Path(__file__).parent;s=(root/'review-late-images-8s.cjs').read_text(encoding='utf-8')
s=s.replace('late-image-evidence-8s','late-lazy-evidence').replace('<img id="slow','<img loading="lazy" id="slow').replace('},8500)','},1200)')
s=s.replace('await page.waitForTimeout(3000);','await page.waitForFunction(()=>[...__lyraDebug.paginator.doc.images].every(i=>i.complete&&i.naturalWidth>0),null,{timeout:20000}).catch(()=>false);await page.waitForTimeout(350);')
s=s.replace("B12_LATE_IMAGES_PREVIOUS_CHAPTER_LAST_INTENT","E08_LAZY_IMAGES_AFTER_READY_LAST_INTENT")
s=s.replace('defect:after.page!==after.count-1','coverage:before.incomplete>0&&after.incomplete===0,defect:before.incomplete>0&&after.incomplete===0?(after.page!==after.count-1):null')
s=s.replace('8.5s image response delay after ordinary EPUB sanitization','1.2s HTTP response delay after ordinary EPUB sanitization, with genuine loading=lazy image attributes; require incomplete at ready and complete at final measurement')
p=root/'review-lazy-images.cjs';assert not p.exists();p.write_text(s,encoding='utf-8');print(p)
