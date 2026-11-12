import { useCallback, useEffect, useState } from 'react'
import {
  deleteStorageBackend, errorMessage, getStorageBackends, getStorageOptions, testStorageBackend,
  type StorageBackend, type StorageOptions,
} from '../api'
import StorageBackendForm from '../components/StorageBackendForm'
import { typeLabel } from '../utils/storage'
import { formatDate, relativeTime } from '../utils/format'

/** Where synced folders and backups are kept: the user's own back-ends plus shared ones. */
export default function Storage() {
  const [backends, setBackends] = useState<StorageBackend[] | null>(null)
  const [options, setOptions] = useState<StorageOptions | null>(null)
  const [editing, setEditing] = useState<StorageBackend | 'new' | null>(null)
  const [testing, setTesting] = useState<number | null>(null)
  const [error, setError] = useState('')

  const load = useCallback(() => Promise.all([getStorageBackends(), getStorageOptions()])
    .then(([b, o]) => { setBackends(b); setOptions(o) })
    .catch(err => setError(errorMessage(err, 'Could not load storage.'))), [])

  useEffect(() => { load() }, [load])

  const test = async (b: StorageBackend) => {
    setTesting(b.id)
    try { await testStorageBackend(b.id); await load() } catch (err) { setError(errorMessage(err)) } finally { setTesting(null) }
  }

  const remove = async (b: StorageBackend) => {
    if (!confirm(`Remove "${b.name}"? ASync forgets how to reach it; the files stored there are not deleted.`)) return
    try { await deleteStorageBackend(b.id); await load() } catch (err) { setError(errorMessage(err)) }
  }

  return (
    <>
      <div className="header">
        <h1>Storage</h1>
        {editing === null && <button onClick={() => setEditing('new')}>Add storage</button>}
      </div>
      <p className="hint">Where your folders and backups are kept. Passwords and keys are stored encrypted and never shown again.</p>
      {error && <p className="error-msg">{error}</p>}

      {editing !== null && options && (
        <StorageBackendForm
          options={options}
          existing={editing === 'new' ? undefined : editing}
          onSaved={() => { setEditing(null); load() }}
          onCancel={() => setEditing(null)}
        />
      )}

      {backends === null ? <div className="loading">Loading…</div> : backends.length === 0 ? (
        <p className="empty">No storage yet. Add an S3 bucket, a Nextcloud, an SFTP server or a network share.</p>
      ) : (
        <div className="card-list">
          {backends.map(b => (
            <div key={b.id} className="card item-head">
              <div className="item-main">
                <div className="item-title">
                  <strong>{b.name}</strong>
                  <span className="pill pill-accent">{typeLabel(b.type)}</span>
                  {b.shared && <span className="pill">Shared</span>}
                  {b.lastCheckedAt === null ? <span className="pill">Not tested</span>
                    : b.lastCheckOk
                      ? <span className="pill pill-success" title={formatDate(b.lastCheckedAt)}>Working</span>
                      : <span className="pill pill-danger" title={formatDate(b.lastCheckedAt)}>Failing</span>}
                </div>
                <code className="muted">{b.description}</code>
                {b.lastCheckedAt !== null && (b.lastCheckOk
                  ? <span className="muted small-text" title={formatDate(b.lastCheckedAt)}>Checked {relativeTime(b.lastCheckedAt)}</span>
                  : <span className="error-text small-text" title={formatDate(b.lastCheckedAt)}>{b.lastCheckError}</span>)}
              </div>
              {b.canEdit && (
                <div className="row-actions">
                  <button className="secondary small" disabled={testing === b.id} onClick={() => test(b)}>{testing === b.id ? 'Testing…' : 'Test'}</button>
                  <button className="secondary small" onClick={() => setEditing(b)}>Edit</button>
                  <button className="danger small" onClick={() => remove(b)}>Remove</button>
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </>
  )
}
