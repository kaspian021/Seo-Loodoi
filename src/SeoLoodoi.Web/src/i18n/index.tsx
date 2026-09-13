import { createContext, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { fa } from './fa'
import { en } from './en'
import { ar } from './ar'
import { zh } from './zh'
import { es } from './es'
import { fr } from './fr'
import { de } from './de'
import { ru } from './ru'
import { pt } from './pt'
import { tr } from './tr'
import { hi } from './hi'

export type MessageKey = keyof typeof fa
export type Catalog = Record<MessageKey, string>

export interface LanguageInfo {
  code: LanguageCode
  /** English name (for docs/logging). */
  name: string
  /** Native name shown in the language menu. */
  nativeName: string
  dir: 'rtl' | 'ltr'
  /** BCP-47 tag used for Intl number/date formatting. */
  locale: string
}

/** Mirrors the backend SupportedLanguages list exactly — keep them in sync. */
export const languages = [
  { code: 'fa', name: 'Persian', nativeName: 'فارسی', dir: 'rtl', locale: 'fa-IR' },
  { code: 'en', name: 'English', nativeName: 'English', dir: 'ltr', locale: 'en-US' },
  { code: 'ar', name: 'Arabic', nativeName: 'العربية', dir: 'rtl', locale: 'ar' },
  { code: 'zh', name: 'Chinese', nativeName: '中文', dir: 'ltr', locale: 'zh-CN' },
  { code: 'es', name: 'Spanish', nativeName: 'Español', dir: 'ltr', locale: 'es' },
  { code: 'fr', name: 'French', nativeName: 'Français', dir: 'ltr', locale: 'fr' },
  { code: 'de', name: 'German', nativeName: 'Deutsch', dir: 'ltr', locale: 'de' },
  { code: 'ru', name: 'Russian', nativeName: 'Русский', dir: 'ltr', locale: 'ru' },
  { code: 'pt', name: 'Portuguese', nativeName: 'Português', dir: 'ltr', locale: 'pt' },
  { code: 'tr', name: 'Turkish', nativeName: 'Türkçe', dir: 'ltr', locale: 'tr' },
  { code: 'hi', name: 'Hindi', nativeName: 'हिन्दी', dir: 'ltr', locale: 'hi' },
] as const satisfies ReadonlyArray<Omit<LanguageInfo, 'code'> & { code: string }>

export type LanguageCode = 'fa' | 'en' | 'ar' | 'zh' | 'es' | 'fr' | 'de' | 'ru' | 'pt' | 'tr' | 'hi'

export const isLanguageCode = (value: string | null | undefined): value is LanguageCode =>
  languages.some((language) => language.code === value)

export const languageInfo = (code: LanguageCode): LanguageInfo =>
  languages.find((language) => language.code === code) as LanguageInfo

/** The Persian catalog is the fallback of last resort; every key exists there. */
const catalogs: Record<LanguageCode, Catalog> = { fa, en, ar, zh, es, fr, de, ru, pt, tr, hi }

export const STORAGE_KEY = 'loodoi.lang'

export function detectInitialLanguage(): LanguageCode {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (isLanguageCode(saved)) return saved
  } catch {
    /* no storage available (tests/SSR) */
  }
  return 'fa'
}

export function persistLanguage(code: LanguageCode) {
  try {
    localStorage.setItem(STORAGE_KEY, code)
  } catch {
    /* best effort only */
  }
}

export function formatMessage(template: string, vars?: Record<string, string | number>): string {
  if (!vars) return template
  return template.replace(/\{(\w+)\}/g, (match, key: string) => (key in vars ? String(vars[key]) : match))
}

export interface I18nValue {
  lang: LanguageCode
  dir: 'rtl' | 'ltr'
  locale: string
  setLang: (code: LanguageCode) => void
  /** Translates a message key; falls back to Persian, then to the key itself. */
  t: (key: MessageKey, vars?: Record<string, string | number>) => string
  /** True when the key exists in the canonical catalog (for dynamic lookups). */
  has: (key: MessageKey) => boolean
  /** Locale-aware number formatting (Persian digits for fa, etc.). */
  fmtNumber: (value: number, options?: Intl.NumberFormatOptions) => string
  /** Locale-aware date/time formatting from an ISO string. */
  fmtDate: (iso: string) => string
}

const I18nContext = createContext<I18nValue | null>(null)

export function I18nProvider({ children, initialLanguage }: { children: ReactNode; initialLanguage?: LanguageCode }) {
  const [lang, setLangState] = useState<LanguageCode>(
    initialLanguage && isLanguageCode(initialLanguage) ? initialLanguage : detectInitialLanguage(),
  )

  useEffect(() => {
    const info = languageInfo(lang)
    document.documentElement.lang = lang
    document.documentElement.dir = info.dir
  }, [lang])

  const value = useMemo<I18nValue>(() => {
    const info = languageInfo(lang)
    const primary = catalogs[lang]
    return {
      lang,
      dir: info.dir,
      locale: info.locale,
      setLang: (code: LanguageCode) => {
        if (!isLanguageCode(code)) return
        setLangState(code)
        persistLanguage(code)
      },
      t: (key, vars) => formatMessage(primary[key] ?? fa[key] ?? key, vars),
      has: (key) => key in fa,
      fmtNumber: (n, options) => n.toLocaleString(info.locale, options),
      fmtDate: (iso) => new Date(iso).toLocaleString(info.locale),
    }
  }, [lang])

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>
}

export function useI18n(): I18nValue {
  const context = useContext(I18nContext)
  if (!context) throw new Error('useI18n must be used inside I18nProvider')
  return context
}

/** Locale-aware replacement for the legacy `faStatus` helper. */
export function useStatusLabel() {
  const { t, has } = useI18n()
  return (value?: string | null) => {
    if (!value) return '—'
    const key = `status.${value}` as MessageKey
    return has(key) ? t(key) : value
  }
}

/** Locale-aware replacement for the legacy `faAnalysisStatus` helper. */
export function useAnalysisLabel() {
  const { t, has } = useI18n()
  return (value?: string | null) => {
    if (!value) return '—'
    const key = `analysis.${value}` as MessageKey
    return has(key) ? t(key) : value
  }
}

/** Locale-aware replacement for the legacy `analysisHint` helper. */
export function useAnalysisHint() {
  const { t, has } = useI18n()
  return (value?: string | null) => {
    if (!value) return ''
    const key = `analysis.hint.${value}` as MessageKey
    return has(key) ? t(key) : ''
  }
}

/** Locale-aware replacement for the legacy `faCategory` helper. */
export function useCategoryLabel() {
  const { t, has } = useI18n()
  return (value: string) => {
    const key = `category.${value}` as MessageKey
    return has(key) ? t(key) : value
  }
}

/** Locale-aware replacement for the legacy `competitorCrawlHint` helper. */
export function useCompetitorHint() {
  const { t, fmtNumber } = useI18n()
  return (crawl?: { status: string; pagesCrawled: number } | null) => {
    if (!crawl) return t('comp.hint.notCrawled')
    const pages = fmtNumber(crawl.pagesCrawled)
    const base: Record<string, string> = {
      Queued: t('comp.hint.queued'),
      Running: t('comp.hint.running', { pages }),
      Completed: t('comp.hint.completed', { pages }),
      Failed: t('comp.hint.failed'),
      Cancelled: t('comp.hint.cancelled'),
    }
    return base[crawl.status] ?? crawl.status
  }
}

/** Locale-aware replacement for the legacy `fmtScore` helper. */
export function useScoreText() {
  const { fmtNumber } = useI18n()
  return (value?: number | null) => (value === null || value === undefined ? '—' : fmtNumber(value, { maximumFractionDigits: 1 }))
}
