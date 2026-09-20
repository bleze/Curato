import { useEffect, useState } from 'react'
import type { MediaItemDto, TagDto } from '../api'
import { getTagItems, getTags, thumbnailUrl } from '../api'
import { TagGrid } from './TagGrid'

export function AlbumsPanel({
  libraryPath,
  onClose,
  onDelete,
}: {
  libraryPath: string
  onClose: () => void
  onDelete: (mediaItemId: number) => void
}) {
  const [tags, setTags] = useState<TagDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selectedTag, setSelectedTag] = useState<TagDto | null>(null)
  const [tagItems, setTagItems] = useState<MediaItemDto[] | null>(null)

  function refreshTags() {
    getTags(libraryPath)
      .then(setTags)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  useEffect(refreshTags, [libraryPath])

  function openTag(tag: TagDto) {
    setSelectedTag(tag)
    setTagItems(null)
    getTagItems(libraryPath, tag.id)
      .then(setTagItems)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  function handleDelete(mediaItemId: number) {
    onDelete(mediaItemId)
    setTagItems((items) => (items ? items.filter((i) => i.id !== mediaItemId) : items))
  }

  if (selectedTag) {
    return (
      <div className="recyclebin-panel albums-panel">
        <div className="recyclebin-header">
          <h2>
            {selectedTag.name}
            {selectedTag.isFavoriteShortcut && <span className="badge">Favorite</span>}
          </h2>
          <button
            type="button"
            onClick={() => {
              setSelectedTag(null)
              setTagItems(null)
              refreshTags()
            }}
          >
            Back to Albums
          </button>
        </div>
        {error && <p className="error">{error}</p>}
        {tagItems === null ? (
          <p>Loading…</p>
        ) : (
          <TagGrid items={tagItems} libraryPath={libraryPath} onDelete={handleDelete} />
        )}
      </div>
    )
  }

  return (
    <div className="recyclebin-panel">
      <div className="recyclebin-header">
        <h2>Albums</h2>
        <button type="button" onClick={onClose}>
          Close
        </button>
      </div>
      <p className="recyclebin-hint">
        Freeform tags and Picasa-imported favorites/albums (§9) — a photo can belong to any number
        of these regardless of which folder it lives in.
      </p>
      {error && <p className="error">{error}</p>}
      {tags === null && <p>Loading…</p>}
      {tags !== null && tags.length === 0 && <p>No albums yet.</p>}
      {tags && tags.length > 0 && (
        <div className="recyclebin-grid">
          {tags.map((tag) => (
            <button type="button" className="album-card" key={tag.id} onClick={() => openTag(tag)}>
              <div className="thumb">
                {tag.coverRelativePath ? (
                  <img src={thumbnailUrl(libraryPath, tag.coverRelativePath)} alt="" loading="lazy" />
                ) : (
                  <div className="thumb-fallback">No preview</div>
                )}
              </div>
              <p className="album-card-name">
                {tag.name}
                {tag.isFavoriteShortcut && <span className="badge">Favorite</span>}
              </p>
              <p className="recyclebin-item-meta">{tag.itemCount} item(s)</p>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
