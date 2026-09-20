import { useEffect, useState } from 'react'
import './App.css'
import type { AuthStatus, LibraryDto } from './api'
import { getAuthStatus, getLibrary, logout, scanLibrary } from './api'
import { AuthGate } from './components/AuthGate'
import { EventSection } from './components/EventSection'

function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null)
  const [path, setPath] = useState('')
  const [library, setLibrary] = useState<LibraryDto | null>(null)
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    getAuthStatus()
      .then(setAuthStatus)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }, [])

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

      {status && <p className="status">{status}</p>}
      {error && <p className="error">{error}</p>}

      {library && loadedPath && (
        <div className="library">
          {library.trips.map((trip) => (
            <section className="trip" key={trip.id}>
              <h2>
                {trip.title}
                {trip.namingMode === 'Adopted' && <span className="badge">Adopted</span>}
              </h2>
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
