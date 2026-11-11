import { useCallback, useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  errorMessage, getDevices, getEnrollments, renameDevice, revokeDevice, revokeEnrollment,
  type Device, type Enrollment,
} from '../api'
import AddDevicePanel from '../components/AddDevicePanel'
import { formatDate, PLATFORM_LABELS, relativeTime } from '../utils/format'

export default function Devices() {
  const [devices, setDevices] = useState<Device[] | null>(null)
  const [enrollments, setEnrollments] = useState<Enrollment[]>([])
  const [adding, setAdding] = useState(false)
  const [approveCode, setApproveCode] = useState('')
  const [error, setError] = useState('')
  const navigate = useNavigate()

  const load = useCallback(() => Promise.all([getDevices(), getEnrollments()])
    .then(([d, e]) => {
      setDevices(d)
      setEnrollments(e)
    })
    .catch(err => setError(errorMessage(err, 'Could not load devices.'))), [])

  useEffect(() => { load() }, [load])

  const rename = async (device: Device) => {
    const name = prompt('Device name', device.name)?.trim()
    if (!name || name === device.name) return
    try { await renameDevice(device.id, name); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const revoke = async (device: Device) => {
    if (!confirm(`Remove "${device.name}"? It stops syncing at once. To use it again, add it again.`)) return
    try { await revokeDevice(device.id); await load() } catch (err) { setError(errorMessage(err)) }
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

      {adding && (
        <AddDevicePanel
          knownDeviceIds={devices?.map(d => d.id) ?? []}
          onDeviceAdded={load}
          onClose={() => { setAdding(false); load() }}
        />
      )}

      {devices === null ? <div className="loading">Loading…</div> : active.length === 0 ? (
        <p className="empty">No devices yet. Use <strong>Add device</strong> to connect a phone, computer or server.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>Name</th><th>Platform</th><th>Version</th><th>Last seen</th><th>Added</th><th></th></tr>
            </thead>
            <tbody>
              {active.map(d => (
                <tr key={d.id}>
                  <td>{d.name}</td>
                  <td>{PLATFORM_LABELS[d.platform]}</td>
                  <td className="muted">{d.appVersion ?? '—'}</td>
                  <td title={d.lastSeenAt ? formatDate(d.lastSeenAt) : undefined}>{d.lastSeenAt ? relativeTime(d.lastSeenAt) : 'Never'}</td>
                  <td className="muted">{formatDate(d.createdAt)}</td>
                  <td className="row-actions">
                    <button className="secondary small" onClick={() => rename(d)}>Rename</button>
                    <button className="danger small" onClick={() => revoke(d)}>Remove</button>
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
                <span>
                  {e.label ?? (e.reusable ? 'Reusable code' : 'One-time code')}
                  {e.reusable && <span className="muted"> · used {e.usedCount}×</span>}
                  <span className="muted"> · expires {relativeTime(e.expiresAt)}</span>
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
