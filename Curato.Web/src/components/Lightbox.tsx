import { useEffect, useRef, useState } from 'react'
import type { MediaItemDto } from '../api'
import { fullImageUrl, rawFileUrl } from '../api'

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

export function Lightbox({
  items,
  index,
  libraryPath,
  onClose,
  onNavigate,
}: {
  items: MediaItemDto[]
  index: number
  libraryPath: string
  onClose: () => void
  onNavigate: (index: number) => void
}) {
  const item = items[index]
  const [playingLive, setPlayingLive] = useState(false)
  const [zoom, setZoom] = useState(1)
  const [pan, setPan] = useState({ x: 0, y: 0 })
  const [dragging, setDragging] = useState(false)
  const dragStart = useRef<{ x: number; y: number; panX: number; panY: number } | null>(null)

  useEffect(() => {
    setPlayingLive(false)
    setZoom(1)
    setPan({ x: 0, y: 0 })
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

  function toggleZoom(e: React.MouseEvent) {
    e.stopPropagation()
    if (zoom > MIN_ZOOM) {
      setZoom(MIN_ZOOM)
      setPan({ x: 0, y: 0 })
    } else {
      setZoom(2)
    }
  }

  function handleMouseDown(e: React.MouseEvent) {
    if (zoom <= MIN_ZOOM) return
    e.stopPropagation()
    setDragging(true)
    dragStart.current = { x: e.clientX, y: e.clientY, panX: pan.x, panY: pan.y }
  }

  function handleMouseMove(e: React.MouseEvent) {
    if (!dragStart.current) return
    setPan({
      x: dragStart.current.panX + (e.clientX - dragStart.current.x),
      y: dragStart.current.panY + (e.clientY - dragStart.current.y),
    })
  }

  function endDrag() {
    setDragging(false)
    dragStart.current = null
  }

  return (
    <div className="lightbox-overlay" onClick={onClose}>
      <button type="button" className="lightbox-close" onClick={onClose} aria-label="Close">
        &times;
      </button>
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
            src={fullImageUrl(libraryPath, item.relativePath)}
            alt=""
            onDoubleClick={toggleZoom}
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
      <div className="lightbox-caption">
        {formatTakenAt(item.takenAt)} &middot; {item.relativePath} &middot; {index + 1} / {items.length}
      </div>
    </div>
  )
}
