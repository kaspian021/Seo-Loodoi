import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AiView } from './App'
import { api, type AiResponse, type AnalysisStatus, type Crawl, type SeoProject } from './api'
import { I18nProvider, STORAGE_KEY } from './i18n'

const project: SeoProject = { id: 'project-a', name: 'AI fixture', baseUrl: 'https://example.com', normalizedHost: 'example.com', status: 'Active', createdAt: '2026-01-01T00:00:00Z' }
const crawl: Crawl = { id: 'crawl-a', status: 'Completed', pagesDiscovered: 2, pagesCrawled: 2, errors: 0 }
const succeeded: AnalysisStatus = { crawlId: 'crawl-a', projectId: 'project-a', crawlStatus: 'Completed', analysisStatus: 'Succeeded', attempts: 1, issueCount: 2, retryable: false }

function response(): AiResponse {
  return { summary: 'Evidence-based summary', observations: ['Observation one'], rootCauses: ['Root cause one'],
    recommendations: ['Recommendation one'], actions: ['Action one'], confidence: 0.95,
    missingEvidence: ['Missing keyword tracking'], provider: 'deterministic-expert-engine', promptVersion: '1.0.0' }
}
function view(analysis: AnalysisStatus | null = succeeded) {
  return <I18nProvider><AiView project={project} latest={crawl} analysis={analysis} /></I18nProvider>
}

beforeEach(() => { localStorage.setItem(STORAGE_KEY, 'en') })
afterEach(() => { cleanup(); vi.restoreAllMocks(); localStorage.clear() })

describe('AI expert workspace', () => {
  it('keeps the analyze action gated on a succeeded crawl analysis', () => {
    const run = vi.spyOn(api, 'ai')
    const { rerender } = render(view(null))
    expect(screen.getByText('The assistant activates after the first crawl completes and analyzes successfully.')).toBeDefined()
    expect((screen.getByRole('button', { name: 'Analyze latest snapshot' }) as HTMLButtonElement).disabled).toBe(true)
    rerender(view({ ...succeeded, analysisStatus: 'Running' }))
    expect((screen.getByRole('button', { name: 'Analyze latest snapshot' }) as HTMLButtonElement).disabled).toBe(true)
    expect(run).not.toHaveBeenCalled()
  })

  it('renders the full structured output with root causes, recommendations, provider badge and prompt version', async () => {
    const run = vi.spyOn(api, 'ai').mockResolvedValue(response())
    render(view())
    fireEvent.click(screen.getByRole('button', { name: 'Analyze latest snapshot' }))
    await screen.findByText('Evidence-based summary')
    expect(run).toHaveBeenCalledWith('project-a', 'crawl-a')
    expect(screen.getByText('Observation one')).toBeDefined()
    expect(screen.getByText('Root cause one')).toBeDefined()
    expect(screen.getByText('Recommendation one')).toBeDefined()
    expect(screen.getByText('Action one')).toBeDefined()
    expect(screen.getByText('Missing keyword tracking')).toBeDefined()
    // Provider badge + promptVersion are the transparency channel between the
    // deterministic expert engine and an openai-compatible provider.
    expect(screen.getByText(/deterministic-expert-engine/)).toBeDefined()
    expect(screen.getByText(/Prompt version 1\.0\.0/)).toBeDefined()
    expect(screen.getByText(/Confidence 95%/)).toBeDefined()
  })

  it('shows analysis errors as errors and never invents structured output', async () => {
    vi.spyOn(api, 'ai').mockRejectedValue(new Error('network down'))
    render(view())
    fireEvent.click(screen.getByRole('button', { name: 'Analyze latest snapshot' }))
    await screen.findByRole('alert')
    expect(screen.getByRole('alert').textContent).toContain('network down')
    expect(screen.queryByText('Evidence-based summary')).toBeNull()
    expect(screen.queryByText('Root cause one')).toBeNull()
  })
})
