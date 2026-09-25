import { useEffect, useState } from 'react'
import type { iCloudSyncStatus } from '../api'
import {
  getHomeLocation,
  getICloudSyncStatus,
  getLibraryFolder,
  iCloudCancelLogin,
  iCloudDisconnect,
  iCloudLogin,
  iCloudSyncNow,
  iCloudUpdateSettings,
  iCloudVerifyTwoFactor,
  setHomeLocation,
  setLibraryFolder,
} from '../api'
import { MAX_THUMB_MIN_WIDTH, MIN_THUMB_MIN_WIDTH, setThumbMinWidth, useThumbMinWidth } from '../thumbSize'
import { setUse24HourTime, useUse24HourTime } from '../timeFormat'

export function SettingsPanel({
  onClose,
  onOpenLibrary,
  onLibraryContentChanged,
}: {
  onClose: () => void
  onOpenLibrary: (path: string) => void
  onLibraryContentChanged: (path: string) => void
}) {
  const [activeTab, setActiveTab] = useState<'library' | 'icloud' | 'display'>('library')
  const use24Hour = useUse24HourTime()
  const thumbMinWidth = useThumbMinWidth()
  const [libraryFolder, setLibraryFolderValue] = useState<string | null>(null)
  const [libraryFolderInput, setLibraryFolderInput] = useState('')
  const [homeLocationInput, setHomeLocationInput] = useState('')
  const [status, setStatus] = useState<iCloudSyncStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [awaitingCode, setAwaitingCode] = useState(false)
  const [code, setCode] = useState('')
  const [lookbackInput, setLookbackInput] = useState('')
  const [sinceDateInput, setSinceDateInput] = useState('')
  const [recentCapInput, setRecentCapInput] = useState('')
  const [syncing, setSyncing] = useState(false)

  function refresh() {
    getLibraryFolder().then((path) => {
      setLibraryFolderValue(path)
      setLibraryFolderInput((current) => current || path || '')
    })
    getHomeLocation().then((label) => {
      setHomeLocationInput((current) => current || label || '')
    })
    getICloudSyncStatus()
      .then((s) => {
        setStatus(s)
        if (s.lookbackDays !== null) {
          setLookbackInput((current) => current || String(s.lookbackDays))
        }
        if (s.sinceDate !== null) {
          setSinceDateInput((current) => current || s.sinceDate!)
        }
        if (s.recentItemCap !== null) {
          setRecentCapInput((current) => current || String(s.recentItemCap))
        }
      })
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  useEffect(refresh, [])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  // "Sync now" is a single long-running request with no progress in its
  // own response — poll the status endpoint on the side while it's in
  // flight so CurrentActivity (icloudpd's latest output line) shows up
  // live instead of the UI looking identical whether it's working or stuck.
  useEffect(() => {
    if (!syncing) return
    const interval = setInterval(() => {
      getICloudSyncStatus()
        .then(setStatus)
        .catch(() => {
          // Transient poll failure — the main sync-now call's own error
          // handling covers real failures, so this is safe to ignore.
        })
    }, 1500)
    return () => clearInterval(interval)
  }, [syncing])

  async function runBusy(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  // Also opens it immediately (which closes this panel) rather than just
  // persisting the setting and leaving you to separately find and click an
  // "Open this library" button to actually see it — that extra step never
  // did anything Save itself couldn't just do.
  function handleSaveLibraryFolder() {
    return runBusy(async () => {
      await setLibraryFolder(libraryFolderInput)
      onOpenLibrary(libraryFolderInput)
    })
  }

  function handleSaveHomeLocation() {
    return runBusy(() => setHomeLocation(homeLocationInput))
  }

  function handleICloudLogin() {
    return runBusy(async () => {
      const result = await iCloudLogin(username, password)
      if (result.error) {
        setError(result.error)
      } else if (result.requiresTwoFactor) {
        // The server keeps the live session alive for the verify step below —
        // the password doesn't need to (and shouldn't) stick around here.
        setPassword('')
        setAwaitingCode(true)
      } else {
        setUsername('')
        setPassword('')
        refresh()
      }
    })
  }

  function handleICloudVerify() {
    return runBusy(async () => {
      await iCloudVerifyTwoFactor(username, code)
      setAwaitingCode(false)
      setUsername('')
      setCode('')
      refresh()
    })
  }

  function handleCancelLogin() {
    return runBusy(async () => {
      await iCloudCancelLogin()
      setAwaitingCode(false)
      setUsername('')
      setPassword('')
      setCode('')
    })
  }

  function handleSyncNow() {
    setSyncing(true)
    return runBusy(async () => {
      try {
        const result = await iCloudSyncNow()
        if (!result.success) {
          setError(result.errorMessage ?? 'Sync failed.')
        } else if (libraryFolder) {
          onLibraryContentChanged(libraryFolder)
        }
      } finally {
        setSyncing(false)
      }
      refresh()
    })
  }

  function handleToggleEnabled() {
    if (!status) return Promise.resolve()
    return runBusy(async () => {
      await iCloudUpdateSettings(
        status.intervalMinutes ?? 360,
        !status.enabled,
        status.lookbackDays ?? 365,
        status.sinceDate,
        status.recentItemCap ?? 2000,
      )
      refresh()
    })
  }

  function handleSaveScope() {
    if (!status) return Promise.resolve()

    const days = Number.parseInt(lookbackInput, 10)
    if (!sinceDateInput && (!Number.isFinite(days) || days <= 0)) {
      setError('Enter a positive number of days, or pick a specific date.')
      return Promise.resolve()
    }

    const cap = Number.parseInt(recentCapInput, 10)
    if (!Number.isFinite(cap) || cap <= 0) {
      setError('Enter a positive number for the max files per sync.')
      return Promise.resolve()
    }

    return runBusy(async () => {
      await iCloudUpdateSettings(
        status.intervalMinutes ?? 360,
        status.enabled ?? true,
        days,
        sinceDateInput || null,
        cap,
      )
      refresh()
    })
  }

  function handleClearSinceDate() {
    setSinceDateInput('')
  }

  function handleDisconnect() {
    return runBusy(async () => {
      await iCloudDisconnect()
      setAwaitingCode(false)
      refresh()
    })
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-dialog" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h2>Settings</h2>
          <button type="button" onClick={onClose}>
            Close
          </button>
        </div>
        <div className="modal-tabs">
          <button
            type="button"
            className={`modal-tab ${activeTab === 'library' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('library')}
          >
            Library Folder
          </button>
          <button
            type="button"
            className={`modal-tab ${activeTab === 'icloud' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('icloud')}
          >
            iCloud
          </button>
          <button
            type="button"
            className={`modal-tab ${activeTab === 'display' ? 'is-active' : ''}`}
            onClick={() => setActiveTab('display')}
          >
            Display
          </button>
        </div>
        <div className="modal-body">
          {error && <p className="error">{error}</p>}

          {activeTab === 'library' && (
            <section className="settings-section">
              <p className="recyclebin-hint">
                The destination iCloud sync (and any future external source) copies content into. Scanning/organizing
                a folder you point Curato at directly still works exactly as before — this is a separate, additional
                setting.
              </p>
              <div className="settings-row">
                <input
                  type="text"
                  placeholder="D:/Photos or /volume1/Photos"
                  value={libraryFolderInput}
                  onChange={(e) => setLibraryFolderInput(e.target.value)}
                />
                <button type="button" onClick={handleSaveLibraryFolder} disabled={busy || !libraryFolderInput.trim()}>
                  Save
                </button>
              </div>
              {libraryFolder && <p className="settings-meta">Current: {libraryFolder}</p>}

              <h3>Home location</h3>
              <p className="recyclebin-hint">
                Shown instead of the reverse-geocoded place name for anything nearby (event/trip locations, default
                event titles) — useful since that automatic lookup can be as coarse as a whole county for a real
                home address. Leave blank to use the automatic name everywhere.
              </p>
              <div className="settings-row">
                <input
                  type="text"
                  placeholder="e.g. Kirke Hyllinge"
                  value={homeLocationInput}
                  onChange={(e) => setHomeLocationInput(e.target.value)}
                />
                <button type="button" onClick={handleSaveHomeLocation} disabled={busy}>
                  Save
                </button>
              </div>
            </section>
          )}

          {activeTab === 'icloud' && (
            <section className="settings-section">
              {!libraryFolder ? (
          <p className="recyclebin-hint">Set a library folder above first.</p>
        ) : status?.connected ? (
          <>
            <p>
              Connected as <strong>{status.appleUsername}</strong>
            </p>
            <p className="settings-meta">
              {status.lastSyncAt
                ? `Last synced ${new Date(status.lastSyncAt).toLocaleString()} — ${status.lastSyncItemCount ?? 0} item(s) (${status.lastSyncStatus})`
                : 'Never synced yet — the first sync may take a while.'}
            </p>
            {status.lastSyncStatus === 'Error' && status.lastSyncMessage && (
              <p className="error">{status.lastSyncMessage}</p>
            )}
            {syncing && (
              <p className="settings-meta" title="icloudpd's most recent output line, updating live">
                {status.currentActivity ? `Working: ${status.currentActivity}` : 'Starting…'}
              </p>
            )}
            <div className="settings-row">
              <button type="button" onClick={handleSyncNow} disabled={busy}>
                {busy ? 'Syncing…' : 'Sync now'}
              </button>
              <label>
                <input
                  type="checkbox"
                  checked={status.enabled ?? false}
                  onChange={handleToggleEnabled}
                  disabled={busy}
                />{' '}
                Auto-sync every {Math.round((status.intervalMinutes ?? 360) / 60)}h
              </label>
              <button type="button" onClick={handleDisconnect} disabled={busy}>
                Disconnect
              </button>
            </div>
            <div className="settings-row">
              <label>
                Only sync photos from the last{' '}
                <input
                  type="number"
                  min={1}
                  value={lookbackInput}
                  onChange={(e) => setLookbackInput(e.target.value)}
                  disabled={!!sinceDateInput}
                  style={{ width: '70px' }}
                />{' '}
                day(s)
              </label>
            </div>
            <div className="settings-row">
              <label>
                or since a specific date:{' '}
                <input
                  type="date"
                  value={sinceDateInput}
                  onChange={(e) => setSinceDateInput(e.target.value)}
                />
              </label>
              {sinceDateInput && (
                <button type="button" onClick={handleClearSinceDate} disabled={busy}>
                  Clear (use days instead)
                </button>
              )}
            </div>
            <div className="settings-row">
              <label>
                Max files to check per sync:{' '}
                <input
                  type="number"
                  min={1}
                  value={recentCapInput}
                  onChange={(e) => setRecentCapInput(e.target.value)}
                  style={{ width: '90px' }}
                />
              </label>
              <button type="button" onClick={handleSaveScope} disabled={busy}>
                Save
              </button>
            </div>
            <p className="settings-meta">
              The date setting applies to every sync, not just the first (the specific date wins over "last N days"
              when both are set) — widen it if you want more history, but a never-yet-synced account otherwise won't
              pull years of backlog by surprise. "Max files to check" is a separate performance ceiling: raise it if
              your date range covers more than that many recently-added items, since anything beyond the cap won't
              even be considered.
            </p>
          </>
        ) : awaitingCode ? (
          <div className="settings-row">
            <input
              type="text"
              placeholder="6-digit code"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              autoFocus
            />
            <button type="button" onClick={handleICloudVerify} disabled={busy || !code.trim()}>
              Verify
            </button>
            <button type="button" onClick={handleCancelLogin} disabled={busy}>
              Start over
            </button>
          </div>
        ) : (
          <div className="settings-row">
            <input
              type="text"
              placeholder="Apple ID"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
            />
            <input
              type="password"
              placeholder="Password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
            <button type="button" onClick={handleICloudLogin} disabled={busy || !username.trim() || !password}>
              Connect
            </button>
          </div>
        )}
            </section>
          )}

          {activeTab === 'display' && (
            <section className="settings-section">
              <label className="settings-checkbox-row">
                <input
                  type="checkbox"
                  checked={use24Hour}
                  onChange={(e) => setUse24HourTime(e.target.checked)}
                />
                Use 24-hour time (14:30 instead of 2:30 PM)
              </label>
              <p className="recyclebin-hint">
                Overrides your browser's own locale default, which is where AM/PM was coming from.
              </p>

              <div className="settings-thumbsize-row">
                <label htmlFor="thumb-size-slider">Thumbnail size</label>
                <input
                  id="thumb-size-slider"
                  type="range"
                  min={MIN_THUMB_MIN_WIDTH}
                  max={MAX_THUMB_MIN_WIDTH}
                  step={10}
                  value={thumbMinWidth}
                  onChange={(e) => setThumbMinWidth(Number(e.target.value))}
                />
                <span className="settings-thumbsize-value">{thumbMinWidth}px</span>
              </div>
              <p className="recyclebin-hint">
                The grid behind this window updates live as you drag — close this to see the full effect.
              </p>
            </section>
          )}
        </div>
      </div>
    </div>
  )
}
