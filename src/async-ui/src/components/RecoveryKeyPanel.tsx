import { useState } from 'react'
import { errorMessage, issueRecoveryKey, type Folder } from '../api'
import { useAuth } from '../hooks/useAuth'

interface Props {
  folder: Folder
  /** A key to show right away (the one made with the folder); otherwise the person asks for a new one first. */
  shown?: string
  onClose: () => void
}

/**
 * A standard-encryption Backup folder's recovery key. With it and the storage alone, the backups can be restored
 * without this server (or taken over by a new one). The key is shown once: the server keeps only its key file in
 * the repository, so asking again makes a new key and the previous one stops working.
 */
export default function RecoveryKeyPanel({ folder, shown, onClose }: Props) {
  const { user } = useAuth()
  const [key, setKey] = useState<string | null>(shown ?? null)
  const [storagePath, setStoragePath] = useState(folder.rootPath)
  const [password, setPassword] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState('')
  const replacing = folder.recoveryKeyCreatedAt !== null && shown === undefined

  const issue = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const made = await issueRecoveryKey(folder.id, user?.hasPassword ? { password } : { confirm: confirmed })
      setKey(made.recoveryKey)
      setStoragePath(made.storagePath)
      setPassword('')
    } catch (err) {
      setError(errorMessage(err, 'Could not make a recovery key.'))
    } finally {
      setBusy(false)
    }
  }

  const copy = async () => {
    if (!key) return
    try {
      await navigator.clipboard.writeText(key)
      setCopied(true)
    } catch {
      setError('Copying is not allowed here: select the key and copy it by hand.')
    }
  }

  const close = () => {
    if (key && !confirm('Close? The recovery key is not shown again (you can make a new one, which replaces it).')) return
    setKey(null)
    onClose()
  }

  return (
    <div className="card section">
      <div className="header">
        <h2>Recovery key: {folder.name}</h2>
        <button type="button" className="secondary small" onClick={close}>{key ? 'Done' : 'Cancel'}</button>
      </div>

      {key ? (
        <>
          <p className="hint">
            Store this key somewhere safe, away from this server (a password manager, or printed). <strong>It is shown only now.</strong>{' '}
            With it and the storage, these backups can be restored without this server, even if its database is lost.
          </p>
          <pre className="command recovery-key">{key}</pre>
          <div className="actions">
            <button type="button" className="secondary small" onClick={copy}>{copied ? 'Copied' : 'Copy'}</button>
          </div>
          <p className="hint small-text">
            To restore without the server, give <code>async-cli</code> the folder's directory on the storage
            (<code>{storagePath}</code> on {folder.storageBackendName}) and this key:
          </p>
          <pre className="command">async-cli restore --repo &lt;that directory&gt; --target &lt;dir&gt;</pre>
          <p className="hint small-text">
            A new server can take these backups over: add a Backup folder with standard encryption on the same storage and
            directory, and enter this key.
          </p>
        </>
      ) : (
        <form onSubmit={issue}>
          <p className="hint">
            {replacing
              ? `This folder got a recovery key on ${new Date(folder.recoveryKeyCreatedAt!).toLocaleDateString()}. The server does not keep keys, so it can only make a new one: the previous key then stops working.`
              : 'This folder has no recovery key yet. Without one, its backups depend on this server\'s database: if that is lost, they cannot be read.'}
          </p>
          {user?.hasPassword ? (
            <label>
              Your password
              <input type="password" value={password} onChange={e => setPassword(e.target.value)} required autoComplete="current-password" />
            </label>
          ) : (
            <label className="checkbox">
              <input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />
              {replacing ? 'Make a new key; the previous one stops working.' : 'Make a recovery key for this folder.'}
            </label>
          )}
          {error && <p className="error-msg">{error}</p>}
          <div className="actions">
            <button type="submit" disabled={busy || (!user?.hasPassword && !confirmed)}>{replacing ? 'Make a new key' : 'Make a recovery key'}</button>
          </div>
        </form>
      )}
      {key && error && <p className="error-msg">{error}</p>}
    </div>
  )
}
