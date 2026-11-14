import { useState } from 'react'
import {
  createFolder, errorMessage, isConflict, updateFolder,
  type Folder, type FolderEncryption, type FolderKind, type Retention, type SaveFolder, type StorageBackend, type SyncMode,
} from '../api'
import { KINDS, MODES } from '../utils/folders'

const DEFAULT_RETENTION: Retention = { last: 0, hourly: 0, daily: 7, weekly: 4, monthly: 12, yearly: 0, withinHours: 24 }

const RETENTION_FIELDS: { key: keyof Retention; label: string }[] = [
  { key: 'withinHours', label: 'Keep all made within (hours)' },
  { key: 'last', label: 'Newest' },
  { key: 'hourly', label: 'Hourly' },
  { key: 'daily', label: 'Daily' },
  { key: 'weekly', label: 'Weekly' },
  { key: 'monthly', label: 'Monthly' },
  { key: 'yearly', label: 'Yearly' },
]

interface Props {
  backends: StorageBackend[]
  /** The folder as currently loaded (live updates replace it while the form is open). */
  existing?: Folder
  onSaved: () => void
  onCancel: () => void
  /** Discard the edits and start again from the latest version (after a 409). */
  onReload: () => void
}

/** Create or edit a folder. Mode, encryption, kind, storage and directory are fixed once it exists. */
export default function FolderForm({ backends, existing, onSaved, onCancel, onReload }: Props) {
  const [startRevision] = useState(existing?.revision)
  const [name, setName] = useState(existing?.name ?? '')
  const [mode, setMode] = useState<SyncMode>(existing?.mode ?? 'Backup')
  const [encryption, setEncryption] = useState<FolderEncryption>(existing?.encryption ?? 'Transport')
  const [kind, setKind] = useState<FolderKind>(existing?.kind ?? 'Files')
  const [backendId, setBackendId] = useState<number>(existing?.storageBackendId ?? backends[0]?.id ?? 0)
  const [rootPath, setRootPath] = useState(existing?.rootPath ?? '')
  const [interval, setIntervalMinutes] = useState(existing?.intervalMinutes ?? 60)
  const [excludes, setExcludes] = useState((existing?.excludes ?? []).join('\n'))
  const [retention, setRetention] = useState<Retention>(existing?.retention ?? DEFAULT_RETENTION)
  const [versionsDays, setVersionsDays] = useState(existing?.versionsKeepDays ?? 30)
  const [trashDays, setTrashDays] = useState(existing?.trashKeepDays ?? 30)
  const [pruneDays, setPruneDays] = useState(existing?.pruneIntervalDays ?? 7)
  const [checkDays, setCheckDays] = useState(existing?.checkIntervalDays ?? 30)
  const [checkPercent, setCheckPercent] = useState(existing?.checkReadDataPercent ?? 10)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [conflict, setConflict] = useState(false)

  const fixed = existing !== undefined
  const changedElsewhere = existing !== undefined && existing.revision !== startRevision

  const chooseMode = (m: SyncMode) => {
    setMode(m)
    if (m !== 'Backup') { setEncryption('Transport'); setKind('Files') }
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    setConflict(false)
    const req: SaveFolder = {
      name: name.trim(), mode, encryption, kind, storageBackendId: backendId, rootPath: rootPath.trim(),
      intervalMinutes: interval,
      excludes: excludes.split('\n').map(x => x.trim()).filter(Boolean),
      retention, versionsKeepDays: versionsDays, trashKeepDays: trashDays,
      pruneIntervalDays: pruneDays, checkIntervalDays: checkDays, checkReadDataPercent: checkPercent,
      expectedRevision: startRevision,
    }
    try {
      if (existing) await updateFolder(existing.id, req)
      else await createFolder(req)
      onSaved()
    } catch (err) {
      if (isConflict(err)) setConflict(true)
      else setError(errorMessage(err, 'Could not save the folder.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card section" onSubmit={save}>
      <div className="header">
        <h2>{existing ? `Edit ${existing.name}` : 'Add folder'}</h2>
        <button type="button" className="secondary small" onClick={onCancel}>Cancel</button>
      </div>

      {!fixed && (
        <div className="type-picker">
          {MODES.map(m => (
            <button type="button" key={m.mode} className={`type-option${m.mode === mode ? ' selected' : ''}`} onClick={() => chooseMode(m.mode)}>
              <strong>{m.label}</strong>
              <span className="muted">{m.blurb}</span>
            </button>
          ))}
        </div>
      )}

      <div className="form-row">
        <label>
          Name
          <input value={name} onChange={e => setName(e.target.value)} required maxLength={100} placeholder="Documents" />
        </label>
        <label>
          Storage
          <select value={backendId} disabled={fixed} onChange={e => setBackendId(Number(e.target.value))} required>
            {fixed && !backends.some(b => b.id === backendId) && <option value={backendId}>{existing?.storageBackendName}</option>}
            {backends.map(b => <option key={b.id} value={b.id}>{b.name}{b.shared ? ' (shared)' : ''}</option>)}
          </select>
        </label>
        <label>
          Directory on the storage
          <input value={rootPath} disabled={fixed} onChange={e => setRootPath(e.target.value)} required placeholder="documents" />
          {!fixed && <span className="field-hint">Folders on one storage must not be inside each other.</span>}
        </label>
        <label>
          Sync every (minutes)
          <input type="number" min={1} max={44640} value={interval} onChange={e => setIntervalMinutes(Number(e.target.value))} required />
        </label>
        {mode === 'Backup' && (
          <>
            <label>
              Encryption
              <select value={encryption} disabled={fixed} onChange={e => setEncryption(e.target.value as FolderEncryption)}>
                <option value="Transport">Standard: the server can show and restore files</option>
                <option value="EndToEnd">End-to-end: only your devices can read the backups</option>
              </select>
            </label>
            <label>
              Contains
              <select value={kind} disabled={fixed} onChange={e => setKind(e.target.value as FolderKind)}>
                {KINDS.map(k => <option key={k.kind} value={k.kind}>{k.label}</option>)}
              </select>
            </label>
          </>
        )}
      </div>
      {fixed && <p className="hint">Mode, encryption, contents, storage and directory cannot change. Create a new folder instead.</p>}
      {!fixed && encryption === 'EndToEnd' && (
        <p className="hint">
          After creating it, set it up on one device with <code>async-cli init-encryption</code>. Keep the passphrase and the
          recovery key it prints: without them nobody, not even the server's admin, can restore these backups.
        </p>
      )}

      {mode === 'Backup' && (
        <fieldset className="fieldset">
          <legend>Housekeeping <span className="muted">(0 days = never)</span></legend>
          <div className="form-row compact">
            <label>
              Free space every (days)
              <input type="number" min={0} max={3650} value={pruneDays} onChange={e => setPruneDays(Number(e.target.value))} />
            </label>
            <label>
              Verify every (days)
              <input type="number" min={0} max={3650} value={checkDays} onChange={e => setCheckDays(Number(e.target.value))} />
            </label>
            <label>
              Data read back per verify (%)
              <input type="number" min={0} max={100} value={checkPercent} onChange={e => setCheckPercent(Number(e.target.value))} />
            </label>
          </div>
          <p className="hint small-text">
            Freeing space removes the data of snapshots that are no longer kept. Verifying finds damage before you need a
            restore. {encryption === 'EndToEnd'
              ? 'For end-to-end folders your devices do both (only they have the key).'
              : 'The server does both.'}
          </p>
        </fieldset>
      )}

      {mode === 'Backup' ? (
        <fieldset className="fieldset">
          <legend>Snapshots to keep <span className="muted">(per device; 0 = none of that kind; the newest is always kept)</span></legend>
          <div className="form-row compact">
            {RETENTION_FIELDS.map(f => (
              <label key={f.key}>
                {f.label}
                <input type="number" min={0} value={retention[f.key] ?? 0}
                  onChange={e => setRetention(r => ({ ...r, [f.key]: f.key === 'withinHours' && e.target.value === '' ? null : Number(e.target.value) }))} />
              </label>
            ))}
          </div>
        </fieldset>
      ) : (
        <div className="form-row">
          <label>
            Keep old versions (days)
            <input type="number" min={0} max={3650} value={versionsDays} onChange={e => setVersionsDays(Number(e.target.value))} />
          </label>
          <label>
            Keep deleted files (days)
            <input type="number" min={0} max={3650} value={trashDays} onChange={e => setTrashDays(Number(e.target.value))} />
          </label>
        </div>
      )}

      <label>
        Exclude <span className="muted">(one pattern per line, like .gitignore)</span>
        <textarea rows={3} value={excludes} onChange={e => setExcludes(e.target.value)} placeholder={'node_modules/\n*.tmp'} />
      </label>

      {changedElsewhere && !conflict && <p className="hint warning-text">This folder was changed elsewhere since you opened it.</p>}
      {conflict && (
        <p className="error-msg">
          The folder was changed meanwhile. <button type="button" className="secondary small" onClick={onReload}>Reload</button> and
          apply your change again.
        </p>
      )}
      {error && <p className="error-msg">{error}</p>}

      <div className="actions">
        <button type="submit" disabled={busy || backends.length === 0}>{existing ? 'Save' : 'Add folder'}</button>
      </div>
    </form>
  )
}
