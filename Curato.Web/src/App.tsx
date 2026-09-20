import { useState } from 'react'
import './App.css'
import type { LibraryDto } from './api'
import { getLibrary, scanLibrary } from './api'
import { EventSection } from './components/EventSection'

function App() {
  const [path, setPath] = useState('')
  const [library, setLibrary] = useState<LibraryDto | null>(null)
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleScan() {
    if (!path.trim()) return
    setBusy(true)
    setError(null)
    setStatus('Scanning…')
    try {
      const result = await scanLibrary(path)
      setStatus(
        `Found ${result.mediaItemCount} items \u2014 ` +
          `${result.autoOrganizedEventCount} auto-organized event(s), ` +
          `${result.adoptedEventCount} adopted event(s), ` +
          `${result.tripGroupCount} trip(s).`,
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

  return (
    <main className="app-shell">
      <h1>Curato</h1>
      <p className="tagline">
        Point this at a folder to scan/organize it (PLAN.md Phase 1). No login yet
        &mdash; local testing only.
      </p>

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
