import { useEffect, useState } from 'react'
import type { DeletedItemDto } from '../api'
import { getRecycleBin, purgeDeletedItem, recycleBinThumbnailUrl, restoreDeletedItem } from '../api'

function daysRemaining(purgeAt: string): number {
  const ms = new Date(purgeAt).getTime() - Date.now()
  return Math.max(0, Math.ceil(ms / (1000 * 60 * 60 * 24)))
}

export function RecycleBinPanel({ libraryPath, onClose }: { libraryPath: string; onClose: () => void }) {
  const [items, setItems] = useState<DeletedItemDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<number | null>(null)

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
    }
  }

  return (
    <div className="recyclebin-panel">
      <div className="recyclebin-header">
        <h2>Recycle Bin</h2>
        <button type="button" onClick={onClose}>
          Close
        </button>
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
              </div>
              <p className="recyclebin-item-path" title={item.originalRelativePath}>
                {item.originalRelativePath}
              </p>
              <p className="recyclebin-item-meta">{daysRemaining(item.purgeAt)} day(s) left</p>
              <div className="recyclebin-item-actions">
                <button type="button" disabled={busyId === item.id} onClick={() => handleRestore(item.id)}>
                  Restore
                </button>
                <button
                  type="button"
                  className="danger"
                  disabled={busyId === item.id}
                  onClick={() => handlePurge(item.id)}
                >
                  Delete forever
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
