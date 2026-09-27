import { useEffect, useRef, useState } from 'react'
import './App.css'
import type { AuthStatus, FavoriteCategoryDto, LibraryDto, PicasaImportResult } from './api'
import {
  createTripGroup,
  deleteMediaItem,
  getAuthStatus,
  getFavoriteCategories,
  getLibrary,
  getLibraryFolder,
  logout,
  mergeEventInto,
  moveMediaItemToEvent,
  organizeLibrary,
  removeEventFromTrip,
  renameEvent,
  renameTripGroup,
  scanLibrary,
  setMediaItemFavoriteCategory,
  setTakenAt,
  ungroupTripGroup,
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

function formatRemovedBareYearTripsNote(removedBareYearTripGroupCount: number): string {
  if (removedBareYearTripGroupCount === 0) return ''
  return ` Ungrouped ${removedBareYearTripGroupCount} fictitious year-folder "trip(s)" back into standalone events.`
}

function formatRemovedEmptyEventsNote(removedEmptyEventCount: number): string {
  if (removedEmptyEventCount === 0) return ''
  return ` Removed ${removedEmptyEventCount} empty event(s) left over from an earlier delete.`
}

function formatRemovedMissingFilesNote(removedMissingFileCount: number): string {
  if (removedMissingFileCount === 0) return ''
  return ` Removed ${removedMissingFileCount} item(s) whose file was deleted on disk.`
}

// proposedFolderPath (set on an Event/TripGroup) is exactly the dry-run
// preview Organize would act on — the same field LibraryGrid already reads
// to show each "Will move to: ..." pill. Nothing carrying one means a real
// Organize run right now would move zero files, so the bar offering to run
// it has nothing to offer.
function hasPendingOrganize(library: LibraryDto): boolean {
  return (
    library.standaloneEvents.some((event) => event.proposedFolderPath !== null) ||
    library.trips.some(
      (trip) => trip.proposedFolderPath !== null || trip.events.some((event) => event.proposedFolderPath !== null),
    )
  )
}

function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null)
  const [path, setPath] = useState('')
  const [library, setLibrary] = useState<LibraryDto | null>(null)
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  const [favoriteCategories, setFavoriteCategories] = useState<FavoriteCategoryDto[]>([])
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [scanning, setScanning] = useState(false)
  const scanAbortRef = useRef<AbortController | null>(null)
  const [autoLoading, setAutoLoading] = useState(false)
  // Library/Albums/Recycle Bin/Persons are mutually-exclusive views of the
  // same main content area (a toolbar toggle, not stacked dismissable
  // panels) — Find Duplicates and Settings stay real overlays below since
  // they're one-off tools/actions, not browsable views of the library's
  // contents. Persons has no browsing feature behind it yet (no API, no
  // grid) — it's a placeholder marking where that lands once it's built.
  const [activeView, setActiveView] = useState<'library' | 'albums' | 'recycleBin' | 'persons'>('library')
  const [showDuplicates, setShowDuplicates] = useState(false)
  const [showSettings, setShowSettings] = useState(false)

  useEffect(() => {
    getAuthStatus()
      .then(setAuthStatus)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }, [])

  // Once logged in, silently load a library without any manual step — this
  // is a read-only GET (no re-scan), so it's cheap and never touches files.
  // The configured "Library Folder" (Settings) wins when set: it's a
  // server-side setting shared across every browser/device, and if you've
  // set one up (e.g. for iCloud sync) it's clearly "the" library you care
  // about — synced content should just show up, not require separately
  // remembering to open it. Falls back to this browser's own last-scanned
  // path (localStorage) for the plain manual-folder workflow, when no
  // Library Folder is configured at all.
  useEffect(() => {
    if (!authStatus?.isAuthenticated) {
      return
    }

    setAutoLoading(true)
    getLibraryFolder()
      .then((configuredPath) => configuredPath ?? recallPath())
      .then((pathToLoad) => {
        if (!pathToLoad) {
          return
        }

        setPath(pathToLoad)
        return getLibrary(pathToLoad).then((lib) => {
          setLibrary(lib)
          setLoadedPath(pathToLoad)
          rememberPath(pathToLoad)
        })
      })
      .catch(() => {
        // The folder may have moved/been unmounted since last time — fall
        // back to the empty "type a path" state rather than showing an
        // error for something that just happened automatically.
      })
      .finally(() => setAutoLoading(false))
  }, [authStatus?.isAuthenticated])

  // Favorite categories change rarely compared to the library itself, so
  // this only re-fetches when the open library actually changes, not on
  // every library mutation — Settings calls onFavoriteCategoriesChanged
  // directly after an add/delete instead, the same pattern
  // refreshLibraryIfCurrentlyOpen below uses for Settings-triggered syncs.
  useEffect(() => {
    if (!loadedPath) {
      setFavoriteCategories([])
      return
    }

    getFavoriteCategories(loadedPath)
      .then(setFavoriteCategories)
      .catch(() => {
        // Best-effort — the star button just hides itself with zero categories.
      })
  }, [loadedPath])

  function refreshFavoriteCategoriesIfCurrentlyOpen(path: string) {
    if (!loadedPath || loadedPath.toLowerCase() !== path.toLowerCase()) {
      return
    }

    getFavoriteCategories(path)
      .then(setFavoriteCategories)
      .catch(() => {
        // Best-effort, same reasoning as refreshLibraryIfCurrentlyOpen below.
      })
  }

  // Silently re-fetches the currently-open library if it's the one that
  // just changed — for after a Settings-triggered "Sync now" completes.
  // Confirmed live this was needed: a sync run from Settings updates the
  // library folder's database, but the main grid (if already loaded before
  // the sync started) has no way to know that happened on its own, so new
  // content stayed invisible until a manual hard refresh. Deliberately a
  // no-op if a different library is currently open — never surprise-swap
  // what's on screen out from under whatever you're actually looking at.
  function refreshLibraryIfCurrentlyOpen(path: string) {
    if (!loadedPath || loadedPath.toLowerCase() !== path.toLowerCase()) {
      return
    }

    getLibrary(path)
      .then(setLibrary)
      .catch(() => {
        // Best-effort — the sync's own success/error status already
        // reported the result; a failed silent refresh isn't worth
        // surfacing as a separate error.
      })
  }

  // Just views a library, without re-scanning — for a folder that's already
  // indexed (e.g. an iCloud sync already ran ScanService server-side; there's
  // nothing to gain from re-scanning just to look at it). Same shape as the
  // auto-load-on-login effect above.
  async function handleOpenLibrary(pathToOpen: string) {
    setBusy(true)
    setError(null)
    try {
      const lib = await getLibrary(pathToOpen)
      setLibrary(lib)
      setLoadedPath(pathToOpen)
      setPath(pathToOpen)
      rememberPath(pathToOpen)
      setShowSettings(false)
      setActiveView('library')
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  async function handleScan() {
    if (!path.trim()) return
    const controller = new AbortController()
    scanAbortRef.current = controller
    setBusy(true)
    setScanning(true)
    setError(null)
    setStatus('Scanning…')
    try {
      const result = await scanLibrary(path, controller.signal)
      setStatus(
        `${result.scan.mediaItemCount} items total \u2014 new this scan: ` +
          `${result.scan.newAutoOrganizedEventCount} auto-organized event(s), ` +
          `${result.scan.newAdoptedEventCount} adopted event(s), ` +
          `${result.scan.newTripGroupCount} trip(s), ` +
          `${result.scan.itemsAddedToExistingEvents} item(s) added to existing events.` +
          formatRemovedBareYearTripsNote(result.scan.removedBareYearTripGroupCount) +
          formatRemovedEmptyEventsNote(result.scan.removedEmptyEventCount) +
          formatRemovedMissingFilesNote(result.scan.removedMissingFileCount) +
          formatPicasaSummary(result.picasaImport),
      )
      const lib = await getLibrary(path)
      setLibrary(lib)
      setLoadedPath(path)
      rememberPath(path)
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        // The server has no way to actually stop mid-walk (ScanService.Scan
        // is one synchronous pass with nothing to check a token against) —
        // this only stops the tab from waiting on it. Safe either way since
        // re-scanning is idempotent, so whatever the server finishes with
        // just gets picked up cleanly next time.
        setStatus('Scan cancelled — it may still be finishing on the server in the background.')
      } else {
        setError(err instanceof Error ? err.message : String(err))
      }
    } finally {
      setBusy(false)
      setScanning(false)
      scanAbortRef.current = null
    }
  }

  function handleCancelScan() {
    scanAbortRef.current?.abort()
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
          formatRemovedBareYearTripsNote(result.scan.removedBareYearTripGroupCount) +
          formatRemovedEmptyEventsNote(result.scan.removedEmptyEventCount) +
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

  // The actual rotate call already happened inside Lightbox (it needs the
  // result immediately, to bust its own <img>'s cache) — this just keeps
  // the rest of the app's data fresh afterward, same as the other handlers.
  async function handleRotated() {
    if (!loadedPath) return
    const lib = await getLibrary(loadedPath)
    setLibrary(lib)
  }

  async function handleRenameEvent(eventId: number, title: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await renameEvent(loadedPath, eventId, title)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleRenameTrip(tripGroupId: number, title: string) {
    if (!loadedPath) return
    setError(null)
    try {
      await renameTripGroup(loadedPath, tripGroupId, title)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleMoveMediaItemToEvent(mediaItemId: number, targetEventId: number | null) {
    if (!loadedPath) return
    setError(null)
    try {
      await moveMediaItemToEvent(loadedPath, mediaItemId, targetEventId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleSetFavoriteCategory(mediaItemId: number, tagId: number | null) {
    if (!loadedPath) return
    setError(null)
    try {
      await setMediaItemFavoriteCategory(loadedPath, mediaItemId, tagId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleCreateTripGroup(eventIds: number[]) {
    if (!loadedPath) return
    setError(null)
    try {
      await createTripGroup(loadedPath, eventIds)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  async function handleUngroupTripGroup(tripGroupId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      await ungroupTripGroup(loadedPath, tripGroupId)
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  // Sequential, not fired-off-in-parallel — same reasoning as
  // handleMergeEventsInto just below.
  async function handleRemoveEventsFromTrip(eventIds: number[]) {
    if (!loadedPath) return
    setError(null)
    try {
      for (const eventId of eventIds) {
        await removeEventFromTrip(loadedPath, eventId)
      }
      const lib = await getLibrary(loadedPath)
      setLibrary(lib)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  // Sequential, not fired-off-in-parallel: several merges hitting the same
  // library.db concurrently risked overlapping write transactions, and this
  // also means one final library refresh instead of one per source Event.
  async function handleMergeEventsInto(sourceEventIds: number[], targetEventId: number) {
    if (!loadedPath) return
    setError(null)
    try {
      for (const sourceEventId of sourceEventIds) {
        await mergeEventInto(loadedPath, sourceEventId, targetEventId)
      }
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
        <div className="app-brand">
          <img src="/favicon.svg" alt="" className="app-logo" width={40} height={40} />
          <h1>Curato</h1>
        </div>
        <div className="account">
          {loadedPath && (
            <div className="view-switcher">
              <button
                type="button"
                className={activeView === 'library' ? 'is-active' : ''}
                onClick={() => setActiveView('library')}
              >
                Library
              </button>
              <button
                type="button"
                className={activeView === 'albums' ? 'is-active' : ''}
                onClick={() => setActiveView('albums')}
              >
                Albums
              </button>
              <button
                type="button"
                className={activeView === 'persons' ? 'is-active' : ''}
                onClick={() => setActiveView('persons')}
              >
                Persons
              </button>
              <button
                type="button"
                className={activeView === 'recycleBin' ? 'is-active' : ''}
                onClick={() => setActiveView('recycleBin')}
              >
                Recycle Bin
              </button>
            </div>
          )}
          {loadedPath && (
            <button type="button" onClick={() => setShowDuplicates(true)}>
              Find Duplicates
            </button>
          )}
          {loadedPath && (
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
          )}
          <button
            type="button"
            onClick={handleScan}
            disabled={busy || !path.trim()}
            title={path ? `Re-scan ${path} for new/changed files` : 'Set a library folder in Settings first'}
          >
            {busy ? 'Scanning…' : 'Scan'}
          </button>
          <button type="button" onClick={() => setShowSettings(true)}>
            Settings
          </button>
          <span>{authStatus.username}</span>
          <button type="button" onClick={handleLogout}>
            Sign out
          </button>
        </div>
      </header>

      {scanning && (
        <div className="scan-progress-bar">
          <span className="scan-progress-label">Scanning…</span>
          <div className="scan-progress-track">
            <div className="scan-progress-fill" />
          </div>
          <button type="button" onClick={handleCancelScan}>
            Cancel
          </button>
        </div>
      )}

      {!loadedPath && !autoLoading && (
        <p className="tagline">No library folder configured yet — open Settings to point Curato at your photos.</p>
      )}

      {autoLoading && <p className="status">Loading your library…</p>}
      {status && <p className="status">{status}</p>}
      {error && <p className="error">{error}</p>}

      {showDuplicates && loadedPath && (
        <DuplicateFinderPanel
          libraryPath={loadedPath}
          onClose={() => {
            setShowDuplicates(false)
            getLibrary(loadedPath).then(setLibrary)
          }}
        />
      )}

      {showSettings && (
        <SettingsPanel
          onClose={() => setShowSettings(false)}
          onOpenLibrary={handleOpenLibrary}
          onLibraryContentChanged={refreshLibraryIfCurrentlyOpen}
          onFavoriteCategoriesChanged={refreshFavoriteCategoriesIfCurrentlyOpen}
        />
      )}

      {loadedPath && activeView === 'recycleBin' && (
        <div className="library">
          <RecycleBinPanel libraryPath={loadedPath} />
        </div>
      )}

      {loadedPath && activeView === 'albums' && (
        <div className="library">
          <AlbumsPanel
            libraryPath={loadedPath}
            onDelete={handleDelete}
            onTakenAtChanged={handleTakenAtChanged}
            onRotated={handleRotated}
          />
        </div>
      )}

      {loadedPath && activeView === 'persons' && (
        <div className="library">
          <div className="library-tab-panel">
            <div className="recyclebin-header">
              <h2>Persons</h2>
            </div>
            <p className="recyclebin-hint">Browsing photos by person isn't built yet — coming later.</p>
          </div>
        </div>
      )}

      {library && loadedPath && activeView === 'library' && (
        <div className="library">
          {hasPendingOrganize(library) && (
            <div className="organize-bar">
              <button type="button" onClick={handleOrganize} disabled={busy}>
                {busy ? 'Working…' : 'Organize'}
              </button>
              <span className="organize-hint">
                Moves newly auto-organized items into their dated folders. Adopted folders are
                never touched.
              </span>
            </div>
          )}
          <LibraryGrid
            library={library}
            libraryPath={loadedPath}
            favoriteCategories={favoriteCategories}
            onDelete={handleDelete}
            onTakenAtChanged={handleTakenAtChanged}
            onRotated={handleRotated}
            onRenameEvent={handleRenameEvent}
            onRenameTrip={handleRenameTrip}
            onMoveMediaItemToEvent={handleMoveMediaItemToEvent}
            onCreateTripGroup={handleCreateTripGroup}
            onUngroupTripGroup={handleUngroupTripGroup}
            onMergeEventsInto={handleMergeEventsInto}
            onRemoveEventsFromTrip={handleRemoveEventsFromTrip}
            onSetFavoriteCategory={handleSetFavoriteCategory}
          />
        </div>
      )}
    </main>
  )
}

export default App
