import { useEffect, useState } from 'react'
import './App.css'
import type { AuthStatus, LibraryDto, PicasaImportResult } from './api'
import {
  deleteMediaItem,
  getAuthStatus,
  getLibrary,
  logout,
  organizeLibrary,
  scanLibrary,
  setTakenAt,
  uploadFiles,
} from './api'
import { AlbumsPanel } from './components/AlbumsPanel'
import { AuthGate } from './components/AuthGate'
import { DuplicateFinderPanel } from './components/DuplicateFinderPanel'
import { LibraryGrid } from './components/LibraryGrid'
import { RecycleBinPanel } from './components/RecycleBinPanel'
import { SettingsPanel } from './components/SettingsPanel'

const LAST_PATH_KEY = 'curato:lastLibraryPath'

function rememberPath(path: string) {
  try {
    localStorage.setItem(LAST_PATH_KEY, path)
  } catch {
    // Private browsing / blocked storage — losing this is a minor
    // convenience hit, not worth surfacing to the user.
  }
}

function recallPath(): string | null {
  try {
    return localStorage.getItem(LAST_PATH_KEY)
  } catch {
    return null
  }
}

// Picasa import (§8) runs automatically on every scan/upload — no button to
// press, no separate step to remember. This only has anything to say when
// .picasa.ini files were actually found.
function formatPicasaSummary(result: PicasaImportResult): string {
  if (result.iniFilesFound === 0) return ''
  return (
    ` Picasa data: ${result.photosWithNewData} photo(s) newly imported ` +
    `(${result.facesImported} face(s), ${result.editsImported} edit(s), ${result.tagsApplied} tag(s)).`
  )
}

// Auto-Trip formation (a pure time-gap guess, no location signal) has been
// disabled — this only has anything to say the one time a library scanned
// before that change gets its leftover bogus Trips cleaned up.
function formatRemovedTripsNote(removedAutoTripGroupCount: number): string {
  if (removedAutoTripGroupCount === 0) return ''
  return ` Removed ${removedAutoTripGroupCount} old auto-generated trip grouping(s) that were never based on real location data.`
}

function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null)
  const [path, setPath] = useState('')
  const [library, setLibrary] = useState<LibraryDto | null>(null)
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [autoLoading, setAutoLoading] = useState(false)
  const [showRecycleBin, setShowRecycleBin] = useState(false)
  const [showDuplicates, setShowDuplicates] = useState(false)
  const [showAlbums, setShowAlbums] = useState(false)
  const [showSettings, setShowSettings] = useState(false)

  useEffect(() => {
    getAuthStatus()
      .then(setAuthStatus)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }, [])

  // Once logged in, silently re-load whatever library you last had open —
  // this is a read-only GET (no re-scan), so it's cheap and never touches
  // files, and it's why the grid used to show up empty until you retyped
  // the path and clicked Scan again.
  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return
    }

    const lastPath = recallPath()
    if (!lastPath) {
      return
    }

    setPath(lastPath)
    setAutoLoading(true)
    getLibrary(lastPath)
      .then((lib) => {
        setLibrary(lib)
        setLoadedPath(lastPath)
      })
      .catch(() => {
        // The folder may have moved/been unmounted since last time — fall
        // back to the empty "type a path" state rather than showing an
        // error for something that just happened automatically.
      })
      .finally(() => setAutoLoading(false))
  }, [authStatus?.isAuthenticated])

  async function handleScan() {
    if (!path.trim()) return
    setBusy(true)
    setError(null)
    setStatus('Scanning…')
    try {
      const result = await scanLibrary(path)
      setStatus(
        `${result.scan.mediaItemCount} items total \u2014 new this scan: ` +
          `${result.scan.newAutoOrganizedEventCount} auto-organized event(s), ` +
          `${result.scan.newAdoptedEventCount} adopted event(s), ` +
          `${result.scan.newTripGroupCount} trip(s), ` +
          `${result.scan.itemsAddedToExistingEvents} item(s) added to existing events.` +
          formatRemovedTripsNote(result.scan.removedAutoTripGroupCount) +
          formatPicasaSummary(result.picasaImport),
      )
      const lib = await getLibrary(path)
      setLibrary(lib)
      setLoadedPath(path)
      rememberPath(path)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleOrganize() {
    if (!loadedPath) return
    setBusy(true)
    setError(null)
    setStatus('Organizing…')
    try {
      const result = await organizeLibrary(loadedPath)
      setStatus(
        `Organize: ${result.eventsOrganized} event(s) arranged, ${result.filesMoved} file(s) moved.`,
      )
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleUpload(fileList: FileList | null) {
    if (!fileList || fileList.length === 0 || !loadedPath) return
    setBusy(true)
    setError(null)
    setStatus('Uploading…')
    try {
      const result = await uploadFiles(loadedPath, fileList)
      setStatus(
        `Uploaded ${result.filesSaved} file(s)` +
          (result.filesSkipped > 0 ? ` (${result.filesSkipped} skipped — unsupported type or too large)` : '') +
          `. ${result.scan.newAutoOrganizedEventCount} new event(s), ` +
          `${result.scan.itemsAddedToExistingEvents} item(s) added to existing events.` +
          formatRemovedTripsNote(result.scan.removedAutoTripGroupCount) +
          formatPicasaSummary(result.picasaImport),
      )
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleDelete(mediaItemId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      await deleteMediaItem(loadedPath, mediaItemId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleTakenAtChanged(mediaItemId: number, takenAt: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await setTakenAt(loadedPath, mediaItemId, takenAt)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleLogout() {
    await logout()
    setLibrary(null)
    setLoadedPath(null)
    setAuthStatus((prev) => (prev ? { ...prev, isAuthenticated: false, username: null } : prev))
  }

  if (!authStatus) {
    return (
      <main className="app-shell">
        <h1>Curato</h1>
      </main>
    )
  }

  if (authStatus.needsSetup || !authStatus.isAuthenticated) {
    return (
      <AuthGate
        needsSetup={authStatus.needsSetup}
        onSignedIn={() => getAuthStatus().then(setAuthStatus)}
      />
    )
  }

  return (
    <main className="app-shell">
      <header className="app-header">
        <h1>Curato</h1>
        <div className="account">
          {loadedPath && (
            <button type="button" onClick={() => setShowRecycleBin(true)}>
              Recycle Bin
            </button>
          )}
          {loadedPath && (
            <button type="button" onClick={() => setShowDuplicates(true)}>
              Find Duplicates
            </button>
          )}
          {loadedPath && (
            <button type="button" onClick={() => setShowAlbums(true)}>
              Albums
            </button>
          )}
          <button type="button" onClick={() => setShowSettings(true)}>
            Settings
          </button>
          <span>{authStatus.username}</span>
          <button type="button" onClick={handleLogout}>
            Sign out
          </button>
        </div>
      </header>
      <p className="tagline">Point this at a folder to scan/organize it (PLAN.md Phase 1).</p>

      <div className="scan-form">
        <input
          type="text"
          placeholder="C:/Photos or /volume1/Photos"
          value={path}
          onChange={(e) => setPath(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && handleScan()}
        />
        <button type="button" onClick={handleScan} disabled={busy || !path.trim()}>
          {busy ? 'Scanning\u2026' : 'Scan'}
        </button>
      </div>

      {autoLoading && <p className="status">Loading your library…</p>}
      {status && <p className="status">{status}</p>}
      {error && <p className="error">{error}</p>}

      {showRecycleBin && loadedPath && (
        <RecycleBinPanel libraryPath={loadedPath} onClose={() => setShowRecycleBin(false)} />
      )}

      {showDuplicates && loadedPath && (
        <DuplicateFinderPanel
          libraryPath={loadedPath}
          onClose={() => {
            setShowDuplicates(false)
            getLibrary(loadedPath).then(setLibrary)
          }}
        />
      )}

      {showAlbums && loadedPath && (
        <AlbumsPanel
          libraryPath={loadedPath}
          onClose={() => setShowAlbums(false)}
          onDelete={handleDelete}
          onTakenAtChanged={handleTakenAtChanged}
        />
      )}

      {showSettings && <SettingsPanel onClose={() => setShowSettings(false)} />}

      {library && loadedPath && (
        <div className="library">
          <div className="organize-bar">
            <button type="button" onClick={handleOrganize} disabled={busy}>
              {busy ? 'Working…' : 'Organize'}
            </button>
            <span className="organize-hint">
              Moves newly auto-organized items into their dated folders. Adopted folders are
              never touched.
            </span>
            <label className="upload-button">
              {busy ? 'Working…' : 'Add photos'}
              <input
                type="file"
                accept="image/*,video/*"
                multiple
                disabled={busy}
                onChange={(e) => {
                  handleUpload(e.target.files)
                  e.target.value = ''
                }}
              />
            </label>
          </div>
          <LibraryGrid
            library={library}
            libraryPath={loadedPath}
            onDelete={handleDelete}
            onTakenAtChanged={handleTakenAtChanged}
          />
        </div>
      )}
    </main>
  )
}

export default App
