import { useEffect, useState } from 'react'
import type { DeletedItemDto } from '../api'
import { getRecycleBin, purgeDeletedItem, recycleBinThumbnailUrl, restoreDeletedItem } from '../api'
import { RecycleBinLightbox } from './RecycleBinLightbox'
import { daysRemaining } from '../recycleBin'

export function RecycleBinPanel({ libraryPath }: { libraryPath: string }) {
  const [items, setItems] = useState<DeletedItemDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)
  // Purging is the one irreversible action here (Restore just undoes the
  // original soft-delete) — a confirm step guards against a stray click on
  // an icon button doing something Restore can't take back.
  const [confirmPurgeId, setConfirmPurgeId] = useState<number | null>(null)
  const [lightboxIndex, setLightboxIndex] = useState<number | null>(null)

  function refresh() {
    getRecycleBin(libraryPath)
      .then(setItems)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  useEffect(refresh, [libraryPath])

  async function handleRestore(id: number) {
    setBusyId(id)
    try {
      await restoreDeletedItem(libraryPath, id)
      refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusyId(null)
    }
  }

  async function handlePurge(id: number) {
    setBusyId(id)
    try {
      await purgeDeletedItem(libraryPath, id)
      refresh()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusyId(null)
      setConfirmPurgeId(null)
    }
  }

  return (
    <div className="library-tab-panel">
      <div className="recyclebin-header">
        <h2>Recycle Bin</h2>
      </div>
      <p className="recyclebin-hint">
        Deleted items stay here for 30 days before being purged automatically.
      </p>
      {error && <p className="error">{error}</p>}
      {items === null && <p>Loading…</p>}
      {items && items.length === 0 && <p>Recycle Bin is empty.</p>}
      {items && items.length > 0 && (
        <div className="recyclebin-grid">
          {items.map((item, i) => (
            <div className="recyclebin-item" key={item.id}>
              <div className="thumb">
                <img
                  src={recycleBinThumbnailUrl(libraryPath, item.id)}
                  alt=""
                  loading="lazy"
                  onClick={() => setLightboxIndex(i)}
                  onError={(e) => {
                    e.currentTarget.replaceWith(
                      Object.assign(document.createElement('div'), {
                        className: 'thumb-fallback',
                        textContent: item.kind === 'Photo' ? 'No preview' : '',
                      }),
                    )
                  }}
                />
                {item.kind === 'Video' && (
                  <div className="video-play-badge" aria-hidden="true">
                    <svg viewBox="0 0 24 24" width="32" height="32">
                      <circle cx="12" cy="12" r="11" fill="rgba(0,0,0,0.55)" />
                      <path d="M9.5 7.5v9l7-4.5z" fill="white" />
                    </svg>
                  </div>
                )}
                <button
                  type="button"
                  className="thumb-move"
                  title="Restore"
                  disabled={busyId === item.id}
                  onClick={() => handleRestore(item.id)}
                >
                  <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M9 5l7 7-7 7" />
                  </svg>
                </button>
                <button
                  type="button"
                  className="thumb-delete"
                  title="Delete forever"
                  disabled={busyId === item.id}
                  onClick={() => setConfirmPurgeId(item.id)}
                >
                  <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6h16z" />
                  </svg>
                </button>
              </div>
              <p className="recyclebin-item-path" title={item.originalRelativePath}>
                {item.originalRelativePath}
              </p>
              <p className="recyclebin-item-meta">{daysRemaining(item.purgeAt)} day(s) left</p>
            </div>
          ))}
        </div>
      )}

      {confirmPurgeId !== null && (
        <div className="event-picker-overlay" onClick={() => setConfirmPurgeId(null)}>
          <div className="confirm-dialog" onClick={(e) => e.stopPropagation()}>
            <p>Delete this item forever? This can't be undone.</p>
            <div className="confirm-dialog-actions">
              <button type="button" className="modal-footer-cancel" onClick={() => setConfirmPurgeId(null)}>
                Cancel
              </button>
              <button
                type="button"
                className="danger"
                disabled={busyId === confirmPurgeId}
                onClick={() => handlePurge(confirmPurgeId)}
              >
                Delete forever
              </button>
            </div>
          </div>
        </div>
      )}

      {lightboxIndex !== null && items && (
        <RecycleBinLightbox
          items={items}
          index={lightboxIndex}
          libraryPath={libraryPath}
          onClose={() => setLightboxIndex(null)}
          onNavigate={setLightboxIndex}
          onRestore={handleRestore}
          onPurge={handlePurge}
        />
      )}
    </div>
  )
}
