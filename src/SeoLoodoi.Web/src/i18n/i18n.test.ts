import { describe, expect, it } from 'vitest'
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
import { detectInitialLanguage, formatMessage, isLanguageCode, languageInfo, languages, STORAGE_KEY } from './index'

const catalogs = { fa, en, ar, zh, es, fr, de, ru, pt, tr, hi } as const
const faKeys = Object.keys(fa).sort()
const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map((match) => match[1]).sort()

describe('i18n catalogs', () => {
  it('ships exactly the 11 supported product languages', () => {
    expect(languages.map((language) => language.code).sort()).toEqual([
      'ar', 'de', 'en', 'es', 'fa', 'fr', 'hi', 'pt', 'ru', 'tr', 'zh',
    ])
  })

  it('every locale translates every key — complete and non-empty', () => {
    for (const [code, catalog] of Object.entries(catalogs)) {
      expect(Object.keys(catalog).sort(), `locale ${code} key set`).toEqual(faKeys)
      for (const key of faKeys) {
        expect(catalog[key as keyof typeof fa], `${code}:${key} must not be empty`).not.toBe('')
      }
    }
  })

  it('every non-Persian locale is genuinely translated, not a copy of the source', () => {
    for (const [code, catalog] of Object.entries(catalogs)) {
      if (code === 'fa') continue
      // Persian and Arabic share some real words, so identical values are legal —
      // a wholesale copy is not. Requiring the majority to differ catches that.
      const identical = faKeys.filter((key) => catalog[key as keyof typeof fa] === fa[key as keyof typeof fa]).length
      expect(identical / faKeys.length, `locale ${code} looks untranslated`).toBeLessThan(0.5)
    }
  })

  it('placeholders are identical across all locales', () => {
    for (const [code, catalog] of Object.entries(catalogs)) {
      for (const key of faKeys) {
        expect(placeholders(catalog[key as keyof typeof fa]), `${code}:${key} placeholders`).toEqual(placeholders(fa[key as keyof typeof fa]))
      }
    }
  })

  it('marks Persian and Arabic as right-to-left and everything else as left-to-right', () => {
    expect(languageInfo('fa').dir).toBe('rtl')
    expect(languageInfo('ar').dir).toBe('rtl')
    for (const language of languages.filter((candidate) => candidate.code !== 'fa' && candidate.code !== 'ar')) {
      expect(language.dir, language.code).toBe('ltr')
    }
  })
})

describe('i18n runtime helpers', () => {
  it('interpolates named placeholders', () => {
    expect(formatMessage('پیشرفت {a} از {b}', { a: 5, b: 10 })).toBe('پیشرفت 5 از 10')
    expect(formatMessage('{count} open issues', { count: 3 })).toBe('3 open issues')
  })

  it('keeps unknown placeholders intact instead of dropping them', () => {
    expect(formatMessage('سلام {name}', {})).toBe('سلام {name}')
  })

  it('validates language codes', () => {
    expect(isLanguageCode('fa')).toBe(true)
    expect(isLanguageCode('en')).toBe(true)
    expect(isLanguageCode('xx')).toBe(false)
    expect(isLanguageCode(null)).toBe(false)
  })

  it('defaults to Persian when nothing is stored', () => {
    localStorage.removeItem(STORAGE_KEY)
    expect(detectInitialLanguage()).toBe('fa')
  })

  it('restores a stored supported language and ignores garbage', () => {
    localStorage.setItem(STORAGE_KEY, 'de')
    expect(detectInitialLanguage()).toBe('de')
    localStorage.setItem(STORAGE_KEY, 'not-a-language')
    expect(detectInitialLanguage()).toBe('fa')
    localStorage.removeItem(STORAGE_KEY)
  })
})
