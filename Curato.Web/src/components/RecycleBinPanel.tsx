import { useEffect, useState } from 'react'
import type { DeletedItemDto } from '../api'
import { getRecycleBin, purgeDeletedItem, recycleBinThumbnailUrl, restoreDeletedItem } from '../api'

function daysRemaining(purgeAt: string): number {
  const ms = new Date(purgeAt).getTime() - Date.now()
  return Math.max(0, Math.ceil(ms / (1000 * 60 * 60 * 24)))
}

export function RecycleBinPanel({ libraryPath }: { libraryPath: string }) {
  const [items, setItems] = useState<DeletedItemDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)
  // Purging is the one irreversible action here (Restore just undoes the
  // original soft-delete) — a confirm step guards against a stray click on
  // an icon button doing something Restore can't take back.
  const [confirmPurgeId, setConfirmPurgeId] = useState<number | null>(null)

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
          {items.map((item) => (
            <div className="recyclebin-item" key={item.id}>
              <div className="thumb">
                {item.kind === 'Photo' ? (
                  <img src={recycleBinThumbnailUrl(libraryPath, item.id)} alt="" loading="lazy" />
                ) : (
                  <div className="thumb-fallback">Video</div>
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
                  <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
                    <path d="M6 6l12 12M18 6L6 18" />
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
    </div>
  )
}
