import { test, expect, type Page, type BrowserContext } from '@playwright/test'

// No page.route()/API mocks: Chromium talks to Kestrel and migrated PostgreSQL.
// The E2E executable replaces only outbound page bytes with a controlled site.
test.describe.configure({ mode: 'serial' })
let ownerState: Awaited<ReturnType<BrowserContext['storageState']>>
let projectId: string
let crawlId: string
const suffix = `${Date.now()}-${process.pid}`
const email = `browser-owner-${suffix}@test.example`
const password = 'FixtureOnly@2026x'
const projectName = `Browser fixture ${suffix}`

async function register(page: Page, address: string) {
  await page.addInitScript(() => {
    if (!localStorage.getItem('loodoi.lang')) localStorage.setItem('loodoi.lang', 'en')
  })
  await page.goto('/')
  await page.getByRole('button', { name: 'Create account', exact: true }).click()
  await page.getByLabel('Full name', { exact: true }).fill('Browser Test User')
  await page.getByLabel('Work email', { exact: true }).fill(address)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password', { exact: true }).fill(password)
  await page.getByRole('checkbox').check()
  await page.getByRole('button', { name: 'Create account & sign in', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Add your first website' })).toBeVisible()
}

async function createProject(page: Page, name: string, url = 'https://example.com') {
  await page.getByRole('button', { name: 'Add website', exact: true }).click()
  await page.getByLabel('Project name', { exact: true }).fill(name)
  await page.getByLabel('Website address', { exact: true }).fill(url)
  const response = page.waitForResponse(r => r.url().endsWith('/api/seo/projects') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Create project', exact: true }).click()
  const created = await response
  if (created.status() !== 201) throw new Error(`create project failed: HTTP ${created.status()} ${await created.text()}`)
  const project = await created.json()
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible()
  return project.id as string
}

async function nav(page: Page, name: string) {
  await page.locator('nav').getByRole('button', { name, exact: true }).click()
}

function blockedIssue(page: Page) {
  return page.locator('.issue-detail').filter({ hasText: 'AEO_CRAWLERS_BLOCKED' })
}

test('register → project → durable crawl/analysis → AEO issues → resolve → reload and login', async ({ page }) => {
  const scriptErrors: string[] = []
  page.on('pageerror', error => scriptErrors.push(error.message))
  await register(page, email)
  projectId = await createProject(page, projectName)
  // Before any crawl exists, AEO must not invent a score or enable analysis.
  await nav(page, 'AEO / GEO')
  await expect(page.getByText('Complete a crawl before analyzing AEO.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Analyze AEO', exact: true })).toBeDisabled()
  await nav(page, 'Overview')
  const started = page.waitForResponse(r => r.url().endsWith(`/api/seo/projects/${projectId}/crawls`) && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Start crawl', exact: true }).click()
  const crawlResponse = await started
  expect(crawlResponse.status()).toBe(202)
  crawlId = (await crawlResponse.json()).id as string
  // The crawl and its analysis run as durable background jobs; the UI polls
  // while active. The AEO action may only become valid for a completed crawl,
  // and the completed crawl's analysis may still finish right after.
  await nav(page, 'AEO / GEO')
  await expect(page.getByRole('button', { name: 'Analyze AEO', exact: true })).toBeEnabled({ timeout: 120_000 })
  const fetchAnalysis = async () => page.evaluate(async ({ p, c }) => {
    const response = await fetch(`/api/seo/projects/${p}/crawls/${c}/analysis-status`, {
      headers: { Authorization: `Bearer ${localStorage.getItem('loodoi.access')}` },
    })
    return (await response.json()) as { analysisStatus: string; crawlId: string; crawlStatus: string; score: unknown; issueCount: number }
  }, { p: projectId, c: crawlId })
  await expect.poll(fetchAnalysis, { timeout: 60_000 })
    .toEqual(expect.objectContaining({ crawlId, crawlStatus: 'Completed', analysisStatus: 'Succeeded' }))
  const analysis = await fetchAnalysis()
  expect(analysis.score).not.toBeNull()
  expect(analysis.issueCount).toBeGreaterThan(0)
  await expect(page.getByText('No saved AEO assessment for this crawl.')).toBeVisible()
  const aeoResponse = page.waitForResponse(r => r.url().endsWith('/aeo/analyze') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Analyze AEO', exact: true }).click()
  const aeo = await aeoResponse
  expect(aeo.status()).toBe(200)
  const report = (await aeo.json()) as { projectId: string; crawlId: string; signals: { pagesAnalyzed: number }; crawlerAccess: { userAgentToken: string; access: string }[]; robotsAvailable: boolean }
  expect(report.projectId).toBe(projectId)
  expect(report.crawlId).toBe(crawlId)
  expect(report.robotsAvailable).toBe(true)
  expect(report.signals.pagesAnalyzed).toBe(2)
  expect(report.crawlerAccess.some(c => c.userAgentToken === 'GPTBot' && c.access === 'Blocked')).toBe(true)
  await expect(page.getByText('Analyzed text pages: 2 (sample limited to 500).')).toBeVisible()
  await nav(page, 'Audit & Issues')
  await expect(page.locator('.issue-detail').filter({ hasText: 'TITLE_MISSING' })).toHaveCount(1)
  await expect(blockedIssue(page)).toHaveCount(1)
  await blockedIssue(page).getByText('View evidence', { exact: true }).click()
  await expect(blockedIssue(page).locator('pre')).toContainText('GPTBot')
  await expect(blockedIssue(page).locator('pre')).toContainText(crawlId)
  await blockedIssue(page).getByRole('button', { name: 'Resolved', exact: true }).click()
  await expect(blockedIssue(page).getByRole('button', { name: 'Resolved' })).toHaveCount(0)
  await expect(blockedIssue(page).locator('.issue-row > span')).toHaveText('Resolved')
  await nav(page, 'AEO / GEO')
  const repeated = page.waitForResponse(r => r.url().endsWith('/aeo/analyze'))
  await page.getByRole('button', { name: 'Analyze AEO', exact: true }).click()
  expect((await repeated).status()).toBe(200)
  await page.reload()
  await nav(page, 'Audit & Issues')
  await expect(blockedIssue(page)).toHaveCount(1)
  await expect(blockedIssue(page).locator('.issue-row > span')).toHaveText('Resolved')
  await page.getByRole('button', { name: 'Sign out', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Sign in to your workspace' })).toBeVisible()
  expect(await page.evaluate(() => localStorage.getItem('loodoi.access'))).toBeNull()
  await page.getByLabel('Work email', { exact: true }).fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Secure sign-in', exact: true }).click()
  await expect(page.getByRole('heading', { name: projectName, exact: true })).toBeVisible()
  ownerState = await page.context().storageState() // kept in memory, never committed
  expect(scriptErrors).toEqual([])
})

test('a second project has no borrowed AEO data; Persian RTL and project switching preserve scope', async ({ browser }) => {
  // The API enforces a real 120 requests/minute fixed window per user. The
  // full crawl/analysis cycle in the first test can leave this window almost
  // exhausted; align with the next window boundary instead of weakening a
  // production control.
  await new Promise(resolve => setTimeout(resolve, 60_000 - (Date.now() % 60_000) + 1_500))
  const context = await browser.newContext({ storageState: ownerState })
  try {
    const page = await context.newPage()
    await page.goto('/')
    await expect(page.getByRole('heading', { name: projectName, exact: true })).toBeVisible()
    // A second project for the same owner must use a different host: the
    // schema enforces one project per (owner, normalized host).
    const emptyId = await createProject(page, `Empty fixture ${suffix}`, 'https://example.org')
    await nav(page, 'AEO / GEO')
    await expect(page.getByText('Complete a crawl before analyzing AEO.')).toBeVisible()
    await expect(page.getByText('Analyzed text pages: 2 (sample limited to 500).')).toHaveCount(0)
    await page.getByRole('combobox', { name: 'Select project' }).selectOption(projectId)
    await expect(page.getByText('Analyzed text pages: 2 (sample limited to 500).')).toBeVisible()
    await page.getByRole('combobox', { name: 'Select project' }).selectOption(emptyId)
    await expect(page.getByText('Complete a crawl before analyzing AEO.')).toBeVisible()
    await page.locator('header select.lang').selectOption('fa')
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
    await expect(page.getByText('ابتدا یک خزش را کامل کنید.')).toBeVisible()
  } finally { await context.close() }
})

test('a different real account cannot read or mutate the owner project', async ({ page }) => {
  await register(page, `browser-other-${suffix}@test.example`)
  await expect(page.getByRole('option', { name: projectName, exact: true })).toHaveCount(0)
  // Inspect access through the browser's authenticated fetch; no route stubs.
  const statuses = await page.evaluate(async ({ projectId: p, crawlId: c }) => {
    const headers = { Authorization: `Bearer ${localStorage.getItem('loodoi.access')}` }
    const root = `/api/seo/projects/${p}`
    const read = await fetch(`${root}/crawls/${c}/aeo`, { headers })
    const mutate = await fetch(`${root}/crawls/${c}/aeo/analyze`, { method: 'POST', headers })
    const issues = await fetch(`${root}/issues?crawlId=${c}`, { headers })
    return { read: read.status, mutate: mutate.status, issuesStatus: issues.status, issues: await issues.json() }
  }, { projectId, crawlId })
  expect(statuses).toEqual({ read: 404, mutate: 404, issuesStatus: 200, issues: [] })
})

test('register → project → crawl → AI expert analysis → structured output and metered credit', async ({ page }) => {
  // Phase 12 Stage 2: the AI path runs on its own account with a single project.
  // This flow intentionally creates one project; duplicate owner/host creation is
  // covered separately by the API contract and now returns an explicit 409.
  await register(page, `browser-ai-${suffix}@test.example`)
  const aiProjectId = await createProject(page, `AI fixture ${suffix}`, 'https://example.com')
  const started = page.waitForResponse(r => r.url().endsWith(`/api/seo/projects/${aiProjectId}/crawls`) && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Start crawl', exact: true }).click()
  const crawlResponse = await started
  expect(crawlResponse.status()).toBe(202)
  const aiCrawlId = (await crawlResponse.json()).id as string
  const fetchAnalysis = async () => page.evaluate(async ({ p, c }) => {
    const response = await fetch(`/api/seo/projects/${p}/crawls/${c}/analysis-status`, {
      headers: { Authorization: `Bearer ${localStorage.getItem('loodoi.access')}` },
    })
    return (await response.json()) as { analysisStatus: string; crawlId: string; crawlStatus: string }
  }, { p: aiProjectId, c: aiCrawlId })
  await expect.poll(fetchAnalysis, { timeout: 120_000 })
    .toEqual(expect.objectContaining({ crawlId: aiCrawlId, crawlStatus: 'Completed', analysisStatus: 'Succeeded' }))
  // The sidebar AI item carries an "AI" marker badge in its accessible name.
  await page.locator('nav').getByRole('button', { name: 'AI Assistant' }).click()
  await expect(page.getByRole('button', { name: 'Analyze latest snapshot', exact: true })).toBeEnabled({ timeout: 30_000 })
  const aiResponse = page.waitForResponse(r => r.url().includes('/ai/analyze') && r.request().method() === 'POST')
  await page.getByRole('button', { name: 'Analyze latest snapshot', exact: true }).click()
  const ai = await aiResponse
  expect(ai.status()).toBe(200)
  const report = (await ai.json()) as { summary: string; observations: string[]; rootCauses: string[]; recommendations: string[]; actions: string[]; confidence: number; missingEvidence: string[]; provider: string; promptVersion: string }
  // Structured output: the deterministic expert engine discloses itself through
  // the provider badge and the configured prompt version; nothing is invented.
  expect(report.provider).toBe('deterministic-expert-engine')
  expect(report.promptVersion).toBe('1.0.0')
  expect(report.summary.length).toBeGreaterThan(0)
  expect(report.observations.length).toBeGreaterThan(0)
  expect(report.rootCauses.length).toBeGreaterThan(0)
  expect(report.missingEvidence.length).toBeGreaterThan(0)
  expect(Array.isArray(report.recommendations)).toBe(true)
  expect(Array.isArray(report.actions)).toBe(true)
  expect(report.confidence).toBeGreaterThan(0)
  // Stage 2 UI renders the full structured output including the previously hidden
  // rootCauses and recommendations sections plus the provider/prompt badge.
  await expect(page.getByText('deterministic-expert-engine')).toBeVisible()
  await expect(page.getByText('Prompt version 1.0.0')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Root causes', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Recommendations', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Insufficient evidence', exact: true })).toBeVisible()
  // Credit ordering: exactly one AI credit is metered for the accepted request
  // (rejected/no-crawl requests cost zero — pinned by AiExpertContractTests).
  const credits = await page.evaluate(async () => {
    const response = await fetch('/api/seo/billing/entitlements', {
      headers: { Authorization: `Bearer ${localStorage.getItem('loodoi.access')}` },
    })
    return (await response.json()) as { plan: string; aiCreditsUsed: number; maxAiCreditsPerMonth: number }
  })
  expect(credits.plan).toBe('Starter')
  expect(credits.aiCreditsUsed).toBe(1)
  expect(credits.maxAiCreditsPerMonth).toBe(25)
})
