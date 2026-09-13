import { fa } from './i18n/fa'
import type { MessageKey } from './i18n'
import { formatMessage } from './i18n'

/**
 * Legacy Persian helpers, kept for backward compatibility. They now read from
 * the canonical `fa` catalog so the translation files stay the single source
 * of truth; App.tsx itself uses the locale-aware `useI18n()` hook.
 */
const statusKey = (value: string | null | undefined): MessageKey | null =>
  value ? ((`status.${value}` as MessageKey) in fa ? (`status.${value}` as MessageKey) : null) : null

export const faStatus = (value?: string) => {
  const key = statusKey(value)
  return key ? fa[key] : value ?? '—'
}

export const faAnalysisStatus = (value?: string | null) => {
  if (!value) return '—'
  const key = `analysis.${value}` as MessageKey
  return key in fa ? fa[key] : value
}

export const analysisHint = (value?: string | null) => {
  if (!value) return ''
  const key = `analysis.hint.${value}` as MessageKey
  return key in fa ? fa[key] : ''
}

export const fmtScore = (value?: number | null) =>
  value === null || value === undefined ? '—' : value.toLocaleString('fa-IR', { maximumFractionDigits: 1 })

export const faCategory = (key: string) => {
  const catalogKey = `category.${key}` as MessageKey
  return catalogKey in fa ? fa[catalogKey] : key
}

export const isAnalysisActive = (value?: string | null) => value === 'Pending' || value === 'Running'

export const competitorCrawlHint = (crawl?: { status: string; pagesCrawled: number } | null) => {
  if (!crawl) return fa['comp.hint.notCrawled']
  const pages = crawl.pagesCrawled.toLocaleString('fa-IR')
  const base: Record<string, string> = {
    Queued: fa['comp.hint.queued'],
    Running: formatMessage(fa['comp.hint.running'], { pages }),
    Completed: formatMessage(fa['comp.hint.completed'], { pages }),
    Failed: fa['comp.hint.failed'],
    Cancelled: fa['comp.hint.cancelled'],
  }
  return base[crawl.status] ?? crawl.status
}
