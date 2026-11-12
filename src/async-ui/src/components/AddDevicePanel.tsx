import { useEffect, useRef, useState } from 'react'
import { QRCodeSVG } from 'qrcode.react'
import { createEnrollment, errorMessage, getDevices, type EnrollmentCreated } from '../api'
import { relativeTime } from '../utils/format'

const VALIDITY_OPTIONS = [
  { minutes: 10, label: '10 minutes' },
  { minutes: 60, label: '1 hour' },
  { minutes: 60 * 24, label: '1 day' },
  { minutes: 60 * 24 * 7, label: '7 days' },
  { minutes: 60 * 24 * 30, label: '30 days' },
]

interface Props {
  knownDeviceIds: string[]
  onDeviceAdded: () => void
  onClose: () => void
}

/**
 * Makes an add-device code and shows it as a QR code (phones and desktops scan it, or type the code) and as
 * a CLI command. While it is open, it watches for the new device so the user sees it join.
 */
export default function AddDevicePanel({ knownDeviceIds, onDeviceAdded, onClose }: Props) {
  const [reusable, setReusable] = useState(false)
  const [validMinutes, setValidMinutes] = useState(10)
  const [label, setLabel] = useState('')
  const [created, setCreated] = useState<EnrollmentCreated | null>(null)
  const [joined, setJoined] = useState<string[]>([])
  const [error, setError] = useState('')
  const [now, setNow] = useState(() => Date.now())
  const known = useRef(new Set(knownDeviceIds))

  const create = async () => {
    setError('')
    try {
      setCreated(await createEnrollment({ reusable, validMinutes, label: label.trim() || undefined }))
    } catch (err) {
      setError(errorMessage(err, 'Could not create a code.'))
    }
  }

  // Poll for devices that joined with this code, and re-render every few seconds for the expiry countdown.
  useEffect(() => {
    if (!created) return
    const timer = setInterval(async () => {
      setNow(Date.now())
      try {
        const fresh = (await getDevices()).filter(d => !known.current.has(d.id))
        if (fresh.length === 0) return
        fresh.forEach(d => known.current.add(d.id))
        setJoined(j => [...j, ...fresh.map(d => d.name)])
        onDeviceAdded()
      } catch { /* try again on the next tick */ }
    }, 3000)
    return () => clearInterval(timer)
  }, [created, onDeviceAdded])

  const expired = created ? Date.parse(created.expiresAt) <= now : false
  const done = created && !created.reusable && joined.length > 0
  // The server's configured public address (what the QR code holds), which may differ from this page's origin.
  const server = (created && new URL(created.pairUri).searchParams.get('s')) || window.location.origin

  return (
    <section className="card section add-device">
      <div className="header">
        <h2>Add a device</h2>
        <button className="secondary small" onClick={onClose}>{done ? 'Done' : 'Close'}</button>
      </div>

      {!created ? (
        <>
          <p className="hint">
            Make a code, then scan it with the ASync app on the new device (or type it in). Servers and CLI agents can use it too.
          </p>
          <label className="checkbox">
            <input type="checkbox" checked={reusable} onChange={e => setReusable(e.target.checked)} />
            Allow several devices to use this code (for scripted installs of CLI agents)
          </label>
          {reusable && (
            <div className="form-row">
              <label>
                Valid for
                <select value={validMinutes} onChange={e => setValidMinutes(Number(e.target.value))}>
                  {VALIDITY_OPTIONS.map(o => <option key={o.minutes} value={o.minutes}>{o.label}</option>)}
                </select>
              </label>
              <label>
                Label (optional)
                <input value={label} onChange={e => setLabel(e.target.value)} placeholder="Home servers" maxLength={100} />
              </label>
            </div>
          )}
          {error && <p className="error-msg">{error}</p>}
          <button onClick={create}>Make code</button>
        </>
      ) : (
        <div className="pairing">
          <div className="qr">
            <QRCodeSVG value={created.pairUri} size={200} marginSize={2} bgColor="transparent" fgColor="currentColor" />
          </div>
          <div className="pairing-details">
            <p className="hint">Scan with the ASync app, or enter the server address and code:</p>
            <dl>
              <dt>Server</dt><dd><code>{server}</code></dd>
              <dt>Code</dt><dd><code className="big-code">{created.code}</code></dd>
            </dl>
            <p className="hint">On a server or in Docker:</p>
            <pre className="command">async-cli enroll --server {server} --code {created.code}</pre>
            {expired ? <p className="error-msg">This code has expired. Close and make a new one.</p> : (
              <p className="muted small-text">
                <span className="pill pill-accent">{created.reusable ? 'Reusable' : 'Works once'}</span> expires {relativeTime(created.expiresAt, now)}
              </p>
            )}
            {joined.length > 0 && <p className="success-msg">Added: {joined.join(', ')}</p>}
          </div>
        </div>
      )}
    </section>
  )
}
