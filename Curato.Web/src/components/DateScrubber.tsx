import { useRef, useState } from 'react'

export interface DateMarker {
  /** 0-1 position within the grid's total rendered height — see LibraryGrid's blockHeight/buildDateMarkers. */
  heightFraction: number
  label: string
  tooltipLabel: string
  isMajor: boolean
}

/**
 * A right-edge rail for jumping straight to a month/year without scrolling
 * through everything in between (Picasa/Photos-style date scrubber, §11).
 * Markers are spaced by actual cumulative rendered height (heightFraction),
 * not by counting distinct months — confirmed live as a real bug, not just
 * a cosmetic one: the native scrollbar right next to this rail moves
 * proportionally to real pixel height, so a "2025" label placed by naive
 * month-count would land nowhere near where dragging the native scrollbar
 * to that same visual position actually takes you, in a library where
 * different months have wildly different photo counts (and therefore
 * wildly different rendered heights).
 */
export function DateScrubber({
  markers,
  onJump,
  rightOffset,
  edgeInset,
}: {
  markers: DateMarker[]
  /** Fraction (0-1) of the grid's total height to jump to — see LibraryGrid's onJump wiring (virtualizer.scrollToOffset). */
  onJump: (fraction: number) => void
  /** Shifts the rail left of the real scrollbar (measured live) so the two never occupy the same pixels — see LibraryGrid.tsx. */
  rightOffset: number
  /**
   * A classic (non-overlay) scrollbar reserves real pixels at each end for
   * its arrow buttons, so its thumb's actual travel range is shorter than
   * the scrollbar's own full length. Confirmed *functionally*, not just by
   * eyeballing a screenshot (screenshots in this environment turned out to
   * be scaled ~1.2x relative to real CSS pixels, which had made an earlier
   * visual measurement unreliable): clicking right at the very top of the
   * real scrollbar moved scrollTop by exactly 40px (a line-scroll, i.e. an
   * arrow button was actually there), while a click ~12px further down
   * produced an ~840px jump (a page-scroll, i.e. blank track) — the
   * button's real height is small but genuine and was previously
   * unaccounted for, leaving a marker's label a real, visible distance from
   * where the native thumb actually reaches that content. LibraryGrid
   * already measures scrollbarWidth for exactly this purpose (0 on
   * overlay-scrollbar platforms — no arrow buttons either, so no inset
   * needed there either; ~15-17px on Windows, matching this measurement)
   * and passes it through as this prop.
   */
  edgeInset: number
}) {
  const railRef = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<{ y: number; label: string } | null>(null)

  if (markers.length < 2) {
    return null
  }

  // The marker whose month we're currently inside, for the hover tooltip
  // only — the actual jump target (below) no longer snaps to a marker at
  // all, since a plain proportional offset is exactly what makes this
  // agree with the native scrollbar's own (also proportional) behavior.
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
    const usableHeight = Math.max(1, rect.height - edgeInset * 2)
    const rawFraction = Math.min(1, Math.max(0, (clientY - rect.top - edgeInset) / usableHeight))

    // Magnetic snap onto a labeled (major) marker within ~14px, even though
    // the underlying proportional math is exactly correct — confirmed live
    // this was still needed: this rail is only 16px wide, compressing
    // 70,000+ pixels of real content into under a thousand, so landing on a
    // specific labeled year without it required genuinely sub-pixel mouse
    // precision. 14px roughly matches a tick label's own rendered height, so
    // clicking anywhere on the visible text reliably lands on what it says.
    const snapToleranceFraction = 14 / usableHeight
    let closestMajor: DateMarker | null = null
    let closestDistance = Infinity
    for (const marker of markers) {
      if (!marker.isMajor) continue
      const distance = Math.abs(marker.heightFraction - rawFraction)
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
    <div className="date-scrubber" ref={railRef} onMouseDown={handleMouseDown} style={{ right: rightOffset }}>
      {markers
        .filter((m) => m.isMajor)
        .map((m) => (
          <div
            key={m.label}
            className="date-scrubber-tick"
            style={{ top: `calc(${edgeInset}px + ${m.heightFraction} * (100% - ${edgeInset * 2}px))` }}
          >
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
