import { useEffect, useState } from 'react'

export type Theme = 'light' | 'dark' | 'system'

const THEME_KEY = 'theme'

/** localStorage can throw (private windows, blocked site data); the choice is only a convenience. */
export function readSetting(key: string): string | null {
  try { return localStorage.getItem(key) } catch { return null }
}

export function writeSetting(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key)
    else localStorage.setItem(key, value)
  } catch { /* not remembered, still applied */ }
}

function storedTheme(): Theme {
  const saved = readSetting(THEME_KEY)
  return saved === 'light' || saved === 'dark' ? saved : 'system'
}

/** Sets data-theme on <html>; "system" follows the OS setting. index.css keys every colour off it. */
export function applyTheme(theme: Theme = storedTheme()) {
  const dark = theme === 'system' ? window.matchMedia('(prefers-color-scheme: dark)').matches : theme === 'dark'
  document.documentElement.dataset.theme = dark ? 'dark' : 'light'
}

export function useTheme() {
  const [theme, setThemeState] = useState<Theme>(storedTheme)

  useEffect(() => {
    applyTheme(theme)
    if (theme !== 'system') return
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const handler = () => applyTheme('system')
    mq.addEventListener('change', handler)
    return () => mq.removeEventListener('change', handler)
  }, [theme])

  const setTheme = (next: Theme) => {
    writeSetting(THEME_KEY, next === 'system' ? null : next)
    setThemeState(next)
  }

  return { theme, setTheme }
}
