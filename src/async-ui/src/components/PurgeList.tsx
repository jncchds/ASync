import { useCallback, useEffect, useState } from 'react'
import { getPurges, isPurgeFinished, type PurgeJob } from '../api'
import { formatBytes, relativeTime } from '../utils/format'

const STATUS: Record<PurgeJob['status'], { label: string; pill: string }> = {
  Pending: { label: 'Queued', pill: '' },
  Waiting: { label: 'Waiting', pill: 'pill-warning' },
  Running: { label: 'Deleting…', pill: 'pill-accent' },
  Done: { label: 'Done', pill: 'pill-success' },
  Failed: { label: 'Failed', pill: 'pill-danger' },
}

/**
 * The purges of deleted folders' stored data that this person started: removing a large folder takes a while, so
 * the server does it in the background and this list follows it (every 2 seconds while one is running).
 * `refresh` changes when the page started a new one; `owner` limits the list to that person's folders (the Folders
 * page), otherwise it also shows the purges of accounts an admin deleted.
 */
export default function PurgeList({ refresh, owner }: { refresh: number; owner?: string }) {
  const [jobs, setJobs] = useState<PurgeJob[]>([])

  const load = useCallback(() => getPurges()
    .then(all => setJobs(owner === undefined ? all : all.filter(j => j.ownerName === owner)))
    .catch(() => {}), [owner])
  useEffect(() => { load() }, [load, refresh])

  const running = jobs.some(j => !isPurgeFinished(j))
  useEffect(() => {
    if (!running) return
    const timer = setInterval(load, 2000)
    return () => clearInterval(timer)
  }, [running, load])

  if (jobs.length === 0) return null
  return (
    <section className="card section">
      <h2>Deleting stored data</h2>
      <ul className="purge-list">
        {jobs.map(j => (
          <li key={j.id}>
            <div className="purge-head">
              <strong>{j.folderName}</strong>
              {owner === undefined && j.ownerName && <span className="muted"> ({j.ownerName})</span>}
              <span className={`pill ${STATUS[j.status].pill}`}>{STATUS[j.status].label}</span>
            </div>
            <div className="muted small-text">
              {j.filesDeleted.toLocaleString()} file{j.filesDeleted === 1 ? '' : 's'}, {formatBytes(j.bytesDeleted)} removed
              {j.finishedAt ? ` · finished ${relativeTime(j.finishedAt)}` : ` · started ${relativeTime(j.createdAt)}`}
              {j.filesLeft > 0 && ` · ${j.filesLeft.toLocaleString()} left`}
            </div>
            {j.message && <div className={`small-text${j.status === 'Failed' ? ' error-msg' : ' hint'}`}>{j.message}</div>}
          </li>
        ))}
      </ul>
    </section>
  )
}
