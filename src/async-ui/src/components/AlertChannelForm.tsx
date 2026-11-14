import { useState } from 'react'
import {
  createAlertChannel, errorMessage, testAlertSettings, updateAlertChannel,
  type AlertChannel, type AlertChannelSettings, type AlertChannelType, type AlertTestResult,
} from '../api'
import { CHANNEL_TYPES } from '../utils/alerts'

interface Field {
  key: string
  label: string
  kind?: 'text' | 'password' | 'email' | 'url'
  required?: boolean
  placeholder?: string
  hint?: string
  /** Write-only on the server: empty while editing = keep the stored value. */
  secret?: boolean
}

const FIELDS: Record<AlertChannelType, Field[]> = {
  Ntfy: [
    { key: 'serverUrl', label: 'ntfy server', kind: 'url', required: true, placeholder: 'https://ntfy.sh' },
    { key: 'topic', label: 'Topic', required: true, placeholder: 'my-async-alerts', hint: 'Letters, digits, - and _. On ntfy.sh, pick a name nobody can guess.' },
    { key: 'token', label: 'Access token', kind: 'password', secret: true, placeholder: 'tk_… (only for protected topics)' },
  ],
  Webhook: [
    { key: 'url', label: 'Address', kind: 'url', required: true, placeholder: 'https://hooks.example.com/async' },
    { key: 'authorization', label: 'Authorization header', kind: 'password', secret: true, placeholder: 'Bearer … (optional)', hint: 'Sent as the Authorization header.' },
  ],
  Email: [{ key: 'address', label: 'E-mail address', kind: 'email', required: true, placeholder: 'you@example.com' }],
}

const DEFAULTS: Record<AlertChannelType, Record<string, string>> = {
  Ntfy: { serverUrl: 'https://ntfy.sh', topic: '', token: '' },
  Webhook: { url: '', authorization: '' },
  Email: { address: '' },
}

interface Props {
  existing?: AlertChannel
  emailAvailable: boolean
  onSaved: () => void
  onCancel: () => void
}

/** Add or edit an alert channel, with a test message before saving. */
export default function AlertChannelForm({ existing, emailAvailable, onSaved, onCancel }: Props) {
  const [type, setType] = useState<AlertChannelType>(existing?.type ?? 'Ntfy')
  const [name, setName] = useState(existing?.name ?? '')
  const [enabled, setEnabled] = useState(existing?.enabled ?? true)
  const [values, setValues] = useState<Record<string, string>>(() => existing
    ? Object.fromEntries(Object.entries({ ...DEFAULTS[existing.type], ...existing.settings }).filter(([k]) => k !== 'type').map(([k, v]) => [k, v ?? '']))
    : DEFAULTS.Ntfy)
  const [test, setTest] = useState<AlertTestResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const types = CHANNEL_TYPES.filter(t => t.type !== 'Email' || emailAvailable || existing?.type === 'Email')

  const chooseType = (t: AlertChannelType) => {
    setType(t)
    setValues(DEFAULTS[t])
    setTest(null)
  }

  const set = (key: string, value: string) => {
    setValues(v => ({ ...v, [key]: value }))
    setTest(null)
  }

  /** Empty strings become null, so the server keeps stored secrets. */
  const settings = (): AlertChannelSettings => ({
    type,
    ...Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v.trim() === '' ? null : v])),
  })

  const runTest = async () => {
    setBusy(true)
    setError('')
    try { setTest(await testAlertSettings(settings(), existing?.id)) }
    catch (err) { setError(errorMessage(err, 'The test could not run.')) }
    finally { setBusy(false) }
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      const req = { name: name.trim(), settings: settings(), enabled }
      if (existing) await updateAlertChannel(existing.id, req)
      else await createAlertChannel(req)
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
        <h2>{existing ? `Edit ${existing.name}` : 'Add a channel'}</h2>
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
      {!existing && !emailAvailable && (
        <p className="hint small-text">E-mail is not offered: this server has no mail settings (an admin sets <code>Smtp__Host</code> and <code>Smtp__From</code>).</p>
      )}

      <label>
        Name
        <input value={name} onChange={e => setName(e.target.value)} required maxLength={100} placeholder="My phone" />
      </label>

      <div className="form-row">
        {FIELDS[type].map(f => {
          const stored = f.secret && existing?.secretsSet.some(s => s.toLowerCase() === f.key.toLowerCase())
          return (
            <label key={f.key}>
              {f.label}
              <input
                type={f.kind === 'password' ? 'password' : f.kind === 'email' ? 'email' : f.kind === 'url' ? 'url' : 'text'}
                value={values[f.key] ?? ''}
                placeholder={stored ? '•••••• stored — leave empty to keep' : f.placeholder}
                required={f.required}
                autoComplete={f.kind === 'password' ? 'new-password' : 'off'}
                onChange={e => set(f.key, e.target.value)} />
              {f.hint && <span className="field-hint">{f.hint}</span>}
            </label>
          )
        })}
      </div>

      <label className="checkbox">
        <input type="checkbox" checked={enabled} onChange={e => setEnabled(e.target.checked)} /> Send alerts here
      </label>

      {test && <p className={test.ok ? 'success-msg' : 'error-msg'}>{test.ok ? 'Test message sent. Check that it arrived.' : `Test failed: ${test.error}`}</p>}
      {error && <p className="error-msg">{error}</p>}

      <div className="actions">
        <button type="button" className="secondary" disabled={busy} onClick={runTest}>Send test</button>
        <button type="submit" disabled={busy}>{existing ? 'Save' : 'Add'}</button>
      </div>
    </form>
  )
}
