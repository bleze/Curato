import { useEffect, useRef, useState } from 'react'
import type { MediaItemDto } from '../api'
import { fullImageUrl, rawFileUrl, rotateMediaItem } from '../api'

const MIN_ZOOM = 1
const MAX_ZOOM = 4
const ZOOM_STEP = 0.4

function formatTakenAt(takenAt: string): string {
  return new Date(takenAt).toLocaleString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })
}

// <input type="datetime-local">'s value format ("YYYY-MM-DDTHH:mm", no
// timezone) — built from the Date object's LOCAL getters (not toISOString,
// which would convert to UTC) so the input shows the same wall-clock time
// formatTakenAt already displays, and round-trips back out the same way.
function toDatetimeLocalValue(takenAt: string): string {
  const d = new Date(takenAt)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export function Lightbox({
  items,
  index,
  libraryPath,
  onClose,
  onNavigate,
  onTakenAtChanged,
  onRotated,
}: {
  items: MediaItemDto[]
  index: number
  libraryPath: string
  onClose: () => void
  onNavigate: (index: number) => void
  onTakenAtChanged: (mediaItemId: number, takenAt: string) => void
  onRotated: (mediaItemId: number) => void
}) {
  const item = items[index]
  const [playingLive, setPlayingLive] = useState(false)
  const [zoom, setZoom] = useState(1)
  const [pan, setPan] = useState({ x: 0, y: 0 })
  const [dragging, setDragging] = useState(false)
  const [editingDate, setEditingDate] = useState(false)
  const [draftDate, setDraftDate] = useState('')
  const [rotating, setRotating] = useState(false)
  // Bumped after a successful rotate so the <img> below gets a new URL —
  // otherwise the browser keeps showing its cached response for the same
  // fullImageUrl even though the server now renders a rotated image for it
  // (PLAN.md §10's Edit pipeline changes rendered pixels, not the URL).
  const [imageCacheBust, setImageCacheBust] = useState(0)
  const dragStart = useRef<{ x: number; y: number; panX: number; panY: number; moved: boolean } | null>(null)

  useEffect(() => {
    setPlayingLive(false)
    setZoom(1)
    setPan({ x: 0, y: 0 })
    setEditingDate(false)
    setImageCacheBust(0)
  }, [index])

  async function handleRotate(deltaDegrees: number) {
    setRotating(true)
    try {
      await rotateMediaItem(libraryPath, item.id, deltaDegrees)
      setImageCacheBust((v) => v + 1)
      onRotated(item.id)
    } finally {
      setRotating(false)
    }
  }

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

  function clampZoom(z: number) {
    return Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, z))
  }

  function handleWheel(e: React.WheelEvent) {
    e.preventDefault()
    e.stopPropagation()
    setZoom((z) => {
      const next = clampZoom(z + (e.deltaY > 0 ? -ZOOM_STEP : ZOOM_STEP))
      if (next === MIN_ZOOM) setPan({ x: 0, y: 0 })
      return next
    })
  }

  function toggleZoom() {
    if (zoom > MIN_ZOOM) {
      setZoom(MIN_ZOOM)
      setPan({ x: 0, y: 0 })
    } else {
      setZoom(2)
    }
  }

  function handleMouseDown(e: React.MouseEvent) {
    e.stopPropagation()
    dragStart.current = { x: e.clientX, y: e.clientY, panX: pan.x, panY: pan.y, moved: false }
    if (zoom > MIN_ZOOM) setDragging(true)
  }

  function handleMouseMove(e: React.MouseEvent) {
    if (!dragStart.current) return
    const dx = e.clientX - dragStart.current.x
    const dy = e.clientY - dragStart.current.y
    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) dragStart.current.moved = true
    if (zoom > MIN_ZOOM) {
      setPan({ x: dragStart.current.panX + dx, y: dragStart.current.panY + dy })
    }
  }

  function endDrag() {
    // A plain click (mousedown+mouseup with no real movement) toggles zoom —
    // matches the zoom-in cursor shown on hover instead of requiring a
    // separate double-click gesture. A real drag (used to pan while already
    // zoomed in) must NOT also toggle zoom when the mouse is released.
    const wasClick = dragStart.current !== null && !dragStart.current.moved
    setDragging(false)
    dragStart.current = null
    if (wasClick) toggleZoom()
  }

  return (
    <div className="lightbox-overlay" onClick={onClose}>
      <button type="button" className="lightbox-close" onClick={onClose} aria-label="Close">
        &times;
      </button>
      {item.kind === 'Photo' && (
        <div className="lightbox-toolbar" onClick={(e) => e.stopPropagation()}>
          <button
            type="button"
            onClick={() => handleRotate(-90)}
            disabled={rotating}
            title="Rotate left — a saved edit, the original file is never touched"
            aria-label="Rotate left"
          >
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M1 4v6h6" />
              <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
            </svg>
          </button>
          <button
            type="button"
            onClick={() => handleRotate(90)}
            disabled={rotating}
            title="Rotate right — a saved edit, the original file is never touched"
            aria-label="Rotate right"
          >
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M23 4v6h-6" />
              <path d="M20.49 15a9 9 0 1 1-2.13-9.36L23 10" />
            </svg>
          </button>
        </div>
      )}
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
          &#8249;
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
          &#8250;
        </button>
      )}
      <div
        className="lightbox-content"
        onClick={(e) => e.stopPropagation()}
        onWheel={item.kind === 'Photo' ? handleWheel : undefined}
        onMouseMove={handleMouseMove}
        onMouseUp={endDrag}
        onMouseLeave={endDrag}
      >
        {item.kind === 'Video' ? (
          <video src={rawFileUrl(libraryPath, item.relativePath)} controls autoPlay />
        ) : item.liveVideoRelativePath && playingLive ? (
          <video src={rawFileUrl(libraryPath, item.liveVideoRelativePath)} autoPlay loop muted playsInline />
        ) : (
          <img
            src={fullImageUrl(libraryPath, item.relativePath, imageCacheBust)}
            alt=""
            onMouseDown={handleMouseDown}
            draggable={false}
            style={{
              transform: `scale(${zoom}) translate(${pan.x / zoom}px, ${pan.y / zoom}px)`,
              transition: dragging ? 'none' : 'transform 0.15s ease',
              cursor: zoom > MIN_ZOOM ? (dragging ? 'grabbing' : 'grab') : 'zoom-in',
            }}
          />
        )}
      </div>
      {item.liveVideoRelativePath && (
        <button
          type="button"
          className="lightbox-live-toggle"
          onClick={(e) => {
            e.stopPropagation()
            setPlayingLive((p) => !p)
          }}
        >
          {playingLive ? '◼ Stop' : '▶ Live'}
        </button>
      )}
      <div className="lightbox-caption" onClick={(e) => e.stopPropagation()}>
        {editingDate ? (
          <span className="lightbox-caption-edit">
            <input
              type="datetime-local"
              value={draftDate}
              onChange={(e) => setDraftDate(e.target.value)}
              autoFocus
            />
            <button
              type="button"
              onClick={() => {
                if (!draftDate) return
                onTakenAtChanged(item.id, draftDate)
                setEditingDate(false)
              }}
            >
              Save
            </button>
            <button type="button" onClick={() => setEditingDate(false)}>
              Cancel
            </button>
          </span>
        ) : (
          <>
            {formatTakenAt(item.takenAt)}
            {item.takenAtSource === 'FileSystem' && (
              <span className="lightbox-caption-hint" title="No Exif or video creation date was found — this is just the file's last-modified timestamp, which can be wrong (e.g. after a copy or download).">
                {' '}(file date, not confirmed)
              </span>
            )}
            {item.takenAtSource === 'ExifModifyDate' && (
              <span className="lightbox-caption-hint" title="The camera's original date tag was missing (common after sharing through iMessage/WhatsApp/some cloud re-encodes) — this is a less-authoritative Exif fallback, not necessarily wrong.">
                {' '}(exif fallback)
              </span>
            )}
            {item.takenAtSource === 'Manual' && (
              <span className="lightbox-caption-hint" title="Set by hand — automatic date detection found nothing usable for this file.">
                {' '}(manual)
              </span>
            )}{' '}
            <button
              type="button"
              className="lightbox-caption-edit-btn"
              title="Correct this date/time"
              onClick={() => {
                setDraftDate(toDatetimeLocalValue(item.takenAt))
                setEditingDate(true)
              }}
            >
              &#9998;
            </button>{' '}
            &middot; {item.relativePath} &middot; {index + 1} / {items.length}
          </>
        )}
      </div>
    </div>
  )
}
