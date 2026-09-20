import { useEffect, useState } from 'react'
import type { DuplicateGroupDto } from '../api'
import { resolveDuplicateGroup, scanDuplicates, thumbnailUrl } from '../api'

export function DuplicateFinderPanel({ libraryPath, onClose }: { libraryPath: string; onClose: () => void }) {
  const [groups, setGroups] = useState<DuplicateGroupDto[] | null>(null)
  const [selectedKeep, setSelectedKeep] = useState<Record<number, number>>({})
  const [error, setError] = useState<string | null>(null)
  const [busyGroup, setBusyGroup] = useState<number | null>(null)

  function refresh() {
    setGroups(null)
    scanDuplicates(libraryPath)
      .then((result) => {
        setGroups(result)
        const defaults: Record<number, number> = {}
        result.forEach((g, i) => {
          defaults[i] = g.suggestedKeepId
        })
        setSelectedKeep(defaults)
      })
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  useEffect(refresh, [libraryPath])

  async function handleResolve(groupIndex: number) {
    const group = groups?.[groupIndex]
    if (!group) return
    const keepId = selectedKeep[groupIndex]
    const removeIds = group.items.map((i) => i.id).filter((id) => id !== keepId)

    setBusyGroup(groupIndex)
    try {
      await resolveDuplicateGroup(libraryPath, keepId, removeIds)
      setGroups((prev) => (prev ? prev.filter((_, i) => i !== groupIndex) : prev))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusyGroup(null)
    }
  }

  return (
    <div className="recyclebin-panel">
      <div className="recyclebin-header">
        <h2>Find Duplicates</h2>
        <button type="button" onClick={onClose}>
          Close
        </button>
      </div>
      <p className="recyclebin-hint">
        Exact byte-for-byte matches only. Pick which copy to keep, then remove the rest — removed
        copies go to the Recycle Bin, never deleted outright.
      </p>
      {error && <p className="error">{error}</p>}
      {groups === null && <p>Scanning for duplicates…</p>}
      {groups !== null && groups.length === 0 && <p>No exact duplicates found.</p>}
      {groups?.map((group, groupIndex) => (
        <div className="duplicate-group" key={groupIndex}>
          <div className="duplicate-group-items">
            {group.items.map((item) => (
              <label
                className={`duplicate-item ${selectedKeep[groupIndex] === item.id ? 'is-keep' : ''}`}
                key={item.id}
                title={item.relativePath}
              >
                <div className="thumb">
                  {item.kind === 'Photo' ? (
                    <img src={thumbnailUrl(libraryPath, item.relativePath)} alt="" loading="lazy" />
                  ) : (
                    <div className="thumb-fallback">Video</div>
                  )}
                </div>
                <input
                  type="radio"
                  name={`keep-${groupIndex}`}
                  checked={selectedKeep[groupIndex] === item.id}
                  onChange={() => setSelectedKeep((prev) => ({ ...prev, [groupIndex]: item.id }))}
                />
                <span className="duplicate-item-path">{item.relativePath}</span>
              </label>
            ))}
          </div>
          <button
            type="button"
            className="danger"
            disabled={busyGroup === groupIndex}
            onClick={() => handleResolve(groupIndex)}
          >
            Keep selected, recycle the rest
          </button>
        </div>
      ))}
    </div>
  )
}
