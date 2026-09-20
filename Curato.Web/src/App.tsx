import { useEffect, useState } from 'react'
import './App.css'
import type { AuthStatus, LibraryDto } from './api'
import {
  deleteMediaItem,
  getAuthStatus,
  getLibrary,
  importPicasaData,
  logout,
  organizeLibrary,
  scanLibrary,
  uploadFiles,
} from './api'
import { AuthGate } from './components/AuthGate'
import { DuplicateFinderPanel } from './components/DuplicateFinderPanel'
import { LibraryGrid } from './components/LibraryGrid'
import { RecycleBinPanel } from './components/RecycleBinPanel'

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
        `${result.mediaItemCount} items total \u2014 new this scan: ` +
          `${result.newAutoOrganizedEventCount} auto-organized event(s), ` +
          `${result.newAdoptedEventCount} adopted event(s), ` +
          `${result.newTripGroupCount} trip(s), ` +
          `${result.itemsAddedToExistingEvents} item(s) added to existing events.`,
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
          `${result.scan.itemsAddedToExistingEvents} item(s) added to existing events.`,
      )
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handlePicasaImport() {
    if (!loadedPath) return
    setBusy(true)
    setError(null)
    setStatus('Importing legacy Picasa data…')
    try {
      const result = await importPicasaData(loadedPath)
      if (result.iniFilesFound === 0) {
        setStatus('No Picasa.ini files found under this library — nothing to import.')
      } else {
        setStatus(
          `Picasa import: ${result.iniFilesFound} Picasa.ini file(s) found, ` +
            `${result.photosMatched} photo(s) matched, ${result.photosWithNewData} newly imported. ` +
            `${result.facesImported} face(s) (${result.peopleMatched} named), ` +
            `${result.editsImported} edit(s), ${result.tagsApplied} tag(s) applied.` +
            (result.contactsFileFound ? '' : ' No contacts.xml found, so faces have no names yet.'),
        )
      }
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
            <button type="button" className="upload-button" onClick={handlePicasaImport} disabled={busy}>
              Import Picasa data
            </button>
          </div>
          <LibraryGrid library={library} libraryPath={loadedPath} onDelete={handleDelete} />
        </div>
      )}
    </main>
  )
}

export default App
