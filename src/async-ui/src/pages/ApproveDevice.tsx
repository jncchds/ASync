import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { decideDevice, errorMessage, getPendingDevice, type PendingDevice } from '../api'
import { PLATFORM_LABELS, relativeTime } from '../utils/format'

type State =
  | { kind: 'loading' }
  | { kind: 'missing'; message: string }
  | { kind: 'pending'; device: PendingDevice }
  | { kind: 'done'; approved: boolean; name: string }

/** Where a headless device's approval link leads: shows what is asking to join, then approves or denies it. */
export default function ApproveDevice() {
  const [params] = useSearchParams()
  const code = params.get('code')?.trim() ?? ''
  const [state, setState] = useState<State>({ kind: 'loading' })
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!code) return
    let cancelled = false
    getPendingDevice(code)
      .then(device => { if (!cancelled) setState({ kind: 'pending', device }) })
      .catch(err => { if (!cancelled) setState({ kind: 'missing', message: errorMessage(err, 'No device is waiting with this code.') }) })
    return () => { cancelled = true }
  }, [code])

  const decide = async (approve: boolean) => {
    if (state.kind !== 'pending') return
    setBusy(true)
    setError('')
    try {
      await decideDevice(state.device.userCode, approve)
      setState({ kind: 'done', approved: approve, name: state.device.name })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="narrow">
      <h1>Approve a device</h1>
      {!code ? (
        <p className="error-msg">The link has no code. <Link to="/devices">Enter it on the Devices page.</Link></p>
      ) : state.kind === 'loading' ? (
        <div className="loading">Loading…</div>
      ) : state.kind === 'missing' ? (
        <div className="card">
          <p className="error-msg">{state.message}</p>
          <Link to="/devices">Back to devices</Link>
        </div>
      ) : state.kind === 'pending' ? (
        <div className="card">
          <p>This device wants to join your account and sync your folders:</p>
          <dl>
            <dt>Name</dt><dd>{state.device.name}</dd>
            <dt>Platform</dt><dd>{PLATFORM_LABELS[state.device.platform]}</dd>
            <dt>Code</dt><dd><code className="big-code">{state.device.userCode}</code></dd>
          </dl>
          <p className="hint">
            Only approve it if you started it yourself and it shows the same code. The request expires {relativeTime(state.device.expiresAt)}.
          </p>
          {error && <p className="error-msg">{error}</p>}
          <div className="actions">
            <button disabled={busy} onClick={() => decide(true)}>Approve</button>
            <button disabled={busy} className="secondary" onClick={() => decide(false)}>Deny</button>
          </div>
        </div>
      ) : (
        <div className="card">
          <p className={state.approved ? 'success-msg' : 'muted'}>
            {state.approved ? `"${state.name}" has been added. It will finish setting up by itself.` : `"${state.name}" was denied.`}
          </p>
          <Link to="/devices">Back to devices</Link>
        </div>
      )}
    </div>
  )
}
