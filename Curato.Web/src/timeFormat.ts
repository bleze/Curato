import { useEffect, useState } from 'react'

// `toLocaleString` with no explicit `hour12` follows the *browser's* locale
// default, not the OS/regional 24-hour setting some users actually expect —
// reported live as AM/PM showing up for someone who wants 24-hour time. A
// per-browser override (localStorage, not a server setting: this is a
// display preference, not library data) lets it be forced either way,
// independent of whatever the browser's own locale happens to be.
const STORAGE_KEY = 'curato:use24HourTime'

type Listener = () => void
const listeners = new Set<Listener>()

function readStoredValue(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === 'true'
  } catch {
    return false
  }
}

let cached = readStoredValue()

export function getUse24HourTime(): boolean {
  return cached
}

export function setUse24HourTime(value: boolean): void {
  cached = value
  try {
    localStorage.setItem(STORAGE_KEY, String(value))
  } catch {
    // Private browsing / blocked storage — the preference just won't persist across reloads.
  }
  listeners.forEach((listener) => listener())
}

// A plain localStorage read wouldn't re-render components on change (the
// `storage` event only fires in *other* tabs, never the one that made the
// change) — this small pub/sub makes every subscriber re-render immediately
// when the Settings toggle flips it, in the same tab.
export function useUse24HourTime(): boolean {
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
