import { useCallback, useEffect, useState } from 'react'
import { createUser, errorMessage, getUsers, setUserPassword, setUserRole, type AdminUser } from '../api'
import { useAuth } from '../hooks/useAuth'
import { formatDate } from '../utils/format'

export default function AdminUsers() {
  const { user: me } = useAuth()
  const [users, setUsers] = useState<AdminUser[] | null>(null)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [isAdmin, setIsAdmin] = useState(false)
  const [error, setError] = useState('')

  const load = useCallback(() => getUsers().then(setUsers).catch(err => setError(errorMessage(err))), [])
  useEffect(() => { load() }, [load])

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    try {
      await createUser(username.trim(), password, isAdmin)
      setUsername('')
      setPassword('')
      setIsAdmin(false)
      await load()
    } catch (err) {
      setError(errorMessage(err, 'Could not create the user.'))
    }
  }

  const resetPassword = async (u: AdminUser) => {
    const pw = prompt(`New password for ${u.username} (at least 8 characters). They will be signed out of every browser.`)
    if (!pw) return
    try { await setUserPassword(u.id, pw); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const toggleAdmin = async (u: AdminUser) => {
    try { await setUserRole(u.id, !u.isAdmin); await load() } catch (err) { setError(errorMessage(err)) }
  }

  return (
    <>
      <div className="header"><h1>Users</h1></div>
      {error && <p className="error-msg">{error}</p>}

      {users === null ? <div className="loading">Loading…</div> : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Username</th><th>Name</th><th>Role</th><th>Sign-in</th><th>Created</th><th></th></tr></thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id}>
                  <td>{u.username}</td>
                  <td>{u.displayName}</td>
                  <td>{u.isAdmin ? 'Admin' : 'User'}</td>
                  <td className="muted">{[u.hasPassword && 'Password', u.hasOidc && 'SSO'].filter(Boolean).join(' + ') || '—'}</td>
                  <td className="muted">{formatDate(u.createdAt)}</td>
                  <td className="row-actions">
                    <button className="secondary small" onClick={() => resetPassword(u)}>Set password</button>
                    {u.id !== me?.id && (
                      <button className="secondary small" onClick={() => toggleAdmin(u)}>{u.isAdmin ? 'Make user' : 'Make admin'}</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <section className="card section">
        <h2>New user</h2>
        <form onSubmit={create}>
          <div className="form-row">
            <label>Username<input value={username} onChange={e => setUsername(e.target.value)} required maxLength={100} /></label>
            <label>Password<input type="password" value={password} onChange={e => setPassword(e.target.value)} required minLength={8} autoComplete="new-password" /></label>
          </div>
          <label className="checkbox"><input type="checkbox" checked={isAdmin} onChange={e => setIsAdmin(e.target.checked)} /> Admin</label>
          <button type="submit">Create user</button>
        </form>
      </section>
    </>
  )
}
