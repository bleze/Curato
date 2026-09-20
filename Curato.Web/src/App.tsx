import { useEffect, useState } from 'react'
import './App.css'
import type { AuthStatus, LibraryDto } from './api'
import { getAuthStatus, getLibrary, logout, organizeLibrary, scanLibrary } from './api'
import { AuthGate } from './components/AuthGate'
import { EventSection } from './components/EventSection'

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
          </div>
          {library.trips.map((trip) => (
            <section className="trip" key={trip.id}>
              <h2>
                {trip.title}
                {trip.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
              </h2>
              {trip.proposedFolderPath && (
                <p className="proposed-path">Will move to: {trip.proposedFolderPath}</p>
              )}
              {trip.events.map((event) => (
                <EventSection key={event.id} event={event} libraryPath={loadedPath} />
              ))}
            </section>
          ))}

          {library.standaloneEvents.map((event) => (
            <EventSection key={event.id} event={event} libraryPath={loadedPath} />
          ))}

          {library.trips.length === 0 && library.standaloneEvents.length === 0 && (
            <p>No media found in this folder.</p>
          )}
        </div>
      )}
    </main>
  )
}

export default App
