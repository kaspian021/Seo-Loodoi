import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AeoView } from './AeoView'
import { api, type AeoReport, type Crawl } from './api'
import { I18nProvider, STORAGE_KEY } from './i18n'

const crawl: Crawl = { id: 'crawl-a', status: 'Completed', pagesDiscovered: 1, pagesCrawled: 1, errors: 0 }
function report(projectId = 'project-a'): AeoReport {
  return { projectId, crawlId: 'crawl-a', aiCrawlabilityScore: null, answerReadinessScore: 0,
    citationReadinessScore: null, aiVisibilityScore: null, robotsAvailable: false,
    crawlerAccess: [{ key: 'bot', displayName: 'Test Bot', userAgentToken: 'TestBot', purpose: 'AnswerEngine', access: 'Unspecified', weight: 1 }],
    findings: ['Observed fixture note'], signals: { pagesAnalyzed: 1, pagesWithQuestionHeadings: 0, pagesWithFaqSchema: 0,
      pagesWithAnySchema: 0, pagesWithEntitySchema: 0, pagesWithAuthorOrDate: 0, pagesWithCanonical: 0,
      pagesWithConciseAnswer: 0, pagesWithHeadingStructure: 0 } }
}
function view(projectId = 'project-a', latest: Crawl | undefined = crawl, updated = vi.fn()) {
  return <I18nProvider><AeoView projectId={projectId} latest={latest} onUpdated={updated} /></I18nProvider>
}

beforeEach(() => { localStorage.setItem(STORAGE_KEY, 'en') })
afterEach(() => { cleanup(); vi.restoreAllMocks(); localStorage.clear() })

describe('AEO workspace', () => {
  it('does not fetch or analyze without a crawl', () => {
    const read = vi.spyOn(api, 'aeo')
    render(<I18nProvider><AeoView projectId="a" onUpdated={vi.fn()} /></I18nProvider>)
    expect(screen.getByText('Complete a crawl before analyzing AEO.')).toBeDefined()
    expect((screen.getByRole('button', { name: 'Analyze AEO' }) as HTMLButtonElement).disabled).toBe(true)
    expect(read).not.toHaveBeenCalled()
  })

  it('distinguishes unknown scores from observed zero and does not claim access when robots is unavailable', async () => {
    vi.spyOn(api, 'aeo').mockResolvedValue(report())
    render(view())
    await screen.findByText('Observed fixture note')
    expect(screen.getAllByText('—')).toHaveLength(3)
    expect(screen.getByText('0')).toBeDefined()
    expect(screen.queryByText('No explicit crawler policy')).toBeNull()
    expect(screen.getByText(/robots.txt was unavailable/)).toBeDefined()
  })

  it('shows no saved report and runs an explicit analysis, refreshing issues on success', async () => {
    vi.spyOn(api, 'aeo').mockResolvedValue(null)
    const run = vi.spyOn(api, 'analyzeAeo').mockResolvedValue(report())
    const updated = vi.fn()
    render(view('project-a', crawl, updated))
    await screen.findByText('No saved AEO assessment for this crawl.')
    fireEvent.click(screen.getByRole('button', { name: 'Analyze AEO' }))
    await screen.findByText('Observed fixture note')
    expect(run).toHaveBeenCalledWith('project-a', 'crawl-a')
    expect(updated).toHaveBeenCalledTimes(1)
  })

  it('shows read errors, supports retry, and never reports them as successful analysis', async () => {
    vi.spyOn(api, 'aeo').mockRejectedValueOnce(new Error('network')).mockResolvedValueOnce(null)
    render(view())
    await screen.findByRole('alert')
    fireEvent.click(screen.getByRole('button', { name: 'Reload' }))
    await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
    expect(screen.getByText('No saved AEO assessment for this crawl.')).toBeDefined()
  })

  it('keeps the saved report on failed analysis and does not refresh issues', async () => {
    vi.spyOn(api, 'aeo').mockResolvedValue(report())
    vi.spyOn(api, 'analyzeAeo').mockRejectedValue(new Error('access denied'))
    const updated = vi.fn()
    render(view('project-a', crawl, updated))
    await screen.findByText('Observed fixture note')
    fireEvent.click(screen.getByRole('button', { name: 'Analyze AEO' }))
    await screen.findByRole('alert')
    expect(screen.getByText('Observed fixture note')).toBeDefined()
    expect(updated).not.toHaveBeenCalled()
  })

  it('disables duplicate submissions while analysis is pending', async () => {
    vi.spyOn(api, 'aeo').mockResolvedValue(null)
    let resolve!: (value: AeoReport) => void
    const run = vi.spyOn(api, 'analyzeAeo').mockReturnValue(new Promise(r => { resolve = r }))
    render(view())
    await screen.findByText('No saved AEO assessment for this crawl.')
    fireEvent.click(screen.getByRole('button', { name: 'Analyze AEO' }))
    const button = screen.getByRole('button', { name: 'Analyzing…' }) as HTMLButtonElement
    expect(button.disabled).toBe(true)
    fireEvent.click(button)
    expect(run).toHaveBeenCalledTimes(1)
    await act(async () => resolve(report()))
  })

  it('discards late responses after switching project', async () => {
    let resolve!: (value: AeoReport) => void
    vi.spyOn(api, 'aeo').mockReturnValueOnce(new Promise(r => { resolve = r })).mockResolvedValueOnce(null)
    const rendered = render(view())
    rendered.rerender(view('project-b'))
    await screen.findByText('No saved AEO assessment for this crawl.')
    await act(async () => resolve(report('project-a')))
    expect(screen.queryByText('Observed fixture note')).toBeNull()
  })

  it('does not enable analysis for an active crawl', async () => {
    vi.spyOn(api, 'aeo').mockResolvedValue(null)
    render(view('project-a', { ...crawl, status: 'Running' }))
    await screen.findByText('No saved AEO assessment for this crawl.')
    expect((screen.getByRole('button', { name: 'Analyze AEO' }) as HTMLButtonElement).disabled).toBe(true)
  })
})
