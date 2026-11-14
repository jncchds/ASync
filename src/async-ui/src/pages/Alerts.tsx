import { useCallback, useEffect, useState } from 'react'
import {
  deleteAlertChannel, errorMessage, getAlertChannels, getAlertIncidents, getAlertSettings, saveAlertSettings, testAlertChannel,
  type AlertChannel, type AlertIncident, type AlertSettings, type SaveAlertSettings,
} from '../api'
import AlertChannelForm from '../components/AlertChannelForm'
import { useSyncEvents, type SyncEvent } from '../hooks/useSyncEvents'
import { ALERT_KIND_LABELS, channelTypeLabel, incidentSubject } from '../utils/alerts'
import { formatDate, relativeTime } from '../utils/format'

type RuleKey = keyof SaveAlertSettings

const RULES: { key: RuleKey; label: string; unit: string; step: string; min: number; max: number }[] = [
  { key: 'minimumGraceHours', label: 'Overdue after at least', unit: 'hours', step: '0.25', min: 0.25, max: 720 },
  { key: 'graceFactor', label: '…or this many sync intervals', unit: '× interval', step: '0.5', min: 1, max: 100 },
  { key: 'failureThreshold', label: 'Failing after', unit: 'failed runs in a row', step: '1', min: 1, max: 100 },
  { key: 'reminderHours', label: 'Remind about open problems every', unit: 'hours (0 = never)', step: '1', min: 0, max: 720 },
]

/** One incident: what, where, since when (and when it ended). */
function IncidentRow({ incident: i }: { incident: AlertIncident }) {
  return (
    <div className="card item-head">
      <div className="item-main">
        <div className="item-title">
          <strong>{incidentSubject(i)}</strong>
          {i.resolvedAt
            ? <span className="pill pill-success" title={formatDate(i.resolvedAt)}>✓ Back to normal {relativeTime(i.resolvedAt)}</span>
            : <span className={`pill ${i.kind === 'Overdue' ? 'pill-warning' : 'pill-danger'}`}>⚠ {ALERT_KIND_LABELS[i.kind]}</span>}
        </div>
        <span className="small-text">{i.detail}</span>
        <span className="muted small-text" title={formatDate(i.openedAt)}>
          Since {relativeTime(i.openedAt)}{!i.resolvedAt && (i.notifiedAt ? ' · alert sent' : ' · alert pending')}
        </span>
      </div>
    </div>
  )
}

/**
 * Alerts: open problems (overdue or failing devices, failed checks and prunes), where alerts go (ntfy, webhook,
 * e-mail) and when a device counts as overdue. The server finds problems on its own; no push is needed.
 */
export default function Alerts() {
  const [incidents, setIncidents] = useState<AlertIncident[] | null>(null)
  const [channels, setChannels] = useState<AlertChannel[] | null>(null)
  const [settings, setSettings] = useState<AlertSettings | null>(null)
  const [rules, setRules] = useState<Record<RuleKey, string>>({ graceFactor: '', minimumGraceHours: '', failureThreshold: '', reminderHours: '' })
  const [editing, setEditing] = useState<AlertChannel | 'new' | null>(null)
  const [testing, setTesting] = useState<number | null>(null)
  const [rulesSaved, setRulesSaved] = useState(false)
  const [error, setError] = useState('')

  const loadIncidents = useCallback(() => getAlertIncidents().then(setIncidents)
    .catch(err => setError(errorMessage(err, 'Could not load alerts.'))), [])

  const load = useCallback(() => Promise.all([getAlertIncidents(), getAlertChannels(), getAlertSettings()])
    .then(([i, c, s]) => {
      setIncidents(i)
      setChannels(c)
      setSettings(s)
      setRules({
        graceFactor: s.graceFactor?.toString() ?? '', minimumGraceHours: s.minimumGraceHours?.toString() ?? '',
        failureThreshold: s.failureThreshold?.toString() ?? '', reminderHours: s.reminderHours?.toString() ?? '',
      })
    })
    .catch(err => setError(errorMessage(err, 'Could not load alerts.'))), [])

  useEffect(() => { load() }, [load])

  const onEvent = useCallback((e: SyncEvent) => {
    if (e.name === 'alerts.changed' || e.name === 'reconnected') loadIncidents()
  }, [loadIncidents])
  useSyncEvents(onEvent)

  const loadChannels = () => getAlertChannels().then(setChannels).catch(err => setError(errorMessage(err)))

  const test = async (c: AlertChannel) => {
    setTesting(c.id)
    setError('')
    try { await testAlertChannel(c.id); await loadChannels() } catch (err) { setError(errorMessage(err)) } finally { setTesting(null) }
  }

  const remove = async (c: AlertChannel) => {
    if (!confirm(`Remove "${c.name}"? Alerts stop going there.`)) return
    try { await deleteAlertChannel(c.id); await loadChannels() } catch (err) { setError(errorMessage(err)) }
  }

  const saveRules = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    setRulesSaved(false)
    const value = (k: RuleKey) => rules[k].trim() === '' ? null : Number(rules[k])
    try {
      setSettings(await saveAlertSettings({
        graceFactor: value('graceFactor'), minimumGraceHours: value('minimumGraceHours'),
        failureThreshold: value('failureThreshold'), reminderHours: value('reminderHours'),
      }))
      setRulesSaved(true)
    } catch (err) { setError(errorMessage(err, 'Could not save the rules.')) }
  }

  const open = incidents?.filter(i => !i.resolvedAt) ?? []
  const resolved = incidents?.filter(i => i.resolvedAt) ?? []

  return (
    <>
      <div className="header">
        <h1>Alerts</h1>
        {editing === null && <button onClick={() => setEditing('new')}>Add channel</button>}
      </div>
      <p className="hint">
        The server tells you when a device stops syncing or backing up, or when something fails: once per problem, again
        when it is fixed, and as a reminder while it lasts. It needs nothing on your devices{settings?.pushMode === 'None' ? ' and no push service' : ''}.
      </p>
      {error && <p className="error-msg">{error}</p>}

      <h2>Open problems</h2>
      {incidents === null ? <div className="loading">Loading…</div> : open.length === 0 ? (
        <p className="empty">Nothing is wrong: every device synced within its grace period.</p>
      ) : (
        <div className="card-list">{open.map(i => <IncidentRow key={i.id} incident={i} />)}</div>
      )}

      <div className="section">
        <h2>Where alerts go</h2>
        {editing !== null && settings && (
          <AlertChannelForm
            key={editing === 'new' ? 'new' : editing.id}
            existing={editing === 'new' ? undefined : editing}
            emailAvailable={settings.emailAvailable}
            onSaved={() => { setEditing(null); loadChannels() }}
            onCancel={() => setEditing(null)}
          />
        )}
        {channels === null ? <div className="loading">Loading…</div> : channels.length === 0 ? (
          <p className="empty">No channels yet: problems only show here and on the Folders page. Add ntfy, a webhook or e-mail to be told.</p>
        ) : (
          <div className="card-list">
            {channels.map(c => (
              <div key={c.id} className="card item-head">
                <div className="item-main">
                  <div className="item-title">
                    <strong>{c.name}</strong>
                    <span className="pill pill-accent">{channelTypeLabel(c.type)}</span>
                    {!c.enabled && <span className="pill">Off</span>}
                    {c.lastSentAt !== null && (c.lastOk
                      ? <span className="pill pill-success" title={formatDate(c.lastSentAt)}>Delivered</span>
                      : <span className="pill pill-danger" title={formatDate(c.lastSentAt)}>Failing</span>)}
                  </div>
                  <code className="muted">{c.description}</code>
                  {c.lastSentAt !== null && (c.lastOk
                    ? <span className="muted small-text" title={formatDate(c.lastSentAt)}>Last message {relativeTime(c.lastSentAt)}</span>
                    : <span className="error-text small-text" title={formatDate(c.lastSentAt)}>{c.lastError}</span>)}
                </div>
                <div className="row-actions">
                  <button className="secondary small" disabled={testing === c.id} onClick={() => test(c)}>{testing === c.id ? 'Sending…' : 'Send test'}</button>
                  <button className="secondary small" onClick={() => setEditing(c)}>Edit</button>
                  <button className="danger small" onClick={() => remove(c)}>Remove</button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {settings && (
        <form className="card section" onSubmit={saveRules}>
          <h2>When to alert</h2>
          <p className="hint small-text">
            A device is overdue when it has not synced a folder successfully for the longer of the two times below. Leave a
            field empty for the server's default.
          </p>
          <div className="form-row">
            {RULES.map(r => (
              <label key={r.key}>
                {r.label}
                <input type="number" inputMode="decimal" step={r.step} min={r.min} max={r.max} value={rules[r.key]}
                  placeholder={`${settings.defaults[r.key]} (default)`}
                  onChange={e => { setRules(v => ({ ...v, [r.key]: e.target.value })); setRulesSaved(false) }} />
                <span className="field-hint">{r.unit}</span>
              </label>
            ))}
          </div>
          {rulesSaved && <p className="success-msg">Saved. The next check uses these rules.</p>}
          <div className="actions"><button type="submit">Save rules</button></div>
        </form>
      )}

      {resolved.length > 0 && (
        <div className="section">
          <h2>Fixed in the last 7 days</h2>
          <div className="card-list">{resolved.map(i => <IncidentRow key={i.id} incident={i} />)}</div>
        </div>
      )}
    </>
  )
}
