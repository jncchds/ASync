import { useState } from 'react'
import { deleteFolder, errorMessage, type Folder, type PurgeJob } from '../api'

interface Props {
  folder: Folder
  onDeleted: (purge: PurgeJob | null) => void
  onCancel: () => void
}

/**
 * Deleting a folder: by default only syncing stops and the stored data stays (with a warning about what can still
 * open a backup repository). Ticking "also delete the stored data" asks for the folder's name and has the server
 * empty the folder's directory on its storage in the background.
 */
export default function DeleteFolderPanel({ folder, onDeleted, onCancel }: Props) {
  const [purge, setPurge] = useState(false)
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const transportBackup = folder.mode === 'Backup' && folder.encryption === 'Transport'
  const e2e = folder.encryption === 'EndToEnd'

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      onDeleted(await deleteFolder(folder.id, purge, purge ? typed : undefined))
    } catch (err) {
      setError(errorMessage(err, 'Could not delete the folder.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card section delete-panel" onSubmit={submit}>
      <div className="header">
        <h2>Delete “{folder.name}”</h2>
        <button type="button" className="secondary small" onClick={onCancel}>Cancel</button>
      </div>
      <p>Every device stops syncing it. The files on your devices stay where they are.</p>

      {!purge && (
        <p className="hint">
          The stored data stays in <code>{folder.rootPath}</code> on {folder.storageBackendName}.
          {transportBackup && (folder.recoveryKeyCreatedAt
            ? <> Without this folder, <strong>its recovery key is the only way to open those backups</strong> (with <code>async-cli restore --repo</code>, or by creating the folder again with the key).</>
            : <> <strong>This folder has no recovery key yet:</strong> once it is deleted, nothing can open those backups. Make a recovery key first if you may need them.</>)}
          {e2e && <> Its passphrase or recovery key still opens those backups.</>}
        </p>
      )}

      <div className="purge-option">
        <label className="checkbox">
          <input type="checkbox" checked={purge} onChange={e => { setPurge(e.target.checked); setTyped('') }} />
          Also delete the stored data
        </label>
        {purge && (
          <>
            <p className="small-text">
              Everything in <code>{folder.rootPath}</code> on {folder.storageBackendName} is deleted
              {folder.mode === 'Backup' ? ': all snapshots of this backup' : ', including versions and trash'}. Nothing outside that
              directory is touched. <strong>This cannot be undone.</strong>
            </p>
            <label>Type the folder's name, <strong>{folder.name}</strong>, to confirm
              <input type="text" value={typed} onChange={e => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
            </label>
          </>
        )}
      </div>

      {error && <p className="error-msg">{error}</p>}
      <div className="actions">
        <button type="submit" className="danger" disabled={busy || (purge && typed.trim() !== folder.name)}>
          {busy ? 'Deleting…' : purge ? 'Delete folder and data' : 'Delete folder'}
        </button>
      </div>
    </form>
  )
}
