import { useEffect, useState } from 'react'
import type { MediaItemDto } from '../api'
import { fullImageUrl, rawFileUrl } from '../api'

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

  useEffect(() => {
    setPlayingLive(false)
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
      <div className="lightbox-content" onClick={(e) => e.stopPropagation()}>
        {item.kind === 'Video' ? (
          <video src={rawFileUrl(libraryPath, item.relativePath)} controls autoPlay />
        ) : item.liveVideoRelativePath && playingLive ? (
          <video src={rawFileUrl(libraryPath, item.liveVideoRelativePath)} autoPlay loop muted playsInline />
        ) : (
          <img src={fullImageUrl(libraryPath, item.relativePath)} alt="" />
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
        {item.relativePath} &middot; {index + 1} / {items.length}
      </div>
    </div>
  )
}
