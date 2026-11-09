from pathlib import Path
p=Path(__file__).parent
s=(p/'review-late-images.cjs').read_text(encoding='utf-8')
s=s.replace('late-image-evidence','image-reopen-evidence').replace('},3700)','},50)').replace('waitForTimeout(1800)','waitForTimeout(600)').replace('B11_LATE_IMAGES_PREVIOUS_CHAPTER_LAST_INTENT','B15_ACTUAL_IMAGE_ANCHOR_REOPEN')
s=s.replace('await page.screenshot(','''const saved=await page.evaluate(()=>localStorage.getItem('lyra_epub_state_'+__lyraDebug.state.bookId));await page.reload();await page.waitForFunction(()=>__lyraDebug.state.ready);const reopened=await page.evaluate(()=>({page:__lyraDebug.paginator.pageIndex,count:__lyraDebug.paginator.pageCount,anchor:__lyraDebug.paginator.captureAnchor()}));await page.screenshot(''')
s=s.replace('before,after,lateRequests,defect:after.page!==after.count-1','before,after,saved,reopened,lateRequests,defect:reopened.page!==after.page')
s=s.replace('Previous chapter UI action requests last page. The review injects 3.7s image response delay after ordinary EPUB sanitization; no product source changes.', 'Actual chapter-prev action saves mixed chapter image-only last page, then browser reload tests original saved locator. Image responses complete in 50ms.')
t=p/'review-image-reopen.cjs'
assert not t.exists()
t.write_text(s,encoding='utf-8')
