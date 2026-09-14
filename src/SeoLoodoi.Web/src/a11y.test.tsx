import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import App from './App'
import { I18nProvider } from './i18n'

// Accessibility smoke: the auth screen must expose its controls with real
// accessible names (labels, not placeholders), and the language switcher
// must flip the document direction for LTR locales.
describe('accessibility smoke', () => {
  beforeEach(() => {
    localStorage.clear()
    cleanup()
  })

  it('auth page exposes labeled controls in Persian (RTL) by default', () => {
    render(<I18nProvider><App /></I18nProvider>)

    expect(document.documentElement.dir).toBe('rtl')
    expect(screen.getByRole('combobox', { name: 'زبان رابط کاربری' })).toBeDefined()
    expect(screen.getByLabelText('ایمیل کاری')).toBeDefined()
    expect(screen.getByLabelText('رمز عبور')).toBeDefined()
  })

  it('switching to English flips the document direction and translates the switcher', () => {
    render(<I18nProvider><App /></I18nProvider>)

    fireEvent.change(screen.getByRole('combobox', { name: 'زبان رابط کاربری' }), { target: { value: 'en' } })

    expect(document.documentElement.dir).toBe('ltr')
    expect(document.documentElement.lang).toBe('en')
    expect(screen.getByRole('combobox', { name: 'Interface language' })).toBeDefined()
    expect(screen.getByLabelText('Work email')).toBeDefined()
  })
})
