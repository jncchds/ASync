import { useCallback, useEffect, useState } from 'react'
import {
  deleteStorageBackend, errorMessage, getStorageBackends, getStorageOptions, getUsage, setStorageQuota, testStorageBackend,
  type StorageBackend, type StorageOptions, type Usage,
} from '../api'
import StorageBackendForm from '../components/StorageBackendForm'
import UsageSummary, { QuotaMeter, QuotaPill } from '../components/UsageSummary'
import { useAuth } from '../hooks/useAuth'
import { useSyncEvents, type SyncEvent } from '../hooks/useSyncEvents'
import { parseQuota, typeLabel } from '../utils/storage'
import { formatBytes, formatDate, quotaText, relativeTime } from '../utils/format'

/** The working/failing pill: the server tests every back-end hourly (and again within minutes after a failure). */
function HealthPill({ b }: { b: StorageBackend }) {
  if (b.lastCheckedAt === null) return <span className="pill" title="Tested automatically within the hour.">Not tested yet</span>
  const title = `Last tested ${formatDate(b.lastCheckedAt)}${b.checkFailures > 1 ? `\n${b.checkFailures} tests in a row failed` : ''}`
  return b.lastCheckOk
    ? <span className="pill pill-success" title={title}>Working</span>
    : <span className="pill pill-danger" title={title}>⚠ Failing{b.checkFailures > 1 ? ` (${b.checkFailures}×)` : ''}</span>
}

/** Where synced folders and backups are kept: the user's own back-ends plus shared ones. */
export default function Storage() {
  const [backends, setBackends] = useState<StorageBackend[] | null>(null)
  const [options, setOptions] = useState<StorageOptions | null>(null)
  const [editing, setEditing] = useState<StorageBackend | 'new' | null>(null)
  const [testing, setTesting] = useState<number | null>(null)
  const [error, setError] = useState('')
  const [usage, setUsage] = useState<Usage | null>(null)
  const { user } = useAuth()

  const load = useCallback(() => Promise.all([getStorageBackends(), getStorageOptions()])
    .then(([b, o]) => { setBackends(b); setOptions(o) })
    .catch(err => setError(errorMessage(err, 'Could not load storage.'))), [])
  const loadUsage = useCallback(() => getUsage().then(setUsage).catch(() => setUsage(null)), [])

  useEffect(() => { load(); loadUsage() }, [load, loadUsage])
  useSyncEvents(useCallback((e: SyncEvent) => {
    if (e.name === 'usage.changed' || e.name === 'reconnected') { load(); loadUsage() }
  }, [load, loadUsage]))

  const setQuota = async (b: StorageBackend) => {
    const answer = prompt(`How much may each user keep on "${b.name}"? In GB (e.g. 50 or 0.5); leave empty for no limit.`,
      b.userQuotaBytes === null ? '' : String(+(b.userQuotaBytes / 1024 ** 3).toFixed(2)))
    if (answer === null) return
    const quota = parseQuota(answer)
    if (quota === undefined) { setError('Enter a size in GB, or leave it empty for no limit.'); return }
    try { await setStorageQuota(b.id, quota); await Promise.all([load(), loadUsage()]) } catch (err) { setError(errorMessage(err)) }
  }

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
      <p className="hint">
        Where your folders and backups are kept. Passwords and keys are stored encrypted and never shown again. Each one is
        tested every hour; you get an alert when it keeps failing.
      </p>
      {error && <p className="error-msg">{error}</p>}
      {usage && usage.backends.length > 0 && <UsageSummary usage={usage} onScan={loadUsage} />}

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
                  <HealthPill b={b} />
                  {(() => { const q = usage?.backends.find(x => x.storageBackendId === b.id)?.quota; return q && <QuotaPill quota={q} /> })()}
                </div>
                <code className="muted">{b.description}</code>
                <BackendUse b={b} usage={usage} />
                {b.lastCheckedAt !== null && (b.lastCheckOk
                  ? <span className="muted small-text" title={formatDate(b.lastCheckedAt)}>Checked {relativeTime(b.lastCheckedAt)}</span>
                  : <span className="error-text small-text" title={formatDate(b.lastCheckedAt)}>
                      {b.lastCheckError} <span className="muted">({relativeTime(b.lastCheckedAt)}; tested again within minutes)</span>
                    </span>)}
              </div>
              {(b.canEdit || (b.shared && user?.isAdmin)) && (
                <div className="row-actions">
                  {b.shared && user?.isAdmin && <button className="secondary small" onClick={() => setQuota(b)}>Quota</button>}
                  {b.canEdit && <>
                    <button className="secondary small" disabled={testing === b.id} onClick={() => test(b)}>{testing === b.id ? 'Testing…' : 'Test'}</button>
                    <button className="secondary small" onClick={() => setEditing(b)}>Edit</button>
                    <button className="danger small" onClick={() => remove(b)}>Remove</button>
                  </>}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </>
  )
}

/** How much the caller's folders use on a back-end, its per-user limit (shared back-ends) and, for admins, everyone's total. */
function BackendUse({ b, usage }: { b: StorageBackend; usage: Usage | null }) {
  const mine = usage?.backends.find(x => x.storageBackendId === b.id)
  const parts: string[] = []
  if (mine && mine.quota.quotaBytes !== null) parts.push(`You use ${quotaText(mine.quota)} of what each user may keep here`)
  else if (b.usedBytes > 0 || mine) parts.push(`Your folders use ${formatBytes(b.usedBytes)}`)
  else if (b.userQuotaBytes !== null) parts.push(`Each user may keep ${formatBytes(b.userQuotaBytes)} here`)
  if (b.shared && b.totalUsedBytes !== null) parts.push(`all users: ${formatBytes(b.totalUsedBytes)}`)
  if (parts.length === 0) return null
  return (
    <div className="usage-backend">
      <span className="small-text">{parts.join(' · ')}</span>
      {mine && <QuotaMeter quota={mine.quota} label={`Your quota on ${b.name}`} />}
    </div>
  )
}
