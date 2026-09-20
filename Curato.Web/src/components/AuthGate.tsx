import { useState } from 'react'
import { login, setupAccount } from '../api'

export function AuthGate({ needsSetup, onSignedIn }: { needsSetup: boolean; onSignedIn: () => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (needsSetup) {
        await setupAccount(username, password)
      } else {
        await login(username, password)
      }
      onSignedIn()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="app-shell auth-shell">
      <h1>Curato</h1>
      <p className="tagline">
        {needsSetup
          ? 'Create the admin account (one-time setup — PLAN.md §15).'
          : 'Sign in to continue.'}
      </p>
      <form className="auth-form" onSubmit={handleSubmit}>
        <input
          type="text"
          placeholder="Username"
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          autoFocus
        />
        <input
          type="password"
          placeholder={needsSetup ? 'Password (min. 8 characters)' : 'Password'}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <button type="submit" disabled={busy || !username || !password}>
          {busy ? 'Please wait…' : needsSetup ? 'Create account' : 'Sign in'}
        </button>
      </form>
      {error && <p className="error">{error}</p>}
    </main>
  )
}
