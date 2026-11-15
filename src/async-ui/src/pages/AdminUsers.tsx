import { useCallback, useEffect, useState } from 'react'
import { createUser, errorMessage, getUsers, requestUsageScan, setUserPassword, setUserQuota, setUserRole, type AdminUser } from '../api'
import { useAuth } from '../hooks/useAuth'
import { formatBytes, formatDate } from '../utils/format'
import { parseQuota } from '../utils/storage'
import { QuotaMeter } from '../components/UsageSummary'

const WARN_PERCENT = 90

/** "1.2 GB of 10 GB" with a meter, or just the size without a quota. */
function UserStorage({ u }: { u: AdminUser }) {
  if (u.quotaBytes === null) return <span>{formatBytes(u.usedBytes)} <span className="muted">· no quota</span></span>
  const percent = u.quotaBytes > 0 ? 100 * u.usedBytes / u.quotaBytes : 100
  const state = u.usedBytes > u.quotaBytes ? 'Over' : percent >= WARN_PERCENT ? 'Warning' : 'Ok'
  return (
    <span className="usage-cell">
      <span className={state === 'Over' ? 'error-text' : undefined}>{formatBytes(u.usedBytes)} of {formatBytes(u.quotaBytes)}</span>
      <QuotaMeter quota={{ usedBytes: u.usedBytes, quotaBytes: u.quotaBytes, percent, state }} label={`Quota of ${u.username}`} />
    </span>
  )
}
import DeleteUserPanel from '../components/DeleteUserPanel'
import PurgeList from '../components/PurgeList'

export default function AdminUsers() {
  const { user: me } = useAuth()
  const [users, setUsers] = useState<AdminUser[] | null>(null)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [isAdmin, setIsAdmin] = useState(false)
  const [error, setError] = useState('')
  const [deleting, setDeleting] = useState<number | null>(null)
  const [notice, setNotice] = useState('')
  /** Bumped when a deletion started purges, so their list reloads. */
  const [purges, setPurges] = useState(0)

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

  const setQuota = async (u: AdminUser) => {
    const answer = prompt(`Storage quota for ${u.username}, over all their folders, in GB (e.g. 100 or 0.5). Leave empty for no limit.`,
      u.quotaBytes === null ? '' : String(+(u.quotaBytes / 1024 ** 3).toFixed(2)))
    if (answer === null) return
    const quota = parseQuota(answer)
    if (quota === undefined) { setError('Enter a size in GB, or leave it empty for no limit.'); return }
    try { await setUserQuota(u.id, quota); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const recount = async () => {
    try { await requestUsageScan(true); setNotice('Counting everyone\'s storage in the background; reload in a minute.') }
    catch (err) { setError(errorMessage(err)) }
  }

  const toggleAdmin = async (u: AdminUser) => {
    try { await setUserRole(u.id, !u.isAdmin); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const deletingUser = deleting !== null ? users?.find(u => u.id === deleting) : undefined
  const adminCount = users?.filter(u => u.isAdmin).length ?? 0

  return (
    <>
      <div className="header">
        <h1>Users</h1>
        <button className="secondary small" onClick={recount} title="Lists every folder on its storage again (in the background).">Count storage again</button>
      </div>
      {error && <p className="error-msg">{error}</p>}
      {notice && <p className="success-msg">{notice}</p>}

      {deletingUser && (
        <DeleteUserPanel key={deletingUser.id} user={deletingUser} onCancel={() => setDeleting(null)} onDeleted={async started => {
          setNotice(`${deletingUser.username} was deleted.${started > 0 ? ' Their stored data is being removed (see below).' : ''}`)
          setDeleting(null)
          if (started > 0) setPurges(n => n + 1)
          await load()
        }} />
      )}

      {users === null ? <div className="loading">Loading…</div> : (
        <div className="table-wrap">
          <table className="stack">
            <thead><tr><th>Username</th><th>Name</th><th>Role</th><th>Sign-in</th><th>Storage</th><th>Created</th><th></th></tr></thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id}>
                  <td className="title-cell"><span>{u.username}{u.id === me?.id && <span className="muted"> (you)</span>}</span></td>
                  <td data-label="Name"><span>{u.displayName}</span></td>
                  <td data-label="Role"><span className={`pill${u.isAdmin ? ' pill-accent' : ''}`}>{u.isAdmin ? 'Admin' : 'User'}</span></td>
                  <td data-label="Sign-in">
                    <span className="pills">
                      {u.hasPassword && <span className="pill">Password</span>}
                      {u.hasOidc && <span className="pill">SSO</span>}
                      {!u.hasPassword && !u.hasOidc && <span className="muted">—</span>}
                    </span>
                  </td>
                  <td data-label="Storage"><UserStorage u={u} /></td>
                  <td data-label="Created" className="muted nowrap"><span title={formatDate(u.createdAt)}>{new Date(u.createdAt).toLocaleDateString()}</span></td>
                  <td className="actions-cell wrapping">
                    <div className="row-actions">
                      <button className="secondary small" onClick={() => resetPassword(u)}>Set password</button>
                      <button className="secondary small" onClick={() => setQuota(u)}>Quota</button>
                      {u.id !== me?.id && (
                        <button className="secondary small" onClick={() => toggleAdmin(u)}>{u.isAdmin ? 'Make user' : 'Make admin'}</button>
                      )}
                      {u.id !== me?.id && !(u.isAdmin && adminCount <= 1) && (
                        <button className="danger small" onClick={() => { setNotice(''); setDeleting(u.id) }}>Delete</button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <PurgeList refresh={purges} />

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
