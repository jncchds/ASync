import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  deleteFolder, detachFolderDevice, errorMessage, getDevices, getFolders, getStorageBackends,
  type Device, type Folder, type FolderDevice, type StorageBackend,
} from '../api'
import FolderForm from '../components/FolderForm'
import FolderDeviceForm from '../components/FolderDeviceForm'
import SnapshotBrowser from '../components/SnapshotBrowser'
import { useSyncEvents, type SyncEvent } from '../hooks/useSyncEvents'
import { kindLabel, modeLabel } from '../utils/folders'
import { formatDate, formatMinutes, relativeTime } from '../utils/format'

/** Which device form is open: adding one to a folder, or editing one device's settings there. */
type DeviceEdit = { folderId: number; deviceId?: string }

/** Synced and backed-up folders: what they are, where they are stored, and which devices sync them. */
export default function Folders() {
  const [folders, setFolders] = useState<Folder[] | null>(null)
  const [backends, setBackends] = useState<StorageBackend[]>([])
  const [devices, setDevices] = useState<Device[]>([])
  const [editing, setEditing] = useState<number | 'new' | null>(null)
  const [deviceEdit, setDeviceEdit] = useState<DeviceEdit | null>(null)
  const [browsing, setBrowsing] = useState<number | null>(null)
  /** Bumped to re-create open forms from the latest data (after a conflict). */
  const [generation, setGeneration] = useState(0)
  /** Bumped when a device reports a sync, so an open snapshot list picks up new snapshots. */
  const [syncReports, setSyncReports] = useState(0)
  const [error, setError] = useState('')

  const load = useCallback(() => Promise.all([getFolders(), getStorageBackends(), getDevices()])
    .then(([f, b, d]) => {
      setFolders(f)
      setBackends(b)
      setDevices(d.filter(x => !x.revokedAt))
    })
    .catch(err => setError(errorMessage(err, 'Could not load folders.'))), [])

  useEffect(() => { load() }, [load])

  const onEvent = useCallback((e: SyncEvent) => {
    if (e.name === 'config.changed' || e.name === 'folder.status' || e.name === 'reconnected') {
      load()
      if (e.name === 'folder.status') setSyncReports(n => n + 1)
    }
  }, [load])
  const live = useSyncEvents(onEvent)

  const reload = async () => {
    await load()
    setGeneration(g => g + 1)
  }

  const remove = async (f: Folder) => {
    if (!confirm(`Stop syncing "${f.name}" on every device? The files on your devices and on the storage stay where they are.`)) return
    try {
      await deleteFolder(f.id)
      if (browsing === f.id) setBrowsing(null)
      await load()
    } catch (err) { setError(errorMessage(err)) }
  }

  const detach = async (f: Folder, d: FolderDevice) => {
    if (!confirm(`Stop syncing "${f.name}" on ${d.deviceName}? Its local files stay.`)) return
    try { await detachFolderDevice(f.id, d.deviceId); await load() } catch (err) { setError(errorMessage(err)) }
  }

  const current = (id: number) => folders?.find(f => f.id === id)
  const editingFolder = typeof editing === 'number' ? current(editing) : undefined
  const browsingFolder = browsing !== null ? current(browsing) : undefined

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
      {error && <p className="error-msg">{error}</p>}
      {folders !== null && backends.length === 0 && (
        <p className="hint">First <Link to="/storage">add storage</Link> where your folders will be kept.</p>
      )}

      {editing !== null && (editing === 'new' || editingFolder) && (
        <FolderForm
          key={`${editing}-${generation}`}
          backends={backends}
          existing={editingFolder}
          onSaved={() => { setEditing(null); load() }}
          onCancel={() => setEditing(null)}
          onReload={reload}
        />
      )}

      {browsingFolder && <SnapshotBrowser key={browsingFolder.id} folder={browsingFolder} refresh={syncReports} onClose={() => setBrowsing(null)} />}

      {folders === null ? <div className="loading">Loading…</div> : folders.length === 0 ? (
        <p className="empty">No folders yet. Add one, then attach the devices that should sync it.</p>
      ) : (
        <div className="backend-list section">
          {folders.map(f => {
            const browsable = f.mode === 'Backup' && f.encryption === 'Transport' && f.repositoryReady
            return (
              <div key={f.id} className="card folder">
                <div className="backend">
                  <div className="backend-main">
                    <div>
                      <strong>{f.name}</strong>
                      <span className="badge">{modeLabel(f.mode)}</span>
                      {f.kind !== 'Files' && <span className="badge">{kindLabel(f.kind)}</span>}
                      {f.encryption === 'EndToEnd' && <span className="badge shared">End-to-end encrypted</span>}
                    </div>
                    <span className="muted small-text">
                      {f.storageBackendName} · <code>{f.rootPath}</code> · every {formatMinutes(f.intervalMinutes)} · folder #{f.id}
                    </span>
                    {f.excludes.length > 0 && <span className="muted small-text">Excludes: {f.excludes.join(', ')}</span>}
                  </div>
                  <div className="row-actions">
                    {browsable && <button className="secondary small" onClick={() => setBrowsing(browsing === f.id ? null : f.id)}>Snapshots</button>}
                    <button className="secondary small" onClick={() => setEditing(f.id)}>Edit</button>
                    <button className="danger small" onClick={() => remove(f)}>Delete</button>
                  </div>
                </div>

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
                    <table>
                      <thead><tr><th>Device</th><th>Folder on the device</th>{f.mode === 'Mirror' && <th>Direction</th>}<th>Last sync</th><th></th></tr></thead>
                      <tbody>
                        {f.devices.map(d => (
                          <tr key={d.deviceId}>
                            <td>{d.deviceName}{d.paused && <span className="badge">Paused</span>}</td>
                            <td><code>{d.localPath}</code></td>
                            {f.mode === 'Mirror' && <td>{d.direction === 'Download' ? 'Download' : 'Upload'}</td>}
                            <td className="status-cell">
                              {d.lastSyncAt === null ? <span className="muted">Never</span> : (
                                <span className={d.lastSyncOk ? 'success-text' : 'error-text'} title={`${formatDate(d.lastSyncAt)}${d.lastSyncMessage ? `\n${d.lastSyncMessage}` : ''}`}>
                                  ● {relativeTime(d.lastSyncAt)}{d.lastSyncMessage && <span className="muted"> · {d.lastSyncMessage}</span>}
                                </span>
                              )}
                            </td>
                            <td className="row-actions">
                              <button className="secondary small" onClick={() => setDeviceEdit({ folderId: f.id, deviceId: d.deviceId })}>Edit</button>
                              <button className="danger small" onClick={() => detach(f, d)}>Remove</button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
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
