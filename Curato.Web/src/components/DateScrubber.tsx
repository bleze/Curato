import { useRef, useState } from 'react'

export interface DateMarker {
  /** 0-1 position within the grid's scrollable range — see LibraryGrid's blockHeight/buildDateMarkers. */
  heightFraction: number
  label: string
  tooltipLabel: string
  isMajor: boolean
}

/**
 * A right-edge rail for jumping straight to a month/year without scrolling
 * through everything in between (Picasa/Photos-style date scrubber, §11).
 *
 * Renders its own scroll thumb instead of relying on the browser's native
 * one next to it — after three separate attempts to make a native
 * scrollbar's thumb line up with this rail's labels (matching its
 * proportional math, then its exact denominator, then its arrow-button
 * insets), each independently verified correct yet still reported as
 * visibly misaligned, the actual fix was to stop depending on a native
 * control whose exact pixel geometry the DOM can't query at all. Now the
 * thumb and the labels are computed by the same code from the same numbers,
 * so they can't help but agree — LibraryGrid hides the native scrollbar via
 * CSS and feeds this component the live scroll position instead.
 */
export function DateScrubber({
  markers,
  onJump,
  currentFraction,
  thumbSizeFraction,
}: {
  markers: DateMarker[]
  /** Fraction (0-1) of the grid's scrollable range to jump to — see LibraryGrid's onJump wiring (virtualizer.scrollToOffset). */
  onJump: (fraction: number) => void
  /** scrollTop / scrollableRange right now — where the thumb's top edge sits, before accounting for its own size. */
  currentFraction: number
  /** clientHeight / totalContentHeight — how much of the rail's length the thumb itself occupies. */
  thumbSizeFraction: number
}) {
  const railRef = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<{ y: number; label: string } | null>(null)

  if (markers.length < 2) {
    return null
  }

  // A marker's heightFraction and the thumb's currentFraction are the same
  // number when scrolled exactly to that marker (both are scrollTop /
  // scrollableRange) — but the thumb has a real size, so its usable *travel*
  // range is only (1 - thumbSizeFraction) of the rail's length (it can't
  // slide past having its own bottom edge reach the rail's bottom). Labels
  // need that same compression applied to land at the thumb's actual top
  // edge for that content, not at the raw uncompressed fraction.
  const travelFraction = Math.max(0, 1 - thumbSizeFraction)

  // The marker whose month we're currently inside, for the hover tooltip
  // only — the actual jump target (below) doesn't snap to a marker unless
  // the click is close enough to one (see the tolerance check below).
  function nearestMarker(fraction: number): DateMarker {
    let result = markers[0]
    for (const m of markers) {
      if (m.heightFraction <= fraction) result = m
      else break
    }
    return result
  }

  function jumpToClientY(clientY: number) {
    const rail = railRef.current
    if (!rail) return
    const rect = rail.getBoundingClientRect()
    const rawPosition = Math.min(1, Math.max(0, (clientY - rect.top) / rect.height))
    // Un-compress a click position back into a scroll fraction — the inverse
    // of how labels/the thumb are positioned below.
    const rawFraction = travelFraction > 0 ? Math.min(1, rawPosition / travelFraction) : 0

    // Magnetic snap onto a labeled (major) marker within ~14px, even though
    // the underlying proportional math is exactly correct — this rail is
    // only 16px wide, compressing 70,000+ pixels of real content into under
    // a thousand, so landing on a specific labeled year without it required
    // genuinely sub-pixel mouse precision. 14px roughly matches a tick
    // label's own rendered height, so clicking anywhere on the visible text
    // reliably lands on what it says.
    const snapToleranceFraction = 14 / rect.height
    let closestMajor: DateMarker | null = null
    let closestDistance = Infinity
    for (const marker of markers) {
      if (!marker.isMajor) continue
      const distance = Math.abs(marker.heightFraction * travelFraction - rawPosition)
      if (distance < closestDistance) {
        closestDistance = distance
        closestMajor = marker
      }
    }
    const fraction = closestMajor && closestDistance <= snapToleranceFraction ? closestMajor.heightFraction : rawFraction

    onJump(fraction)
    setHover({ y: clientY - rect.top, label: nearestMarker(fraction).tooltipLabel })
  }

  function handleMouseDown(e: React.MouseEvent) {
    jumpToClientY(e.clientY)
    function onMove(ev: MouseEvent) {
      jumpToClientY(ev.clientY)
    }
    function onUp() {
      window.removeEventListener('mousemove', onMove)
      window.removeEventListener('mouseup', onUp)
      setHover(null)
    }
    window.addEventListener('mousemove', onMove)
    window.addEventListener('mouseup', onUp)
  }

  return (
    <div className="date-scrubber" ref={railRef} onMouseDown={handleMouseDown}>
      <div
        className="date-scrubber-thumb"
        style={{
          top: `${currentFraction * travelFraction * 100}%`,
          height: `${thumbSizeFraction * 100}%`,
        }}
      />
      {markers
        .filter((m) => m.isMajor)
        .map((m) => (
          <div key={m.label} className="date-scrubber-tick" style={{ top: `${m.heightFraction * travelFraction * 100}%` }}>
            {m.label}
          </div>
        ))}
      {hover && (
        <div className="date-scrubber-tooltip" style={{ top: hover.y }}>
          {hover.label}
        </div>
      )}
    </div>
  )
}
