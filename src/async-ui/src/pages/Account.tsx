import { useState } from 'react'
import { changeOwnPassword, errorMessage } from '../api'
import { CreditsCard } from '../components/Credits'
import { useAuth } from '../hooks/useAuth'

/** The signed-in person's own account: who they are and, for password accounts, changing the password. */
export default function Account() {
  const { user } = useAuth()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [done, setDone] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    setDone(false)
    if (next !== repeat) { setError('The two new passwords are not the same.'); return }
    setBusy(true)
    try {
      await changeOwnPassword(current, next)
      setCurrent('')
      setNext('')
      setRepeat('')
      setDone(true)
    } catch (err) {
      setError(errorMessage(err, 'Could not change the password.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="header"><h1>Account</h1></div>
      <section className="card section">
        <h2>{user?.displayName}</h2>
        <p className="muted">
          Signed in as <strong>{user?.username}</strong>{user?.isAdmin && <> · <span className="pill pill-accent">admin</span></>}
        </p>
      </section>

      <section className="card section">
        <h2>Password</h2>
        {user?.hasPassword ? (
          <form onSubmit={submit}>
            <p className="hint">
              Changing it signs you out in every other browser. This one stays signed in, and your devices keep syncing (they have
              keys of their own).
            </p>
            <div className="form-row">
              <label>Current password
                <input type="password" value={current} onChange={e => setCurrent(e.target.value)} required autoComplete="current-password" />
              </label>
            </div>
            <div className="form-row">
              <label>New password
                <input type="password" value={next} onChange={e => setNext(e.target.value)} required minLength={8} autoComplete="new-password" />
              </label>
              <label>New password again
                <input type="password" value={repeat} onChange={e => setRepeat(e.target.value)} required minLength={8} autoComplete="new-password" />
              </label>
            </div>
            <p className="hint small-text">At least 8 characters.</p>
            {error && <p className="error-msg">{error}</p>}
            {done && <p className="success-msg">Password changed. Other browsers were signed out.</p>}
            <button type="submit" disabled={busy}>{busy ? 'Changing…' : 'Change password'}</button>
          </form>
        ) : (
          <p className="hint">
            You sign in with single sign-on, so this account has no password of its own: change it with your identity provider.
            An admin can give the account a password if you also need to sign in without it.
          </p>
        )}
      </section>

      <CreditsCard />
    </>
  )
}
