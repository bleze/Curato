import { useEffect, useState } from 'react'

// A per-browser display preference (like timeFormat.ts's 24-hour toggle),
// not library data — controls the *minimum* width a grid thumbnail is
// allowed to shrink to before another column is added (LibraryGrid/TagGrid's
// `repeat(auto-fill, minmax(...))`-equivalent column math), same role the
// hardcoded 120px constant used to play.
const STORAGE_KEY = 'curato:thumbMinWidth'
const DEFAULT_THUMB_MIN_WIDTH = 120
export const MIN_THUMB_MIN_WIDTH = 80
export const MAX_THUMB_MIN_WIDTH = 280

function clamp(value: number): number {
  return Math.min(MAX_THUMB_MIN_WIDTH, Math.max(MIN_THUMB_MIN_WIDTH, value))
}

function readStoredValue(): number {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    const parsed = raw !== null ? Number.parseInt(raw, 10) : NaN
    return Number.isFinite(parsed) ? clamp(parsed) : DEFAULT_THUMB_MIN_WIDTH
  } catch {
    return DEFAULT_THUMB_MIN_WIDTH
  }
}

type Listener = () => void
const listeners = new Set<Listener>()
let cached = readStoredValue()

export function getThumbMinWidth(): number {
  return cached
}

// Called on every slider `input` event (not just on release) so the grid
// behind the Settings dialog updates live as you drag — confirmed this is
// what was actually asked for, not just "save it when you're done".
export function setThumbMinWidth(value: number): void {
  cached = clamp(value)
  try {
    localStorage.setItem(STORAGE_KEY, String(cached))
  } catch {
    // Private browsing / blocked storage — the preference just won't persist across reloads.
  }
  listeners.forEach((listener) => listener())
}

export function useThumbMinWidth(): number {
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
