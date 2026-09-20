import { useRef, useState } from 'react'

export interface DateMarker {
  blockIndex: number
  label: string
  tooltipLabel: string
  isMajor: boolean
}

/**
 * A right-edge rail for jumping straight to a month/year without scrolling
 * through everything in between (Picasa/Photos-style date scrubber, §11).
 * Deliberately spaces markers evenly by *count*, not by each month's actual
 * rendered height (which varies with how many photos it has) — simpler, and
 * "roughly where in the timeline" is all this needs to convey.
 */
export function DateScrubber({
  markers,
  onJump,
}: {
  markers: DateMarker[]
  onJump: (blockIndex: number) => void
}) {
  const railRef = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<{ y: number; label: string } | null>(null)

  if (markers.length < 2) {
    return null
  }

  function jumpToClientY(clientY: number) {
    const rail = railRef.current
    if (!rail) return
    const rect = rail.getBoundingClientRect()
    const fraction = Math.min(1, Math.max(0, (clientY - rect.top) / rect.height))
    const index = Math.round(fraction * (markers.length - 1))
    const marker = markers[index]
    onJump(marker.blockIndex)
    setHover({ y: clientY - rect.top, label: marker.tooltipLabel })
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
      {markers
        .filter((m) => m.isMajor)
        .map((m) => (
          <div
            key={m.blockIndex}
            className="date-scrubber-tick"
            style={{ top: `${(markers.indexOf(m) / (markers.length - 1)) * 100}%` }}
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
