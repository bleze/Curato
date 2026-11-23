import { useEffect, useState } from 'react'
import type { FavoriteCategoryDto, MediaItemDto, ResetTakenAtResult, TagDto } from '../api'
import { getTagItems, getTags, thumbnailUrl } from '../api'
import { TagGrid } from './TagGrid'

export function AlbumsPanel({
  libraryPath,
  onDelete,
  onTakenAtChanged,
  onResetTakenAt,
  onRotated,
  favoriteCategories,
  onSetFavoriteCategory,
}: {
  libraryPath: string
  onDelete: (mediaItemId: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  onResetTakenAt?: (mediaItemId: number) => Promise<ResetTakenAtResult | null>
  onRotated: (mediaItemId: number) => void
  favoriteCategories?: FavoriteCategoryDto[]
  onSetFavoriteCategory?: (mediaItemId: number, tagId: number | null) => void
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

  function handleTakenAtChanged(mediaItemId: number, takenAt: string) {
    onTakenAtChanged(mediaItemId, takenAt)
    setTagItems((items) =>
      items
        ? items.map((i) => (i.id === mediaItemId ? { ...i, takenAt, takenAtSource: 'Manual' } : i))
        : items,
    )
  }

  // Unlike handleTakenAtChanged above, the corrected value comes from the
  // file itself (server-side) rather than something already known here —
  // re-fetching this tag's items is simpler than plumbing the resolved
  // date/source back through just to patch local state by hand.
  async function handleResetTakenAt(mediaItemId: number) {
    const result = await onResetTakenAt?.(mediaItemId)
    if (selectedTag) {
      getTagItems(libraryPath, selectedTag.id)
        .then(setTagItems)
        .catch((err) => setError(err instanceof Error ? err.message : String(err)))
    }
    return result ?? null
  }

  function handleSetFavoriteCategory(mediaItemId: number, tagId: number | null) {
    onSetFavoriteCategory?.(mediaItemId, tagId)
    setTagItems((items) => {
      if (!items) return items
      // Viewing a favorite-shortcut tag whose id no longer matches the new
      // category means this item no longer belongs in this list at all —
      // matches handleDelete's identical "drop it from the open view"
      // pattern above, rather than leaving a stale tile whose star doesn't
      // match the tag it's supposedly filed under.
      if (selectedTag?.isFavoriteShortcut && tagId !== selectedTag.id) {
        return items.filter((i) => i.id !== mediaItemId)
      }
      return items.map((i) => (i.id === mediaItemId ? { ...i, favoriteCategoryId: tagId } : i))
    })
  }

  if (selectedTag) {
    return (
      <div className="library-tab-panel albums-panel">
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
          <TagGrid
            items={tagItems}
            libraryPath={libraryPath}
            onDelete={handleDelete}
            onTakenAtChanged={handleTakenAtChanged}
            onResetTakenAt={handleResetTakenAt}
            onRotated={onRotated}
            favoriteCategories={favoriteCategories}
            onSetFavoriteCategory={handleSetFavoriteCategory}
          />
        )}
      </div>
    )
  }

  return (
    <div className="library-tab-panel">
      <div className="recyclebin-header">
        <h2>Albums</h2>
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
