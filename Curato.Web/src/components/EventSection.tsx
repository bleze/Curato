import type { EventDto } from '../api'
import { thumbnailUrl } from '../api'

function formatRange(startAt: string, endAt: string): string {
  const start = new Date(startAt)
  const end = new Date(endAt)
  const sameDay = start.toDateString() === end.toDateString()
  const dateFmt: Intl.DateTimeFormatOptions = { year: 'numeric', month: 'short', day: 'numeric' }
  return sameDay
    ? start.toLocaleDateString(undefined, dateFmt)
    : `${start.toLocaleDateString(undefined, dateFmt)} – ${end.toLocaleDateString(undefined, dateFmt)}`
}

export function EventSection({
  event,
  libraryPath,
  onDelete,
}: {
  event: EventDto
  libraryPath: string
  onDelete: (mediaItemId: number) => void
}) {
  return (
    <section className="event-section">
      <header className="event-header">
        <h3>{event.title}</h3>
        <span className="event-meta">
          {formatRange(event.startAt, event.endAt)} &middot; {event.items.length} items
          {event.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
        </span>
      </header>
      {event.proposedFolderPath && (
        <p className="proposed-path">Will move to: {event.proposedFolderPath}</p>
      )}
      <div className="thumb-grid">
        {event.items.map((item) => (
          <div className="thumb" key={item.id} title={item.relativePath}>
            {item.kind === 'Photo' ? (
              <img
                src={thumbnailUrl(libraryPath, item.relativePath)}
                alt=""
                loading="lazy"
                onError={(e) => {
                  e.currentTarget.replaceWith(Object.assign(document.createElement('div'), {
                    className: 'thumb-fallback',
                    textContent: 'No preview',
                  }))
                }}
              />
            ) : (
              <div className="thumb-fallback">Video</div>
            )}
            <button
              type="button"
              className="thumb-delete"
              title="Move to Recycle Bin"
              onClick={() => onDelete(item.id)}
            >
              &times;
            </button>
          </div>
        ))}
      </div>
    </section>
  )
}
