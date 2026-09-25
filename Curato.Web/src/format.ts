// Shared with LibraryGrid/TagGrid's thumbnail tooltips, not just Lightbox's
// caption — a grid tile's title attribute is the only way to see a photo's
// date without opening it full-screen first.
export function formatTakenAt(takenAt: string): string {
  return new Date(takenAt).toLocaleString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })
}
