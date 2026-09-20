import { useEffect, useState } from 'react'
import type { iCloudSyncStatus } from '../api'
import {
  getICloudSyncStatus,
  getLibraryFolder,
  iCloudCancelLogin,
  iCloudDisconnect,
  iCloudLogin,
  iCloudSyncNow,
  iCloudUpdateSettings,
  iCloudVerifyTwoFactor,
  setLibraryFolder,
} from '../api'

export function SettingsPanel({ onClose }: { onClose: () => void }) {
  const [libraryFolder, setLibraryFolderValue] = useState<string | null>(null)
  const [libraryFolderInput, setLibraryFolderInput] = useState('')
  const [status, setStatus] = useState<iCloudSyncStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [awaitingCode, setAwaitingCode] = useState(false)
  const [code, setCode] = useState('')

  function refresh() {
    getLibraryFolder().then((path) => {
      setLibraryFolderValue(path)
      setLibraryFolderInput((current) => current || path || '')
    })
    getICloudSyncStatus()
      .then(setStatus)
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
  }

  useEffect(refresh, [])

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

  function handleSaveLibraryFolder() {
    return runBusy(async () => {
      await setLibraryFolder(libraryFolderInput)
      refresh()
    })
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
    return runBusy(async () => {
      const result = await iCloudSyncNow()
      if (!result.success) {
        setError(result.errorMessage ?? 'Sync failed.')
      }
      refresh()
    })
  }

  function handleToggleEnabled() {
    if (!status) return Promise.resolve()
    return runBusy(async () => {
      await iCloudUpdateSettings(status.intervalMinutes ?? 360, !status.enabled)
      refresh()
    })
  }

  function handleDisconnect() {
    return runBusy(async () => {
      await iCloudDisconnect()
      setAwaitingCode(false)
      refresh()
    })
  }

  return (
    <div className="recyclebin-panel">
      <div className="recyclebin-header">
        <h2>Settings</h2>
        <button type="button" onClick={onClose}>
          Close
        </button>
      </div>
      {error && <p className="error">{error}</p>}

      <section className="settings-section">
        <h3>Library Folder</h3>
        <p className="recyclebin-hint">
          The destination iCloud sync (and any future external source) copies content into. Scanning/organizing a
          folder you point Curato at directly still works exactly as before — this is a separate, additional setting.
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
      </section>

      <section className="settings-section">
        <h3>iCloud Sync</h3>
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
    </div>
  )
}
