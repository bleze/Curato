import { useEffect, useState } from 'react'
import type { BrowseFoldersResponse } from '../api'
import { browseFolders } from '../api'

// Server-side filesystem browse, not a browser-native folder picker — Curato
// is self-hosted and the browser viewing it may not share a filesystem with
// the server, so a native picker couldn't hand back a path the server could
// actually open. See SettingsController.BrowseFolders for the endpoint.
export function FolderBrowser({
  startPath,
  onPick,
  onCancel,
}: {
  startPath: string
  onPick: (path: string) => void
  onCancel: () => void
}) {
  const [data, setData] = useState<BrowseFoldersResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  function load(path: string | null) {
    setError(null)
    browseFolders(path ?? undefined)
      .then(setData)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  useEffect(() => {
    load(startPath.trim() || null)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <div className="modal-overlay" onClick={onCancel}>
      <div className="modal-dialog folder-browser" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h2>Browse for a folder</h2>
        </div>
        <div className="modal-body">
          {error && <p className="error">{error}</p>}
          <p className="folder-browser-path">{data?.path ?? 'Drives'}</p>
          <ul className="folder-browser-list">
            {data?.path !== null && data?.path !== undefined && (
              <li>
                <button type="button" onClick={() => load(data.parentPath)}>
                  ..
                </button>
              </li>
            )}
            {data?.folders.map((folder) => (
              <li key={folder.path}>
                <button type="button" onClick={() => load(folder.path)}>
                  {folder.name}
                </button>
              </li>
            ))}
            {data && data.folders.length === 0 && <li className="folder-browser-empty">No subfolders</li>}
          </ul>
        </div>
        <div className="modal-footer">
          <button type="button" className="modal-footer-cancel" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className="modal-footer-save"
            disabled={!data?.path}
            onClick={() => data?.path && onPick(data.path)}
          >
            Select this folder
          </button>
        </div>
      </div>
    </div>
  )
}
