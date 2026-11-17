import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  BROWSE_HINTS, BROWSE_LABELS, errorMessage, getDevices, getEnrollments, renameDevice, revokeDevice, revokeEnrollment, setDeviceMayAddFolders, testDevicePush,
  type Device, type Enrollment,
} from '../api'
import { useSyncEvents, type SyncEvent } from '../hooks/useSyncEvents'
import AddDevicePanel from '../components/AddDevicePanel'
import { formatDate, PLATFORM_LABELS, relativeTime } from '../utils/format'

export default function Devices() {
  const [devices, setDevices] = useState<Device[] | null>(null)
  const [enrollments, setEnrollments] = useState<Enrollment[]>([])
  const [adding, setAdding] = useState(false)
  const [approveCode, setApproveCode] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const navigate = useNavigate()

  const load = useCallback(() => Promise.all([getDevices(), getEnrollments()])
    .then(([d, e]) => {
      setDevices(d)
      setEnrollments(e)
    })
    .catch(err => setError(errorMessage(err, 'Could not load devices.'))), [])

  useEffect(() => { load() }, [load])

  // A device coming online, going offline or changing its browsing setting.
  useSyncEvents(useCallback((e: SyncEvent) => {
    if (e.name === 'device.status' || e.name === 'reconnected') void load()
  }, [load]))

  const rename = async (device: Device) => {
    const name = prompt('Device name', device.name)?.trim()
    if (!name || name === device.name) return
    try { await renameDevice(device.id, name); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const toggleAddFolders = async (device: Device, allowed: boolean) => {
    setError('')
    // Shown at once; the list is reloaded either way (also by the device.status event).
    setDevices(list => list?.map(d => d.id === device.id ? { ...d, mayAddFolders: allowed } : d) ?? null)
    try { await setDeviceMayAddFolders(device.id, allowed) } catch (err) { setError(errorMessage(err)) }
    await load()
  }

  const revoke = async (device: Device) => {
    if (!confirm(`Remove "${device.name}"? It stops syncing at once. To use it again, add it again.`)) return
    try { await revokeDevice(device.id); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const testPush = async (device: Device) => {
    setError('')
    setNotice('')
    try {
      await testDevicePush(device.id)
      setNotice(`Wake-up sent to "${device.name}": it syncs its folders now.`)
      await load()
    } catch (err) { setError(errorMessage(err, 'The wake-up could not be sent.')); await load() }
  }

  const cancelCode = async (e: Enrollment) => {
    try { await revokeEnrollment(e.id); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const active = devices?.filter(d => !d.revokedAt) ?? []
  const removed = devices?.filter(d => d.revokedAt) ?? []

  return (
    <>
      <div className="header">
        <h1>Devices</h1>
        {!adding && <button onClick={() => setAdding(true)}>Add device</button>}
      </div>

      {error && <p className="error-msg">{error}</p>}
      {notice && <p className="success-msg">{notice}</p>}

      {adding && (
        <AddDevicePanel
          knownDeviceIds={devices?.map(d => d.id) ?? []}
          onDeviceAdded={load}
          onClose={() => { setAdding(false); load() }}
        />
      )}

      {active.length > 0 && (
        <p className="hint">
          <strong>Adds folders:</strong> the device may create folders of yours and add itself to your folders from its app,
          on storage you may use. Nothing more: not other devices, storage settings or your account. On for phones and
          computers added with a one-time code or approved here; off for devices added with a reusable code (scripted agents).
        </p>
      )}

      {devices === null ? <div className="loading">Loading…</div> : active.length === 0 ? (
        <p className="empty">No devices yet. Use <strong>Add device</strong> to connect a phone, computer or server.</p>
      ) : (
        <div className="table-wrap">
          <table className="stack">
            <thead>
              <tr><th>Name</th><th>Platform</th><th>Version</th><th>Last seen</th><th>Wake-ups</th><th title="May add folders from its app">Adds folders</th><th></th></tr>
            </thead>
            <tbody>
              {active.map(d => (
                <tr key={d.id}>
                  <td className="title-cell"><span>{d.name}<span className="muted small-text" style={{ display: 'block', fontWeight: 400 }} title={formatDate(d.createdAt)}>added {new Date(d.createdAt).toLocaleDateString()}</span></span></td>
                  <td data-label="Platform"><span className="pill">{PLATFORM_LABELS[d.platform]}</span></td>
                  <td data-label="Version" className="muted nowrap"><span>{d.appVersion ?? '—'}</span></td>
                  <td data-label="Last seen" className="nowrap" title={d.lastSeenAt ? formatDate(d.lastSeenAt) : undefined}>
                    <span>
                      {d.lastSeenAt ? relativeTime(d.lastSeenAt) : 'Never'}
                      {/* Online now, and whether its folders can be browsed from here (the folder form's Browse…). */}
                      {d.browse && d.browse !== 'Offline' && d.browse !== 'Revoked' && (
                        <span className="status-msg" title={BROWSE_HINTS[d.browse]}>
                          <span className={`pill${d.browse === 'Available' ? ' pill-success' : d.browse === 'Wakeable' ? '' : ' pill-warning'}`}>{BROWSE_LABELS[d.browse]}</span>
                        </span>
                      )}
                    </span>
                  </td>
                  <td data-label="Wake-ups" className="wrap">
                    {d.push ? (
                      <span title={`UnifiedPush through ${d.push.host}, registered ${formatDate(d.push.registeredAt)}` +
                        (d.push.lastWakeAt ? `\nLast wake-up ${formatDate(d.push.lastWakeAt)}${d.push.lastWakeError ? `: ${d.push.lastWakeError}` : ''}` : '')}>
                        <span className={`pill ${!d.push.enabled ? '' : d.push.lastWakeOk === false ? 'pill-danger' : 'pill-success'}`}>
                          {d.push.lastWakeOk === false ? '⚠ ' : ''}UnifiedPush
                        </span>
                        <span className="status-msg">
                          {d.push.enabled ? d.push.host : 'off on this server (Push__Mode=None)'}
                          {d.push.enabled && <> · <button type="button" className="link" onClick={() => testPush(d)}>Send wake-up</button></>}
                        </span>
                      </span>
                    ) : <span className="muted">{d.platform === 'Android' ? 'Own schedule' : '—'}</span>}
                  </td>
                  <td data-label="Adds folders" className="nowrap">
                    <label className="checkbox" title={d.mayAddFolders
                      ? `${d.name} may create folders of yours and add itself to your folders from its app.`
                      : `${d.name} cannot add folders; its app says to allow it here.`}>
                      <input type="checkbox" checked={!!d.mayAddFolders} aria-label={`${d.name} may add folders`}
                        onChange={e => toggleAddFolders(d, e.target.checked)} />
                      <span>{d.mayAddFolders ? 'Allowed' : 'Off'}</span>
                    </label>
                  </td>
                  <td className="actions-cell">
                    <div className="row-actions">
                      <button className="secondary small" onClick={() => rename(d)}>Rename</button>
                      <button className="danger small" onClick={() => revoke(d)}>Remove</button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <section className="card section">
        <h2>Approve a server or CLI device</h2>
        <p className="hint">
          A device without a camera shows a short code when it starts. Enter it here, or open the link it printed.
        </p>
        <form className="inline-form" onSubmit={e => { e.preventDefault(); navigate(`/devices/approve?code=${encodeURIComponent(approveCode.trim())}`) }}>
          <input value={approveCode} onChange={e => setApproveCode(e.target.value)} placeholder="ABCD-EFGH" required />
          <button type="submit">Continue</button>
        </form>
      </section>

      {enrollments.length > 0 && (
        <section className="card section">
          <h2>Open add-device codes</h2>
          <ul className="plain-list">
            {enrollments.map(e => (
              <li key={e.id}>
                <span className="item-title">
                  <span>{e.label ?? (e.reusable ? 'Reusable code' : 'One-time code')}</span>
                  <span className={`pill${e.reusable ? ' pill-accent' : ''}`}>{e.reusable ? `reusable · used ${e.usedCount}×` : 'one-time'}</span>
                  <span className="muted small-text">expires {relativeTime(e.expiresAt)}</span>
                </span>
                <button className="secondary small" onClick={() => cancelCode(e)}>Cancel</button>
              </li>
            ))}
          </ul>
        </section>
      )}

      {removed.length > 0 && (
        <details className="section">
          <summary className="muted">Removed devices ({removed.length})</summary>
          <ul className="plain-list">
            {removed.map(d => (
              <li key={d.id} className="muted">{d.name} · {PLATFORM_LABELS[d.platform]} · removed {formatDate(d.revokedAt!)}</li>
            ))}
          </ul>
        </details>
      )}
    </>
  )
}
