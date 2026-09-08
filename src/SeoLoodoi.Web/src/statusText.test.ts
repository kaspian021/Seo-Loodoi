import { describe, expect, it } from 'vitest'
import { analysisHint, competitorCrawlHint, faAnalysisStatus, faCategory, fmtScore, isAnalysisActive } from './statusText'

describe('analysis status text', () => {
  it('labels every lifecycle state in Persian', () => {
    expect(faAnalysisStatus('Pending')).toBe('تحلیل در انتظار')
    expect(faAnalysisStatus('Running')).toBe('تحلیل در حال اجرا')
    expect(faAnalysisStatus('Succeeded')).toBe('تحلیل موفق')
    expect(faAnalysisStatus('Failed')).toBe('تحلیل ناموفق')
    expect(faAnalysisStatus('NoData')).toBe('بدون داده برای تحلیل')
  })
  it('explains pending, failed and no-data states', () => {
    expect(analysisHint('Pending')).toContain('خودکار')
    expect(analysisHint('Failed')).toContain('تلاش مجدد')
    expect(analysisHint('NoData')).toContain('صفحه')
  })
  it('detects active analysis for polling', () => {
    expect(isAnalysisActive('Pending')).toBe(true)
    expect(isAnalysisActive('Running')).toBe(true)
    expect(isAnalysisActive('Succeeded')).toBe(false)
    expect(isAnalysisActive('Failed')).toBe(false)
    expect(isAnalysisActive(undefined)).toBe(false)
  })
})

describe('score formatting', () => {
  it('renders missing evidence as a dash, never a fabricated number', () => {
    expect(fmtScore(null)).toBe('—')
    expect(fmtScore(undefined)).toBe('—')
    expect(fmtScore(85.25)).toContain('۸۵')
  })
  it('labels score categories in Persian', () => {
    expect(faCategory('technical')).toBe('فنی')
    expect(faCategory('structuredData')).toBe('داده ساخت‌یافته')
  })
})

describe('competitor crawl hint', () => {
  it('describes uncrawled and completed states with real page counts', () => {
    expect(competitorCrawlHint(null)).toBe('هنوز خزش نشده')
    expect(competitorCrawlHint({ status: 'Completed', pagesCrawled: 12 })).toContain('۱۲')
    expect(competitorCrawlHint({ status: 'Completed', pagesCrawled: 12 })).toContain('واقعی')
    expect(competitorCrawlHint({ status: 'Running', pagesCrawled: 3 })).toContain('در حال خزش')
  })
})
