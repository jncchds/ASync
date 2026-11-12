import { useCallback, useEffect, useState } from 'react'
import {
  downloadFromSnapshot, errorMessage, getSnapshots, getSnapshotTree,
  type Folder, type Snapshot, type SnapshotTree,
} from '../api'
import { formatBytes, formatDate, relativeTime } from '../utils/format'

interface Props {
  folder: Folder
  /** Bumped by the page when a device reports a sync, so new snapshots show up. */
  refresh: number
  onClose: () => void
}

/** Lists a Transport Backup folder's snapshots, walks their directories and downloads files or whole directories. */
export default function SnapshotBrowser({ folder, refresh, onClose }: Props) {
  const [snapshots, setSnapshots] = useState<Snapshot[] | null>(null)
  const [selected, setSelected] = useState<Snapshot | null>(null)
  const [tree, setTree] = useState<SnapshotTree | null>(null)
  const [loadingTree, setLoadingTree] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    getSnapshots(folder.id)
      .then(list => { setSnapshots(list); setError('') })
      .catch(err => setError(errorMessage(err, 'Could not load the snapshots.')))
  }, [folder.id, refresh])

  const open = useCallback(async (snapshot: Snapshot, path: string) => {
    setSelected(snapshot)
    setLoadingTree(true)
    setError('')
    try { setTree(await getSnapshotTree(folder.id, snapshot.id, path)) }
    catch (err) { setError(errorMessage(err, 'Could not open that directory.')) }
    finally { setLoadingTree(false) }
  }, [folder.id])

  const download = async (path: string, zip: boolean) => {
    if (!selected) return
    setError('')
    try { await downloadFromSnapshot(folder.id, selected.id, path, zip) }
    catch (err) { setError(errorMessage(err, 'Could not start the download.')) }
  }

  const crumbs = tree ? tree.path.split('/').filter(Boolean) : []

  return (
    <div className="card section">
      <div className="header">
        <h2>Snapshots of {folder.name}</h2>
        <button type="button" className="secondary small" onClick={onClose}>Close</button>
      </div>
      {error && <p className="error-msg">{error}</p>}

      {snapshots === null ? <div className="loading">Loading…</div> : snapshots.length === 0 ? (
        <p className="empty">No snapshots yet. They appear after a device's first backup of this folder.</p>
      ) : (
        <div className="snapshot-browser">
          <ul className="snapshot-list">
            {snapshots.map(s => (
              <li key={s.id}>
                <button type="button" className={`snapshot-item${selected?.id === s.id ? ' selected' : ''}`} onClick={() => open(s, '')}>
                  <strong title={formatDate(s.time)}>{formatDate(s.time)}</strong>
                  <span className="muted">
                    {s.deviceName || s.hostname || 'Removed device'} · {relativeTime(s.time)}
                  </span>
                  <span className="muted">
                    {s.filesNew + s.filesChanged + s.filesUnmodified} files · {formatBytes(s.bytesProcessed)}
                    {s.filesNew + s.filesChanged > 0 && ` · ${s.filesNew + s.filesChanged} new or changed`}
                    {s.errors > 0 && <span className="error-text"> · {s.errors} unreadable</span>}
                  </span>
                </button>
              </li>
            ))}
          </ul>

          <div className="snapshot-tree">
            {!selected ? <p className="empty">Choose a snapshot to see its files.</p> : (
              <>
                <div className="tree-toolbar">
                  <nav className="crumbs">
                    <button type="button" className="link" onClick={() => open(selected, '')}>{folder.name}</button>
                    {crumbs.map((c, i) => (
                      <span key={i}>
                        {' / '}
                        <button type="button" className="link" onClick={() => open(selected, crumbs.slice(0, i + 1).join('/'))}>{c}</button>
                      </span>
                    ))}
                  </nav>
                  {tree && <button type="button" className="secondary small" onClick={() => download(tree.path, true)}>Download as ZIP</button>}
                </div>
                {loadingTree && !tree ? <div className="loading">Loading…</div> : tree && (
                  <div className="table-wrap">
                    <table>
                      <thead><tr><th>Name</th><th>Size</th><th>Modified</th><th></th></tr></thead>
                      <tbody>
                        {tree.nodes.length === 0 && <tr><td colSpan={4} className="muted">Empty directory</td></tr>}
                        {tree.nodes.map(n => (
                          <tr key={n.name}>
                            <td className="name-cell">
                              {n.type === 'Directory'
                                ? <button type="button" className="link" onClick={() => open(selected, n.path)}>📁 {n.name}</button>
                                : n.type === 'Symlink'
                                  ? <span className="muted" title={n.linkTarget ?? undefined}>↪ {n.name}</span>
                                  : <span>{n.name}</span>}
                            </td>
                            <td className="muted">{n.type === 'File' ? formatBytes(n.size) : ''}</td>
                            <td className="muted">{formatDate(n.modified)}</td>
                            <td className="row-actions">
                              {n.type === 'File' && <button type="button" className="secondary small" onClick={() => download(n.path, false)}>Download</button>}
                              {n.type === 'Directory' && <button type="button" className="secondary small" onClick={() => download(n.path, true)}>ZIP</button>}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
