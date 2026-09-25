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
