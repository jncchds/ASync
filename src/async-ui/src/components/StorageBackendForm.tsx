import { useState } from 'react'
import {
  createStorageBackend, errorMessage, testStorageSettings, updateStorageBackend,
  type StorageBackend, type StorageOptions, type StorageSettings, type StorageTestResult, type StorageType,
} from '../api'
import { STORAGE_TYPES } from '../utils/storage'

interface Field {
  key: string
  label: string
  kind?: 'text' | 'password' | 'number' | 'checkbox' | 'textarea'
  required?: boolean
  placeholder?: string
  hint?: string
  /** Write-only on the server: empty while editing = keep the stored value. */
  secret?: boolean
}

const FIELDS: Record<StorageType, Field[]> = {
  Local: [{ key: 'path', label: 'Path on the server', required: true, placeholder: '/data/backups' }],
  S3: [
    { key: 'endpoint', label: 'Endpoint', placeholder: 'https://s3.example.com', hint: 'Leave empty for AWS.' },
    { key: 'region', label: 'Region', required: true, placeholder: 'us-east-1' },
    { key: 'bucket', label: 'Bucket', required: true },
    { key: 'prefix', label: 'Folder in the bucket', placeholder: 'async (optional)' },
    { key: 'accessKeyId', label: 'Access key ID', required: true },
    { key: 'secretAccessKey', label: 'Secret access key', kind: 'password', secret: true, required: true },
    { key: 'forcePathStyle', label: 'Path-style addressing (MinIO and most self-hosted services)', kind: 'checkbox' },
  ],
  WebDav: [
    { key: 'url', label: 'Address', required: true, placeholder: 'https://cloud.example.com/remote.php/dav/files/alice/ASync/' },
    { key: 'username', label: 'User name' },
    { key: 'password', label: 'Password or app password', kind: 'password', secret: true },
  ],
  Sftp: [
    { key: 'host', label: 'Host', required: true },
    { key: 'port', label: 'Port', kind: 'number', required: true },
    { key: 'username', label: 'User name', required: true },
    { key: 'password', label: 'Password', kind: 'password', secret: true, hint: 'A password or a private key.' },
    { key: 'privateKey', label: 'Private key (OpenSSH format)', kind: 'textarea', secret: true, placeholder: '-----BEGIN OPENSSH PRIVATE KEY-----' },
    { key: 'privateKeyPassphrase', label: 'Key passphrase', kind: 'password', secret: true },
    { key: 'path', label: 'Directory', placeholder: 'backups (relative to the login directory, or empty)' },
    { key: 'hostKeyFingerprint', label: 'Server key fingerprint (SHA-256)', hint: 'Filled in by Test. Changes to it make connections fail, which protects against impostor servers.' },
  ],
  Smb: [
    { key: 'host', label: 'Host', required: true, placeholder: 'nas.local' },
    { key: 'share', label: 'Share', required: true },
    { key: 'path', label: 'Directory in the share', placeholder: 'optional' },
    { key: 'domain', label: 'Domain', placeholder: 'optional' },
    { key: 'username', label: 'User name', required: true },
    { key: 'password', label: 'Password', kind: 'password', secret: true },
  ],
}

const DEFAULTS: Record<StorageType, Record<string, string | number | boolean>> = {
  Local: { path: '' },
  S3: { endpoint: '', region: 'us-east-1', bucket: '', prefix: '', accessKeyId: '', secretAccessKey: '', forcePathStyle: true },
  WebDav: { url: '', username: '', password: '' },
  Sftp: { host: '', port: 22, username: '', password: '', privateKey: '', privateKeyPassphrase: '', path: '', hostKeyFingerprint: '' },
  Smb: { host: '', share: '', path: '', domain: '', username: '', password: '' },
}

interface Props {
  options: StorageOptions
  existing?: StorageBackend
  onSaved: () => void
  onCancel: () => void
}

/** Add or edit a storage back-end, with a connection test before saving. */
export default function StorageBackendForm({ options, existing, onSaved, onCancel }: Props) {
  const [type, setType] = useState<StorageType>(existing?.type ?? 'S3')
  const [name, setName] = useState(existing?.name ?? '')
  const [shared, setShared] = useState(existing?.shared ?? false)
  const [values, setValues] = useState<Record<string, string | number | boolean>>(() => existing
    ? Object.fromEntries(Object.entries({ ...DEFAULTS[existing.type], ...existing.settings }).map(([k, v]) => [k, v ?? '']))
    : DEFAULTS.S3)
  const [test, setTest] = useState<StorageTestResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const types = STORAGE_TYPES.filter(t => t.type !== 'Local' || options.canCreateLocal || existing?.type === 'Local')

  const chooseType = (t: StorageType) => {
    setType(t)
    setValues(DEFAULTS[t])
    setTest(null)
  }

  const set = (key: string, value: string | number | boolean) => {
    setValues(v => ({ ...v, [key]: value }))
    setTest(null)
  }

  /** Empty strings become null, so the server keeps stored secrets and optional fields stay unset. */
  const settings = (): StorageSettings => ({
    type,
    ...Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v === '' ? null : v])),
  })

  const runTest = async () => {
    setBusy(true)
    setError('')
    try {
      const result = await testStorageSettings(settings(), existing?.id)
      setTest(result)
      if (result.ok && type === 'Sftp' && result.hostKeyFingerprint && !values.hostKeyFingerprint)
        setValues(v => ({ ...v, hostKeyFingerprint: result.hostKeyFingerprint! }))
    } catch (err) {
      setError(errorMessage(err, 'The test could not run.'))
    } finally {
      setBusy(false)
    }
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const req = { name: name.trim(), settings: settings(), shared }
      if (existing) await updateStorageBackend(existing.id, req)
      else await createStorageBackend(req)
      onSaved()
    } catch (err) {
      setError(errorMessage(err, 'Could not save.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card section" onSubmit={save}>
      <div className="header">
        <h2>{existing ? `Edit ${existing.name}` : 'Add storage'}</h2>
        <button type="button" className="secondary small" onClick={onCancel}>Cancel</button>
      </div>

      {!existing && (
        <div className="type-picker">
          {types.map(t => (
            <button type="button" key={t.type} className={`type-option${t.type === type ? ' selected' : ''}`} onClick={() => chooseType(t.type)}>
              <strong>{t.label}</strong>
              <span className="muted">{t.blurb}</span>
            </button>
          ))}
        </div>
      )}

      <label>
        Name
        <input value={name} onChange={e => setName(e.target.value)} required maxLength={100} placeholder="Home NAS" />
      </label>

      {type === 'Local' && <p className="hint">Allowed locations: {options.localRoots.join(', ')}</p>}

      <div className="form-row">
        {FIELDS[type].map(f => {
          const stored = f.secret && existing?.secretsSet.some(s => s.toLowerCase() === f.key.toLowerCase())
          const placeholder = stored ? '•••••• stored — leave empty to keep' : f.placeholder
          const value = values[f.key] ?? ''
          if (f.kind === 'checkbox')
            return (
              <label key={f.key} className="checkbox full">
                <input type="checkbox" checked={Boolean(value)} onChange={e => set(f.key, e.target.checked)} /> {f.label}
              </label>
            )
          return (
            <label key={f.key} className={f.kind === 'textarea' ? 'full' : undefined}>
              {f.label}
              {f.kind === 'textarea'
                ? <textarea rows={4} value={String(value)} placeholder={placeholder} onChange={e => set(f.key, e.target.value)} />
                : <input
                    type={f.kind === 'number' ? 'number' : f.kind === 'password' ? 'password' : 'text'}
                    value={String(value)}
                    placeholder={placeholder}
                    required={f.required && !stored}
                    autoComplete={f.kind === 'password' ? 'new-password' : 'off'}
                    onChange={e => set(f.key, f.kind === 'number' ? Number(e.target.value) : e.target.value)} />}
              {f.hint && <span className="field-hint">{f.hint}</span>}
            </label>
          )
        })}
      </div>

      {options.canCreateShared && !existing && (
        <label className="checkbox">
          <input type="checkbox" checked={shared} onChange={e => setShared(e.target.checked)} />
          Shared: every user can keep folders here (each in their own sub-directory)
        </label>
      )}

      {test && (
        <p className={test.ok ? 'success-msg' : 'error-msg'}>
          {test.ok ? `Connection works (${test.latencyMs} ms).` : `Test failed: ${test.error}`}
          {test.hostKeyFingerprint && <><br /><span className="muted">Server key: <code>{test.hostKeyFingerprint}</code></span></>}
        </p>
      )}
      {error && <p className="error-msg">{error}</p>}

      <div className="actions">
        <button type="button" className="secondary" disabled={busy} onClick={runTest}>Test connection</button>
        <button type="submit" disabled={busy}>{existing ? 'Save' : 'Add'}</button>
      </div>
    </form>
  )
}
