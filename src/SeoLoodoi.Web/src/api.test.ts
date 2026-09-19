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

  it('entitlements fetches and returns tenant entitlement data', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const entitlement = {
      tenantId: 't1',
      plan: 'Pro',
      status: 'Active',
      maxProjects: 15,
      projectsUsed: 3,
      maxPagesPerMonth: 50000,
      pagesUsed: 1200,
      maxKeywords: 500,
      keywordsUsed: 42,
      maxCompetitors: 20,
      competitorsUsed: 5,
      maxTeamMembers: 10,
      teamMembersUsed: 2,
      maxAiCreditsPerMonth: 1000,
      aiCreditsUsed: 15,
      allowCustomBranding: true,
      allowApiAccess: true,
      allowHourlyCrawl: true,
      currentPeriodEnd: '2026-10-15T00:00:00Z',
    }
    mockResponse(entitlement, 200)

    const result = await api.entitlements()

    expect(result).toEqual(entitlement)
  })

  it('checkout requests checkout session token', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const checkoutResp = {
      checkoutUrl: 'https://billing.loodoi.com/checkout?session=xyz',
      sessionToken: 'signed-token-xyz',
    }
    mockResponse(checkoutResp, 200)

    const result = await api.checkout('Pro', 'https://seo.loodoi.com/return')

    expect(result).toEqual(checkoutResp)
  })

  it('redirects fetches crawl redirect chains', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const redirects = [
      { id: 'u1', url: 'https://example.com/dest', statusCode: 200, responseTimeMs: 140, redirectChainJson: '[{"fromUrl":"http://example.com/src","toUrl":"https://example.com/dest","statusCode":301,"durationMs":50}]' }
    ]
    mockResponse(redirects, 200)

    const result = await api.redirects('p1', 'c1')

    expect(result).toEqual(redirects)
  })

  it('assets fetches crawl asset snapshots', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const assets = [
      { crawledUrlId: 'u1', assetsJson: '[{"type":"Image","url":"https://example.com/img.png","isMixedContent":false}]' }
    ]
    mockResponse(assets, 200)

    const result = await api.assets('p1', 'c1')

    expect(result).toEqual(assets)
  })

  it('linkGraph fetches link graph metrics and top anchors', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const graphData = {
      summary: { totalInternalLinks: 42, orphanPages: 1, deadEndPages: 2, weaklyLinkedPages: 3 },
      topAnchors: [{ text: 'Home', count: 10, isGeneric: false }],
      pages: [{ pageId: 'p1', url: 'https://example.com/', inDegree: 5, outDegree: 4, internalAuthority: 98.5, isOrphan: false, isWeaklyLinked: false, isDeadEnd: false }]
    }
    mockResponse(graphData, 200)

    const result = await api.linkGraph('proj1', 'crawl1')

    expect(result).toEqual(graphData)
  })

  it('contentAnalysis fetches readability metrics and keyword densities', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const contentData = {
      summary: { pagesAnalyzed: 10, totalWords: 5400, averageReadabilityScore: 82.5, thinContentPages: 1, keywordStuffingPages: 0 },
      pages: [
        {
          url: 'https://example.com/guide',
          readability: { wordCount: 540, sentenceCount: 30, averageSentenceLength: 18, longSentenceCount: 2, longSentencePercentage: 6.7, paragraphCount: 5, readabilityScore: 82.5, readabilityGrade: 'Easy' },
          topKeywords: [{ term: 'guide', count: 12, densityPercentage: 2.2, gramSize: 1, isStuffing: false }],
          hasKeywordStuffing: false,
          isThinContent: false,
        }
      ]
    }
    mockResponse(contentData, 200)

    const result = await api.contentAnalysis('proj1', 'crawl1')

    expect(result).toEqual(contentData)
  })

  it('keywordSummary fetches keyword rank tracking summary', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const summaryData = {
      totalTracked: 50,
      top3Count: 8,
      top10Count: 22,
      top20Count: 35,
      top100Count: 48,
      improvedCount: 14,
      declinedCount: 6,
      cannibalizationCount: 2,
      totalImpressions: 12500,
      totalClicks: 820,
    }
    mockResponse(summaryData, 200)

    const result = await api.keywordSummary('proj1')

    expect(result).toEqual(summaryData)
  })

  it('keywordCannibalization fetches cannibalization alerts with competing pages', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const cannibalizationData = [
      {
        keywordId: 'k1',
        phrase: 'سئو تکنیکال',
        competingPages: [
          { pageUrl: 'https://example.com/a', impressions: 60, clicks: 10, averagePosition: 3.2, impressionSharePercentage: 60.0 },
          { pageUrl: 'https://example.com/b', impressions: 40, clicks: 4, averagePosition: 7.5, impressionSharePercentage: 40.0 }
        ],
        severity: 'High',
        explanation: 'Competing landing pages detected.'
      }
    ]
    mockResponse(cannibalizationData, 200)

    const result = await api.keywordCannibalization('proj1')

    expect(result).toEqual(cannibalizationData)
  })

  it('batchAddKeywords imports multiple keyword phrases', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const batchResult = {
      addedCount: 2,
      skippedCount: 1,
      addedKeywords: [
        { id: 'k1', phrase: 'سئو داخلی', language: 'fa', country: 'IR', isTracked: true, clicks: null, impressions: null, ctr: null, averagePosition: null, source: 'none' },
        { id: 'k2', phrase: 'سئو خارجی', language: 'fa', country: 'IR', isTracked: true, clicks: null, impressions: null, ctr: null, averagePosition: null, source: 'none' }
      ]
    }
    mockResponse(batchResult, 200)

    const result = await api.batchAddKeywords('proj1', ['سئو داخلی', 'سئو خارجی', 'سئو داخلی'])

    expect(result).toEqual(batchResult)
  })

  it('competitorGaps fetches topic gaps and strategic recommendations', async () => {
    localStorage.setItem('loodoi.access', 'token')
    const gapsData = [
      {
        competitorId: 'c1',
        competitorName: 'Competitor A',
        commonTopicsCount: 5,
        missingTopicsCount: 2,
        topicGaps: [
          {
            topic: 'طراحی سایت وردپرس',
            competitorName: 'Competitor A',
            competitorPageUrl: 'https://competitor.com/wordpress',
            competitorWordCount: 1400,
            gapType: 'MissingInProject',
            recommendation: 'Create a dedicated page.'
          }
        ],
        summaryVerdict: '2 content opportunities detected.'
      }
    ]
    mockResponse(gapsData, 200)

    const result = await api.competitorGaps('proj1', 'crawl1')

    expect(result).toEqual(gapsData)
  })
})
