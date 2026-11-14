import { useEffect, useState } from 'react'
import { deleteUser, errorMessage, getUserDeletionPreview, type AdminUser, type UserDeletionPreview } from '../api'

interface Props {
  user: AdminUser
  onDeleted: (purges: number) => void
  onCancel: () => void
}

const count = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/**
 * An admin deleting an account: the panel lists what goes with it (from the server), and optionally has the
 * server delete the stored data of the user's folders too, after the username is typed.
 */
export default function DeleteUserPanel({ user, onDeleted, onCancel }: Props) {
  const [preview, setPreview] = useState<UserDeletionPreview | null>(null)
  const [purge, setPurge] = useState(false)
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    getUserDeletionPreview(user.id).then(setPreview).catch(err => setError(errorMessage(err, 'Could not load what the account holds.')))
  }, [user.id])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const result = await deleteUser(user.id, purge, purge ? typed : undefined)
      onDeleted(result.purges.length)
    } catch (err) {
      setError(errorMessage(err, 'Could not delete the account.'))
    } finally {
      setBusy(false)
    }
  }

  const folders = preview?.folders ?? []
  return (
    <form className="card section delete-panel" onSubmit={submit}>
      <div className="header">
        <h2>Delete {user.username}</h2>
        <button type="button" className="secondary small" onClick={onCancel}>Cancel</button>
      </div>

      {preview === null ? (!error && <div className="loading">Loading…</div>) : (
        <>
          <p>This removes the account and everything that belongs to it:</p>
          <ul className="removes">
            <li>{count(preview.devices, 'device')}: removed at once; agents that are running stop.</li>
            <li>{count(folders.length, 'folder')}{folders.length > 0 && <>: {folders.join(', ')}</>}</li>
            <li>
              {count(preview.storageBackends.length, 'personal storage back-end')}
              {preview.storageBackends.length > 0 && <>: {preview.storageBackends.join(', ')} (with their credentials)</>}
            </li>
            <li>{count(preview.alertChannels, 'alert channel')}{preview.openIncidents > 0 && `, ${count(preview.openIncidents, 'open alert')}`}</li>
            <li>{count(preview.enrollmentCodes, 'unused enrollment code')}, {count(preview.sessions, 'browser session')}</li>
          </ul>
          <p className="hint">
            Shared storage back-ends stay.{' '}
            {folders.length > 0 && !purge && 'The stored data of the folders stays on the storage (a Backup folder’s recovery key still opens it).'}
          </p>

          {folders.length > 0 && (
            <div className="purge-option">
              <label className="checkbox">
                <input type="checkbox" checked={purge} onChange={e => { setPurge(e.target.checked); setTyped('') }} />
                Also delete the stored data of {folders.length === 1 ? 'the folder' : `all ${folders.length} folders`}
              </label>
              {purge && (
                <>
                  <p className="small-text">
                    Each folder’s directory on its storage is emptied in the background (after its devices have let go).
                    Nothing outside those directories is touched. <strong>This cannot be undone.</strong>
                  </p>
                  <label>Type the username, <strong>{user.username}</strong>, to confirm
                    <input type="text" value={typed} onChange={e => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
                  </label>
                </>
              )}
            </div>
          )}
        </>
      )}

      {error && <p className="error-msg">{error}</p>}
      <div className="actions">
        <button type="submit" className="danger" disabled={busy || preview === null || (purge && typed.trim() !== user.username)}>
          {busy ? 'Deleting…' : purge ? 'Delete account and data' : 'Delete account'}
        </button>
      </div>
    </form>
  )
}
