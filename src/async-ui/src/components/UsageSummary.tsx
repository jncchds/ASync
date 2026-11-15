import { useState } from 'react'
import { errorMessage, requestUsageScan, type QuotaStatus, type Usage } from '../api'
import { formatDate, isFuture, quotaText, relativeTime } from '../utils/format'

/** A thin bar showing how full a quota is: accent, amber from the warning threshold, red when over. */
export function QuotaMeter({ quota, label }: { quota: QuotaStatus; label: string }) {
  if (quota.quotaBytes === null) return null
  const percent = Math.min(100, Math.max(0, quota.percent ?? 0))
  return (
    <div className={`meter ${quota.state.toLowerCase()}`} role="meter" aria-label={label}
      aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(percent)}>
      <span style={{ width: `${Math.max(percent, quota.usedBytes > 0 ? 1.5 : 0)}%` }} />
    </div>
  )
}

export function QuotaPill({ quota }: { quota: QuotaStatus }) {
  if (quota.state === 'Over') return <span className="pill pill-danger">⚠ Quota full</span>
  if (quota.state === 'Warning') return <span className="pill pill-warning">Nearly full</span>
  return null
}

/**
 * The person's storage use: the total against their quota, each back-end with a limit of its own, when it was counted
 * and a button to count again. Counting lists every folder on its storage, so it runs in the background on the server
 * (every few hours, or when asked) and never on a page load.
 */
export default function UsageSummary({ usage, onScan }: { usage: Usage; onScan?: () => void }) {
  const [asked, setAsked] = useState(false)
  const [error, setError] = useState('')
  const limited = usage.backends.filter(b => b.quota.quotaBytes !== null)
  const queued = usage.scanQueued || asked

  const scan = async () => {
    setError('')
    try { await requestUsageScan(); setAsked(true); onScan?.() } catch (err) { setError(errorMessage(err)) }
  }

  return (
    <section className="card usage-summary">
      <div className="item-head">
        <div className="item-main">
          <div className="item-title">
            <strong>Storage used</strong>
            <QuotaPill quota={usage.total} />
          </div>
          <span className="usage-figure">{quotaText(usage.total)}</span>
        </div>
        <button className="secondary small" disabled={queued} onClick={scan}
          title="Lists your folders on their storage again. Deleted files count from then on.">
          {queued ? 'Counting…' : 'Count again'}
        </button>
      </div>
      <QuotaMeter quota={usage.total} label="Your storage quota" />
      {usage.total.state === 'Over' && (
        <p className="error-text small-text">
          New uploads are refused, so devices report failed syncs. Delete files (they count once the folders are counted
          again) or ask an admin for more space. Deleting, restoring and downloading still work.
        </p>
      )}
      {limited.map(b => (
        <div key={b.storageBackendId} className="usage-backend">
          <div className="usage-line">
            <span>On <strong>{b.name}</strong></span>
            <span className="muted">{quotaText(b.quota)} per user</span>
            <QuotaPill quota={b.quota} />
          </div>
          <QuotaMeter quota={b.quota} label={`Your quota on ${b.name}`} />
        </div>
      ))}
      <span className="muted small-text">
        {usage.lastScanAt
          ? <>Counted <span title={formatDate(usage.lastScanAt)}>{relativeTime(usage.lastScanAt)}</span>, plus uploads since.</>
          : 'Not all folders are counted yet.'}
        {usage.nextScanAt && isFuture(usage.nextScanAt) &&
          <> Next count <span title={formatDate(usage.nextScanAt)}>{relativeTime(usage.nextScanAt)}</span>.</>}
      </span>
      {error && <p className="error-msg">{error}</p>}
    </section>
  )
}
