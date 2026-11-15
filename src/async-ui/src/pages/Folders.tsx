import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  detachFolderDevice, errorMessage, getAlertIncidents, getDevices, getFolders, getStorageBackends, getUsage,
  type AlertIncident, type Device, type PurgeJob, type Folder, type FolderDevice, type FolderUsage, type MaintenanceStatus, type StorageBackend,
  type Usage,
} from '../api'
import UsageSummary from '../components/UsageSummary'
import FolderForm from '../components/FolderForm'
import FolderDeviceForm from '../components/FolderDeviceForm'
import SnapshotBrowser from '../components/SnapshotBrowser'
import PlainFileBrowser from '../components/PlainFileBrowser'
import RecoveryKeyPanel from '../components/RecoveryKeyPanel'
import DeleteFolderPanel from '../components/DeleteFolderPanel'
import PurgeList from '../components/PurgeList'
import { useSyncEvents, type SyncEvent } from '../hooks/useSyncEvents'
import { useAuth } from '../hooks/useAuth'
import { kindLabel, modeLabel } from '../utils/folders'
import { ALERT_KIND_LABELS, openByDevice } from '../utils/alerts'
import { formatBytes, formatDate, formatMinutes, relativeTime } from '../utils/format'

/** What a folder takes on its storage, as last counted: "1.2 GB · 3,412 files", with versions and trash or deduplicated data. */
function FolderSize({ usage, backup }: { usage: FolderUsage | null; backup: boolean }) {
  if (usage === null || usage.scannedAt === null)
    return <span className="muted" title={usage?.scanError ?? 'The server counts folders in the background every few hours.'}>size not counted yet</span>
  const pending = usage.pendingBytes > 0 ? `, plus ${formatBytes(usage.pendingBytes)} uploaded since` : ''
  const title = `Counted ${formatDate(usage.scannedAt)}${pending}.${usage.scanError ? `\nThe last count failed: ${usage.scanError}` : ''}`
  const files = `${usage.files.toLocaleString()} ${usage.files === 1 ? 'file' : 'files'}`
  return (
    <span title={title}>
      {formatBytes(usage.bytes + usage.pendingBytes)} on storage
      {backup
        ? usage.dataBytes !== null && <> ({formatBytes(usage.dataBytes)} of data in {usage.snapshots} {usage.snapshots === 1 ? 'snapshot' : 'snapshots'}, deduplicated)</>
        : <> ({files}{usage.historyBytes ? `, ${formatBytes(usage.historyBytes)} versions and trash` : ''})</>}
      {usage.scanError && <span className="error-text"> ⚠</span>}
    </span>
  )
}

/** The last prune or check of a Backup folder as a pill: green when fine, red when failed, grey when not run or off. */
function MaintenancePill({ status, intervalDays, done, failed, never, off }: {
  status: MaintenanceStatus | null
  intervalDays: number
  done: string
  failed: string
  never: string
  off: string
}) {
  const schedule = intervalDays > 0 ? `Scheduled every ${intervalDays === 1 ? 'day' : `${intervalDays} days`}.` : 'Not scheduled (set in Edit).'
  if (status === null)
    return <span className="pill" title={schedule}>{intervalDays > 0 ? never : off}</span>
  const title = `${formatDate(status.at)}${status.by ? ` by ${status.by}` : ''}${status.message ? `\n${status.message}` : ''}\n${schedule}`
  return status.ok
    ? <span className="pill pill-success" title={title}>✓ {done} {relativeTime(status.at)}</span>
    : <span className="pill pill-danger" title={title}>⚠ {failed} {relativeTime(status.at)}</span>
}

/** Which device form is open: adding one to a folder, or editing one device's settings there. */
type DeviceEdit = { folderId: number; deviceId?: string }

/** Synced and backed-up folders: what they are, where they are stored, and which devices sync them. */
export default function Folders() {
  const { user } = useAuth()
  const [folders, setFolders] = useState<Folder[] | null>(null)
  const [backends, setBackends] = useState<StorageBackend[]>([])
  const [devices, setDevices] = useState<Device[]>([])
  /** Open alert incidents (overdue or failing devices, failed checks/prunes). */
  const [incidents, setIncidents] = useState<AlertIncident[]>([])
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [deviceEdit, setDeviceEdit] = useState<DeviceEdit | null>(null)
  const [browsing, setBrowsing] = useState<number | null>(null)
  /** The recovery-key panel: a new folder's key to show once, or asking for a new key. */
  const [recoveryFor, setRecoveryFor] = useState<{ folderId: number; shown?: string } | null>(null)
  /** Bumped to re-create open forms from the latest data (after a conflict). */
  const [generation, setGeneration] = useState(0)
  /** Bumped when a device reports a sync, so an open snapshot list picks up new snapshots. */
  const [syncReports, setSyncReports] = useState(0)
  const [error, setError] = useState('')
  /** The folder whose delete panel is open. */
  const [deleting, setDeleting] = useState<number | null>(null)
  /** Bumped when a purge of stored data starts, so the list of purges reloads. */
  const [purges, setPurges] = useState(0)
  /** Storage use and quota (counted in the background); failures here must not hide the folders. */
  const [usage, setUsage] = useState<Usage | null>(null)
  const loadUsage = useCallback(() => getUsage().then(setUsage).catch(() => setUsage(null)), [])

  const load = useCallback(() => Promise.all([getFolders(), getStorageBackends(), getDevices()])
    .then(([f, b, d]) => {
      setFolders(f)
      setBackends(b)
      setDevices(d.filter(x => !x.revokedAt))
    })
    .catch(err => setError(errorMessage(err, 'Could not load folders.'))), [])

  // Only open incidents (resolved within the last 0 days = none); failures here must not hide the folders.
  const loadIncidents = useCallback(() => getAlertIncidents(0).then(setIncidents).catch(() => setIncidents([])), [])

  useEffect(() => { load(); loadIncidents(); loadUsage() }, [load, loadIncidents, loadUsage])

  const onEvent = useCallback((e: SyncEvent) => {
    if (e.name === 'config.changed' || e.name === 'folder.status' || e.name === 'reconnected') {
      load()
      if (e.name === 'folder.status') setSyncReports(n => n + 1)
    }
    if (e.name === 'alerts.changed' || e.name === 'reconnected') loadIncidents()
    if (e.name === 'usage.changed' || e.name === 'reconnected') { loadUsage(); if (e.name === 'usage.changed') load() }
  }, [load, loadIncidents, loadUsage])
  const live = useSyncEvents(onEvent)

  const reload = async () => {
    await load()
    setGeneration(g => g + 1)
  }

  const deleted = async (f: Folder, purge: PurgeJob | null) => {
    setDeleting(null)
    if (browsing === f.id) setBrowsing(null)
    if (recoveryFor?.folderId === f.id) setRecoveryFor(null)
    if (purge) setPurges(n => n + 1)
    await load()
  }

  const detach = async (f: Folder, d: FolderDevice) => {
    if (!confirm(`Stop syncing "${f.name}" on ${d.deviceName}? Its local files stay.`)) return
    try { await detachFolderDevice(f.id, d.deviceId); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const current = (id: number) => folders?.find(f => f.id === id)
  const editingFolder = typeof editing === 'number' ? current(editing) : undefined
  const browsingFolder = browsing !== null ? current(browsing) : undefined
  const recoveryFolder = recoveryFor !== null ? current(recoveryFor.folderId) : undefined
  const deletingFolder = deleting !== null ? current(deleting) : undefined
  const deviceAlerts = openByDevice(incidents)
  const openCount = incidents.filter(i => !i.resolvedAt).length

  return (
    <>
      <div className="header">
        <h1>Folders</h1>
        <div className="header-actions">
          <span className={`live live-${live}`} title="Updates from your devices appear here as they happen.">
            ● {live === 'live' ? 'Live' : live === 'connecting' ? 'Connecting…' : 'Offline'}
          </span>
          {editing === null && <button onClick={() => setEditing('new')} disabled={backends.length === 0}>Add folder</button>}
        </div>
      </div>
      <p className="hint">What your devices sync or back up, and where it is stored.</p>
      {openCount > 0 && (
        <div className="notice danger">
          <strong>{openCount === 1 ? '1 open problem' : `${openCount} open problems`}</strong>: a device is overdue or failing, a check failed, storage is failing or a quota is full.{' '}
          <Link to="/alerts">See alerts</Link>
        </div>
      )}
      {error && <p className="error-msg">{error}</p>}
      {usage && folders !== null && folders.length > 0 && <UsageSummary usage={usage} onScan={loadUsage} />}
      {folders !== null && backends.length === 0 && (
        <p className="hint">First <Link to="/storage">add storage</Link> where your folders will be kept.</p>
      )}

      {editing !== null && (editing === 'new' || editingFolder) && (
        <FolderForm
          key={`${editing}-${generation}`}
          backends={backends}
          existing={editingFolder}
          onSaved={saved => {
            setEditing(null)
            if (saved.recoveryKey) setRecoveryFor({ folderId: saved.id, shown: saved.recoveryKey })
            load()
          }}
          onCancel={() => setEditing(null)}
          onReload={reload}
        />
      )}

      {recoveryFor && recoveryFolder && (
        <RecoveryKeyPanel key={recoveryFolder.id} folder={recoveryFolder} shown={recoveryFor.shown} onClose={() => setRecoveryFor(null)} />
      )}

      {deletingFolder && (
        <DeleteFolderPanel key={deletingFolder.id} folder={deletingFolder} onDeleted={job => deleted(deletingFolder, job)} onCancel={() => setDeleting(null)} />
      )}
      <PurgeList refresh={purges} owner={user?.username} />

      {browsingFolder && (browsingFolder.mode === 'Backup'
        ? <SnapshotBrowser key={browsingFolder.id} folder={browsingFolder} refresh={syncReports} onClose={() => setBrowsing(null)} />
        : <PlainFileBrowser key={browsingFolder.id} folder={browsingFolder} refresh={syncReports} onClose={() => setBrowsing(null)} />)}

      {folders === null ? <div className="loading">Loading…</div> : folders.length === 0 ? (
        <p className="empty">No folders yet. Add one, then attach the devices that should sync it.</p>
      ) : (
        <div className="card-list section">
          {folders.map(f => {
            const browsable = f.mode === 'Backup' && f.encryption === 'Transport' && f.repositoryReady
            const hasFiles = f.mode !== 'Backup' && f.encryption === 'Transport'
            return (
              <div key={f.id} className="card folder">
                <div className="item-head">
                  <div className="item-main">
                    <div className="item-title">
                      <strong>{f.name}</strong>
                      <span className="pill pill-accent">{modeLabel(f.mode)}</span>
                      {f.kind !== 'Files' && <span className="pill">{kindLabel(f.kind)}</span>}
                      {f.mode === 'Backup' && (f.encryption === 'EndToEnd'
                        ? <span className="pill pill-success" title="Only your devices can read these backups">🔒 End-to-end</span>
                        : <span className="pill" title="Encrypted in transit and at rest; the server can show and restore files">Standard encryption</span>)}
                    </div>
                    <div className="item-meta">
                      <span>{f.storageBackendName}</span>
                      <code>{f.rootPath}</code>
                      <span>every {formatMinutes(f.intervalMinutes)}</span>
                      <FolderSize usage={f.usage} backup={f.mode === 'Backup'} />
                      <span>folder #{f.id}</span>
                    </div>
                    {f.excludes.length > 0 && <span className="muted small-text">Excludes: {f.excludes.join(', ')}</span>}
                    {f.mode === 'Backup' && f.repositoryReady && (
                      <div className="pills">
                        <MaintenancePill status={f.lastPrune} intervalDays={f.pruneIntervalDays}
                          done="Space freed" failed="Freeing space failed" never="Space not freed yet" off="Freeing space: off" />
                        <MaintenancePill status={f.lastCheck} intervalDays={f.checkIntervalDays}
                          done="Verified" failed="Verify failed" never="Not verified yet" off="Verifying: off" />
                      </div>
                    )}
                  </div>
                  <div className="row-actions">
                    {browsable && <button className="secondary small" onClick={() => setBrowsing(browsing === f.id ? null : f.id)}>Snapshots</button>}
                    {hasFiles && <button className="secondary small" onClick={() => setBrowsing(browsing === f.id ? null : f.id)}>Files</button>}
                    {browsable && recoveryFor?.folderId !== f.id && (
                      <button className="secondary small" onClick={() => setRecoveryFor({ folderId: f.id })}>Recovery key</button>
                    )}
                    <button className="secondary small" onClick={() => setEditing(f.id)}>Edit</button>
                    <button className="danger small" onClick={() => setDeleting(f.id)}>Delete</button>
                  </div>
                </div>

                {f.mode === 'Backup' && f.lastCheck && !f.lastCheck.ok && (
                  <div className="notice danger">
                    <strong>The last integrity check failed</strong> ({relativeTime(f.lastCheck.at)}{f.lastCheck.by ? `, by ${f.lastCheck.by}` : ''}).{' '}
                    {f.lastCheck.message} Some backups in this folder may not restore. For the full list, run on one of its devices
                    <pre className="command">async-cli check --folder {f.id} --read-data 100</pre>
                  </div>
                )}
                {f.mode === 'Backup' && f.lastPrune && !f.lastPrune.ok && (
                  <div className="notice">
                    Freeing space failed {relativeTime(f.lastPrune.at)}{f.lastPrune.by ? ` (by ${f.lastPrune.by})` : ''}: {f.lastPrune.message}{' '}
                    Nothing was lost; it is tried again within a day.
                  </div>
                )}
                {browsable && f.recoveryKeyCreatedAt === null && (
                  <div className="notice">
                    <strong>No recovery key yet.</strong> This folder's backups can only be read with a key kept in this server's
                    database. <button className="secondary small" onClick={() => setRecoveryFor({ folderId: f.id })}>Make a recovery key</button>{' '}
                    so they can be restored without the server too.
                  </div>
                )}
                {f.mode === 'Backup' && f.encryption === 'EndToEnd' && !f.repositoryReady && (
                  <div className="notice">
                    Not set up yet. On one of your devices, run
                    <pre className="command">async-cli init-encryption --folder {f.id}</pre>
                    It asks for a passphrase and prints a recovery key: keep both safe. Other devices then run{' '}
                    <code>async-cli unlock --folder {f.id}</code>.
                  </div>
                )}
                {f.mode === 'Backup' && f.encryption === 'EndToEnd' && f.repositoryReady && (
                  <p className="hint small-text">
                    The server cannot read end-to-end encrypted backups. Restore them on a device:{' '}
                    <code>async-cli restore --folder {f.id} --target &lt;dir&gt;</code>
                  </p>
                )}

                <div className="folder-devices">
                  {f.devices.length === 0 ? (
                    <p className="muted small-text">No devices yet: nothing syncs until you add one.</p>
                  ) : (
                    <div className="table-wrap inset">
                      <table className="stack">
                        <thead><tr><th>Device</th><th>Folder on the device</th>{f.mode === 'Mirror' && <th>Direction</th>}<th>Last sync</th><th></th></tr></thead>
                        <tbody>
                          {f.devices.map(d => (
                            <tr key={d.deviceId}>
                              <td className="title-cell">
                                <span>{d.deviceName}{d.paused && <> <span className="pill pill-warning">Paused</span></>}
                                  {(() => {
                                    const alert = deviceAlerts.get(`${f.id}:${d.deviceId}`)
                                    return alert && <> <span className={`pill ${alert.kind === 'Overdue' ? 'pill-warning' : 'pill-danger'}`} title={alert.detail}>⚠ {ALERT_KIND_LABELS[alert.kind]}</span></>
                                  })()}
                                </span>
                              </td>
                              <td data-label="Folder" className="wrap"><code title={d.localPath}>{d.localPath}</code></td>
                              {f.mode === 'Mirror' && (
                                <td data-label="Direction" className="nowrap"><span className="pill">{d.direction === 'Download' ? '⬇ Download' : '⬆ Upload'}</span></td>
                              )}
                              <td data-label="Last sync" className="wrap">
                                {d.lastSyncAt === null ? <span className="muted">Never</span> : (
                                  <span title={`${formatDate(d.lastSyncAt)}${d.lastSyncMessage ? `\n${d.lastSyncMessage}` : ''}`}>
                                    <span className={`status-line ${d.lastSyncOk ? 'success-text' : 'error-text'}`}>
                                      <span className="dot">●</span>{relativeTime(d.lastSyncAt)}
                                    </span>
                                    {d.lastSyncMessage && <span className="status-msg">{d.lastSyncMessage}</span>}
                                  </span>
                                )}
                              </td>
                              <td className="actions-cell">
                                <div className="row-actions">
                                  <button className="secondary small" onClick={() => setDeviceEdit({ folderId: f.id, deviceId: d.deviceId })}>Edit</button>
                                  <button className="danger small" onClick={() => detach(f, d)}>Remove</button>
                                </div>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                  {deviceEdit?.folderId === f.id ? (
                    <FolderDeviceForm
                      key={`${deviceEdit.deviceId ?? 'new'}-${generation}`}
                      folder={f}
                      devices={devices}
                      existing={f.devices.find(d => d.deviceId === deviceEdit.deviceId)}
                      onSaved={() => { setDeviceEdit(null); load() }}
                      onCancel={() => setDeviceEdit(null)}
                      onReload={reload}
                    />
                  ) : (
                    <button className="secondary small" onClick={() => setDeviceEdit({ folderId: f.id })}>Add device</button>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      )}
    </>
  )
}
