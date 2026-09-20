import { useEffect, useRef, useState } from 'react'
import { api, type AeoReport, type Crawl } from './api'
import { useI18n, type MessageKey } from './i18n'

export function AeoView({ projectId, latest, onUpdated }: { projectId: string; latest?: Crawl; onUpdated: () => void }) {
  // Project/crawl switches unmount the old request state. Never show an old
  // project's report while the next project's request is in flight.
  return <AeoPanel key={`${projectId}:${latest?.id ?? 'none'}`} projectId={projectId} latest={latest} onUpdated={onUpdated} />
}

function AeoPanel({ projectId, latest, onUpdated }: { projectId: string; latest?: Crawl; onUpdated: () => void }) {
  const { t, fmtNumber } = useI18n()
  const [report, setReport] = useState<AeoReport | null>(null)
  const [loading, setLoading] = useState(Boolean(latest))
  const [running, setRunning] = useState(false)
  const [error, setError] = useState('')
  const [reload, setReload] = useState(0)
  const crawlId = latest?.id
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  useEffect(() => {
    let cancelled = false
    if (!crawlId) return
    void api.aeo(projectId, crawlId)
      .then(value => { if (!cancelled) setReport(value) })
      .catch(() => { if (!cancelled) setError(t('aeo.loadError')) })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [projectId, crawlId, reload, t])

  async function analyze() {
    if (!latest || running) return
    setRunning(true)
    setError('')
    try {
      const result = await api.analyzeAeo(projectId, latest.id)
      if (alive.current) { setReport(result); onUpdated() }
    } catch {
      if (alive.current) setError(t('aeo.runError'))
    } finally {
      if (alive.current) setRunning(false)
    }
  }

  const scores: [MessageKey, number | null][] = report ? [
    ['aeo.visibility', report.aiVisibilityScore], ['aeo.crawlability', report.aiCrawlabilityScore],
    ['aeo.answer', report.answerReadinessScore], ['aeo.citation', report.citationReadinessScore],
  ] : []
  const accessLabel = (access: string) => access === 'Allowed' ? t('aeo.allowed') : access === 'Blocked' ? t('aeo.blocked') : t('aeo.unspecified')
  return <section aria-label={t('aeo.title')} aria-busy={loading || running}>
    <div className="page-title"><div><h2>{t('aeo.title')}</h2><p>{t('aeo.description')}</p></div>
      <button className="primary" disabled={!latest || latest.status !== 'Completed' || loading || running} onClick={() => void analyze()}>
        {running ? t('aeo.running') : t('aeo.run')}
      </button>
    </div>
    <p className="notice-banner info">{t('aeo.disclaimer')}</p>
    {error && <div role="alert" className="form-error">{error} <button className="table-action" disabled={loading || running} onClick={() => { setError(''); setLoading(true); setReload(n => n + 1) }}>{t('aeo.retry')}</button></div>}
    {loading ? <p role="status">{t('aeo.loading')}</p> : !latest ? <p className="mini-empty">{t('aeo.noCrawl')}</p> : !report ? <p className="mini-empty">{t('aeo.noReport')}</p> : <>
      <div className="metrics mini-metrics">{scores.map(([label, value]) => <div className="metric" key={label}>
        <span>{t(label)}</span><strong>{value == null ? '—' : fmtNumber(value, { maximumFractionDigits: 1 })}</strong>
        <small>{value == null ? t('aeo.unknown') : '/ 100'}</small>
      </div>)}</div>
      <p>{t('aeo.sample', { count: report.signals.pagesAnalyzed })}</p>
      {!report.robotsAvailable && <p className="notice-banner partial">{t('aeo.robotsUnknown')}</p>}
      <div className="panel"><h3>{t('aeo.crawlers')}</h3>
        <ul>{report.crawlerAccess.map(c => <li key={c.key}><b>{c.displayName}</b> — <code>{c.userAgentToken}</code> — {report.robotsAvailable ? accessLabel(c.access) : t('aeo.unknown')}</li>)}</ul>
        <h3>{t('aeo.findings')}</h3>
        {report.findings.length ? <ul>{report.findings.map((finding, index) => <li key={index} dir="auto">{finding}</li>)}</ul> : <p>{t('aeo.noFindings')}</p>}
        <p>{t('aeo.issuesHint')}</p>
      </div>
    </>}
  </section>
}
