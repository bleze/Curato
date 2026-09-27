import { useEffect, useState } from 'react'
import type { DeletedItemDto } from '../api'
import { recycleBinRawUrl, recycleBinThumbnailUrl } from '../api'
import { daysRemaining } from '../recycleBin'

// Matches MediaController's own ViewerMaxDimension — big enough to fill most
// screens without decoding a multi-megapixel derivative for every viewer
// open.
const VIEWER_MAX_DIMENSION = 2400

// A trimmed-down sibling of Lightbox.tsx — a trashed item has no takenAt, no
// Edit pipeline, no Event/Trip membership, and the only actions that make
// sense here are Restore and Delete forever, so this doesn't try to reuse
// Lightbox's much larger prop surface (rotate/date-edit/move/favorite all
// assume a live MediaItemDto). Shares its CSS classes (.lightbox-overlay,
// .lightbox-toolbar, etc.) for the same look and feel.
export function RecycleBinLightbox({
  items,
  index,
  libraryPath,
  onClose,
  onNavigate,
  onRestore,
  onPurge,
}: {
  items: DeletedItemDto[]
  index: number
  libraryPath: string
  onClose: () => void
  onNavigate: (index: number) => void
  onRestore: (deletedItemId: number) => Promise<void>
  onPurge: (deletedItemId: number) => Promise<void>
}) {
  const item = items[index]
  const [busy, setBusy] = useState(false)
  const [confirmingPurge, setConfirmingPurge] = useState(false)

  useEffect(() => {
    setConfirmingPurge(false)
  }, [index])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
      if (e.key === 'ArrowRight' && index < items.length - 1) onNavigate(index + 1)
      if (e.key === 'ArrowLeft' && index > 0) onNavigate(index - 1)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [index, items.length, onClose, onNavigate])

  if (!item) return null

  async function handleRestore() {
    setBusy(true)
    try {
      await onRestore(item.id)
      onClose()
    } finally {
      setBusy(false)
    }
  }

  async function handlePurge() {
    setBusy(true)
    try {
      await onPurge(item.id)
      onClose()
    } finally {
      setBusy(false)
      setConfirmingPurge(false)
    }
  }

  return (
    <div className="lightbox-overlay" onClick={onClose}>
      <button type="button" className="lightbox-close" onClick={onClose} title="Close" aria-label="Close">
        <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M6 6l12 12M18 6L6 18" />
        </svg>
      </button>
      <div className="lightbox-toolbar" onClick={(e) => e.stopPropagation()}>
        <button type="button" onClick={handleRestore} disabled={busy} title="Restore" aria-label="Restore">
          <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 5l7 7-7 7" />
          </svg>
        </button>
        <button
          type="button"
          onClick={() => setConfirmingPurge(true)}
          disabled={busy}
          title="Delete forever"
          aria-label="Delete forever"
        >
          <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16z" />
          </svg>
        </button>
      </div>
      {index > 0 && (
        <button
          type="button"
          className="lightbox-nav lightbox-prev"
          onClick={(e) => {
            e.stopPropagation()
            onNavigate(index - 1)
          }}
          aria-label="Previous"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M15 5l-7 7 7 7" />
          </svg>
        </button>
      )}
      {index < items.length - 1 && (
        <button
          type="button"
          className="lightbox-nav lightbox-next"
          onClick={(e) => {
            e.stopPropagation()
            onNavigate(index + 1)
          }}
          aria-label="Next"
        >
          <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 5l7 7-7 7" />
          </svg>
        </button>
      )}
      <div className="lightbox-content" onClick={(e) => e.stopPropagation()}>
        {item.kind === 'Video' ? (
          <video src={recycleBinRawUrl(libraryPath, item.id)} controls autoPlay />
        ) : (
          // The raw trashed file may be a format browsers can't decode
          // directly (HEIC, RAW) — route photos through the same
          // thumbnail-generation pipeline used everywhere else, just at a
          // much larger size, rather than <img>-ing the raw bytes.
          <img src={recycleBinThumbnailUrl(libraryPath, item.id, VIEWER_MAX_DIMENSION)} alt="" draggable={false} />
        )}
      </div>
      <div className="lightbox-caption" onClick={(e) => e.stopPropagation()}>
        {item.originalRelativePath.split('/').pop()} &middot; {daysRemaining(item.purgeAt)} day(s) left &middot;{' '}
        {index + 1} / {items.length}
      </div>

      {confirmingPurge && (
        <div className="event-picker-overlay" onClick={(e) => e.stopPropagation()}>
          <div className="confirm-dialog">
            <p>Delete this item forever? This can't be undone.</p>
            <div className="confirm-dialog-actions">
              <button type="button" className="modal-footer-cancel" onClick={() => setConfirmingPurge(false)}>
                Cancel
              </button>
              <button type="button" className="danger" disabled={busy} onClick={handlePurge}>
                Delete forever
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
