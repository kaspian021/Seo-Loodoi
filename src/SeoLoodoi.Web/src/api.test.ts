import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'

// B1/F10 regression: the backend answers crawl starts, analysis retries and
// competitor crawls with 202 + JSON bodies. Discarding those bodies made the
// typed clients (e.g. Promise<Crawl>) resolve undefined.
describe('api 202 handling', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    localStorage.clear()
  })

  function mockResponse(body: unknown, status: number) {
    global.fetch = vi.fn().mockResolvedValue(
      new Response(body === null ? null : JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
  }

  it('startCrawl returns the accepted crawl body from a 202', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const crawl = { id: 'c1', status: 'Queued', pagesDiscovered: 0, pagesCrawled: 0, errors: 0 }
    mockResponse(crawl, 202)

    const result = await api.startCrawl('p1')

    expect(result).toEqual(crawl)
  })

  it('retryAnalysis returns the accepted analysis-status body from a 202', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const status = { crawlId: 'c1', projectId: 'p1', crawlStatus: 'Running', analysisStatus: 'Pending', attempts: 0, issueCount: 0, retryable: false }
    mockResponse(status, 202)

    const result = await api.retryAnalysis('p1', 'c1')

    expect(result).toEqual(status)
  })

  it('still resolves undefined for an empty 202 (resume)', async () => {
    localStorage.setItem('loodoi.access', 'token')
    mockResponse(null, 202)

    const result = await api.crawlAction('p1', 'c1', 'resume')

    expect(result).toBeUndefined()
  })

  it('still resolves undefined for 204', async () => {
    localStorage.setItem('loodoi.access', 'token')
    mockResponse(null, 204)

    const result = await api.crawlAction('p1', 'c1', 'cancel')

    expect(result).toBeUndefined()
  })
})
