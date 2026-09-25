import type { FavoriteCategoryDto } from '../api'

/**
 * The star button's picker — only shown when more than one favorite category
 * exists (a single category toggles directly, no menu needed). Reuses the
 * EventPicker's popup shell/styling since both are a short list of named
 * things you click to pick, opened the same way (LibraryGrid's
 * .event-picker-overlay).
 */
export function FavoriteCategoryMenu({
  categories,
  currentId,
  onPick,
  onCancel,
}: {
  categories: FavoriteCategoryDto[]
  currentId: number | null
  onPick: (tagId: number | null) => void
  onCancel: () => void
}) {
  return (
    <div className="move-to-event-picker" onClick={(e) => e.stopPropagation()}>
      <ul>
        <li>
          <button type="button" onClick={() => onPick(null)} disabled={currentId === null}>
            No category
          </button>
        </li>
        {categories.map((c) => (
          <li key={c.id}>
            <button type="button" onClick={() => onPick(c.id)} disabled={c.id === currentId}>
              {c.name}
            </button>
          </li>
        ))}
      </ul>
      <button type="button" className="move-to-event-cancel" onClick={onCancel}>
        Cancel
      </button>
    </div>
  )
}
