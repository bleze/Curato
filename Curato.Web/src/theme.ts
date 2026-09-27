import { useEffect, useState } from 'react'

// System (the default) leaves `data-theme` unset entirely, so index.css's
// `@media (prefers-color-scheme: dark)` block keeps deciding, exactly as
// before this existed. Light/Dark set the attribute to force one regardless
// of the OS preference — index.css has a dedicated `:root[data-theme='dark']`
// block for that, and guards its media-query block with
// `:not([data-theme='light'])` so an explicit Light pick isn't overridden by
// a dark OS preference.
export type ThemePreference = 'system' | 'light' | 'dark'

const STORAGE_KEY = 'curato:theme'

type Listener = () => void
const listeners = new Set<Listener>()

function readStoredValue(): ThemePreference {
  try {
    const stored = localStorage.getItem(STORAGE_KEY)
    return stored === 'light' || stored === 'dark' ? stored : 'system'
  } catch {
    return 'system'
  }
}

function applyToDocument(value: ThemePreference): void {
  if (value === 'system') {
    document.documentElement.removeAttribute('data-theme')
  } else {
    document.documentElement.setAttribute('data-theme', value)
  }
}

let cached = readStoredValue()
// Applied at module load (before first paint) rather than only from a
// useEffect somewhere — App.tsx's own import chain reaches this module
// before it ever renders, so there's no flash of the wrong theme while
// waiting for a component to mount.
applyToDocument(cached)

export function getTheme(): ThemePreference {
  return cached
}

export function setTheme(value: ThemePreference): void {
  cached = value
  try {
    localStorage.setItem(STORAGE_KEY, value)
  } catch {
    // Private browsing / blocked storage — the preference just won't persist across reloads.
  }
  applyToDocument(value)
  listeners.forEach((listener) => listener())
}

// Same small pub/sub as useUse24HourTime — a plain localStorage read
// wouldn't re-render this tab's own radio buttons when Settings changes it.
export function useTheme(): ThemePreference {
  const [value, setValue] = useState(cached)
  useEffect(() => {
    const listener = () => setValue(cached)
    listeners.add(listener)
    return () => {
      listeners.delete(listener)
    }
  }, [])
  return value
}
