// Shared with LibraryGrid/TagGrid's thumbnail tooltips, not just Lightbox's
// caption — a grid tile's title attribute is the only way to see a photo's
// date without opening it full-screen first.
export function formatTakenAt(takenAt: string, use24Hour: boolean): string {
  return new Date(takenAt).toLocaleString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    // Omitted (rather than always passing hour12: true) when not forcing
    // 24-hour, so the browser's own locale default still applies — this
    // only overrides it, doesn't replace it with a different default.
    ...(use24Hour ? { hour12: false } : {}),
  })
}

/**
 * <input type="datetime-local">'s value format ("YYYY-MM-DDTHH:mm", no
 * timezone) — built from the Date object's LOCAL getters (not
 * toISOString, which would convert to UTC) so the input shows the same
 * wall-clock time formatTakenAt already displays, and round-trips back
 * out the same way. Shared by Lightbox's per-photo date edit and
 * LibraryGrid's "Fix dates…" bulk prefill.
 */
export function toDatetimeLocalValue(takenAt: string): string {
  const d = new Date(takenAt)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}
