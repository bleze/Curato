import { useCallback, useEffect, useRef, useState } from 'react'
import type { MediaItemDto } from '../api'
import { fullImageUrl, rawFileUrl } from '../api'

const TRANSITION_MS = 800
const DURATION_OPTIONS = [3, 5, 8] as const
const DEFAULT_DURATION_SECONDS = 5
const CONTROLS_IDLE_MS = 2500

// A grab-bag of visually distinct transitions, picked at random each slide
// (never repeating the immediately-previous one) rather than the single
// crossfade+zoom this shipped with first — confirmed live as feeling flat/
// repetitive on anything longer than a handful of slides. Each name maps to
// its own CSS (App.css's "Slideshow transitions" section) via
// .slideshow-transition--{name}; SlideLayer itself has no idea what any of
// them actually do, it just applies the class and flips .is-visible.
const TRANSITIONS = ['zoom', 'pan-tl', 'pan-br', 'pan-tr', 'pan-bl', 'slide', 'punch', 'blur'] as const
type Transition = (typeof TRANSITIONS)[number]

function pickTransition(previous: Transition): Transition {
  let next = previous
  while (next === previous) {
    next = TRANSITIONS[Math.floor(Math.random() * TRANSITIONS.length)]
  }
  return next
}

/**
 * Fullscreen, hands-off playback of a fixed item list — reused from either
 * an Event/Trip header's own Slideshow button or the Lightbox's "start from
 * here" one, so it always operates over exactly the same items/order those
 * already show. Deliberately its own component rather than a mode bolted
 * onto Lightbox: none of Lightbox's editing affordances (zoom/pan/rotate/
 * move-to-event/date-edit) make sense mid-slideshow, and the crossfade
 * transitions need two overlapping layers Lightbox has no reason to carry.
 */
export function Slideshow({
  items,
  startIndex,
  libraryPath,
  onClose,
}: {
  items: MediaItemDto[]
  startIndex: number
  libraryPath: string
  onClose: () => void
}) {
  const [index, setIndex] = useState(startIndex)
  const [playing, setPlaying] = useState(true)
  const [durationSeconds, setDurationSeconds] = useState<number>(DEFAULT_DURATION_SECONDS)
  const [loop, setLoop] = useState(true)
  const [transition, setTransition] = useState<Transition>('zoom')
  // The just-left slide, kept mounted for TRANSITION_MS so its exit
  // animation overlaps the incoming slide's entrance instead of a hard cut.
  // Keyed by its own MediaItem id (not a manually incremented ref counter)
  // — goTo always changes to a different item, so the id alone is already
  // a stable, unique React key without reading a ref during render.
  const [outgoing, setOutgoing] = useState<MediaItemDto | null>(null)
  const [controlsVisible, setControlsVisible] = useState(true)
  const controlsTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const item = items[index]

  const goTo = useCallback(
    (rawNextIndex: number) => {
      if (rawNextIndex < 0) return
      let nextIndex = rawNextIndex
      if (nextIndex >= items.length) {
        if (!loop) {
          setPlaying(false)
          return
        }
        nextIndex = 0
      }
      setTransition((prev) => pickTransition(prev))
      setOutgoing(items[index])
      setIndex(nextIndex)
    },
    [index, items, loop],
  )

  useEffect(() => {
    if (!outgoing) return
    const t = setTimeout(() => setOutgoing(null), TRANSITION_MS)
    return () => clearTimeout(t)
  }, [outgoing])

  // Auto-advance timer for photos — a video advances on its own `ended`
  // event instead (see SlideLayer's onVideoEnded), so it always plays
  // through rather than getting cut off mid-clip by a fixed timer.
  useEffect(() => {
    if (!playing || !item || item.kind !== 'Photo') return
    const t = setTimeout(() => goTo(index + 1), durationSeconds * 1000)
    return () => clearTimeout(t)
  }, [playing, item, index, durationSeconds, goTo])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
      if (e.key === ' ') {
        e.preventDefault()
        setPlaying((p) => !p)
      }
      if (e.key === 'ArrowRight') goTo(index + 1)
      if (e.key === 'ArrowLeft') goTo(index - 1)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [index, goTo, onClose])

  const showControls = useCallback(() => {
    setControlsVisible(true)
    if (controlsTimerRef.current) clearTimeout(controlsTimerRef.current)
    controlsTimerRef.current = setTimeout(() => setControlsVisible(false), CONTROLS_IDLE_MS)
  }, [])

  useEffect(() => {
    showControls()
    return () => {
      if (controlsTimerRef.current) clearTimeout(controlsTimerRef.current)
    }
  }, [showControls])

  if (!item) return null

  return (
    // No click-to-close here (unlike Lightbox's background click) —
    // a slideshow is meant to be watched hands-off, so an accidental click
    // should just reveal the controls again, not abruptly end it.
    <div
      className="slideshow-overlay"
      onMouseMove={showControls}
      onClick={() => setPlaying((p) => !p)}
      style={{ '--slideshow-transition-ms': TRANSITION_MS } as React.CSSProperties}
    >
      {outgoing && (
        <SlideLayer
          key={`out-${outgoing.id}`}
          item={outgoing}
          libraryPath={libraryPath}
          role="outgoing"
          transition={transition}
          startVisible
          durationSeconds={durationSeconds}
        />
      )}
      <SlideLayer
        key={`in-${item.id}`}
        item={item}
        libraryPath={libraryPath}
        role="incoming"
        transition={transition}
        startVisible={false}
        durationSeconds={durationSeconds}
        playing={playing}
        onVideoEnded={() => goTo(index + 1)}
      />

      <div
        className={`slideshow-controls ${controlsVisible ? 'is-visible' : ''}`}
        onClick={(e) => e.stopPropagation()}
      >
        <button type="button" onClick={() => goTo(index - 1)} disabled={index === 0} aria-label="Previous">
          <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M15 5l-7 7 7 7z" /></svg>
        </button>
        <button type="button" onClick={() => setPlaying((p) => !p)} aria-label={playing ? 'Pause' : 'Play'}>
          {playing ? (
            <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M7 5h4v14H7zM13 5h4v14h-4z" /></svg>
          ) : (
            <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M8 5v14l11-7z" /></svg>
          )}
        </button>
        <button type="button" onClick={() => goTo(index + 1)} aria-label="Next">
          <svg viewBox="0 0 24 24" width="18" height="18" fill="currentColor"><path d="M9 5l7 7-7 7z" /></svg>
        </button>

        <div className="slideshow-duration">
          {DURATION_OPTIONS.map((seconds) => (
            <button
              key={seconds}
              type="button"
              className={durationSeconds === seconds ? 'is-active' : ''}
              onClick={() => setDurationSeconds(seconds)}
            >
              {seconds}s
            </button>
          ))}
        </div>

        <label className="slideshow-loop">
          <input type="checkbox" checked={loop} onChange={(e) => setLoop(e.target.checked)} />
          Loop
        </label>

        <span className="slideshow-progress">{index + 1} / {items.length}</span>

        <button type="button" className="slideshow-exit" onClick={onClose} aria-label="Exit slideshow">
          Exit
        </button>
      </div>
    </div>
  )
}

function SlideLayer({
  item,
  libraryPath,
  role,
  transition,
  startVisible,
  durationSeconds,
  playing,
  onVideoEnded,
}: {
  item: MediaItemDto
  libraryPath: string
  role: 'incoming' | 'outgoing'
  transition: Transition
  /** true = already showing, about to play its exit animation (the outgoing layer); false = hidden, about to play its entrance (the incoming layer). */
  startVisible: boolean
  durationSeconds: number
  playing?: boolean
  onVideoEnded?: () => void
}) {
  const [visible, setVisible] = useState(startVisible)
  const videoRef = useRef<HTMLVideoElement>(null)

  useEffect(() => {
    // The outgoing layer is already loaded and on screen — no need to wait
    // for anything, just start its exit animation next frame (same
    // one-frame-after-mount reasoning as below, so the CSS transition
    // actually animates instead of snapping straight to its end state).
    if (!startVisible) return
    const raf = requestAnimationFrame(() => setVisible(false))
    return () => cancelAnimationFrame(raf)
  }, [startVisible])

  // The incoming layer only reveals once its own image/video has actually
  // finished loading (onLoad/onLoadedData below) — confirmed live as a real
  // bug: revealing on a bare mount-timer let a still-downloading/decoding
  // slide fade in before it had any pixels to show, flashing plain black
  // (the overlay's own background showing through) for however long the
  // load took. onError also reveals rather than leaving the slide stuck
  // invisible forever if a file genuinely fails to load.

  // The <video> element's own `autoPlay` attribute only ever fires once, at
  // mount — toggling the Play/Pause button afterward has to drive an
  // already-playing video imperatively instead, or pausing would do nothing
  // visible for whatever slide happens to be a video.
  useEffect(() => {
    const video = videoRef.current
    if (!video) return
    if (playing === false) {
      video.pause()
    } else {
      video.play().catch(() => {
        // Autoplay-with-sound can be blocked before any user gesture on the
        // page — the slide still advances via onEnded/the timer either way.
      })
    }
  }, [playing])

  function handleReady() {
    if (startVisible || visible) return // outgoing layer, or already revealed — nothing to do
    requestAnimationFrame(() => setVisible(true))
  }

  return (
    <div
      className={`slideshow-slide slideshow-slide--${role} slideshow-transition--${transition} ${visible ? 'is-visible' : ''}`}
    >
      {item.kind === 'Video' ? (
        <video
          ref={videoRef}
          src={rawFileUrl(libraryPath, item.relativePath)}
          autoPlay
          onEnded={onVideoEnded}
          onLoadedData={handleReady}
          onError={handleReady}
          playsInline
        />
      ) : (
        <img
          src={fullImageUrl(libraryPath, item.relativePath, item.id)}
          alt=""
          className="slideshow-kenburns"
          onLoad={handleReady}
          onError={handleReady}
          style={{ '--slideshow-duration': `${durationSeconds + TRANSITION_MS / 1000}s` } as React.CSSProperties}
        />
      )}
    </div>
  )
}
