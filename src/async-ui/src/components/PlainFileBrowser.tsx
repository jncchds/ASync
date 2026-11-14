import { useCallback, useEffect, useState } from 'react'
import {
  downloadPlain, emptyPlainTrash, errorMessage, getPlainDirectory, getPlainHistory, getPlainTrash, getPlainVersionedFiles,
  restoreConflicts, restorePlain,
  type Folder, type PlainDirectory, type PlainFileHistory, type PlainRestoreConflict, type PlainRestoreItem, type PlainSource,
  type PlainTrash, type PlainVersionedFile,
} from '../api'
import { formatBytes, formatDate, relativeTime } from '../utils/format'

interface Props {
  folder: Folder
  /** Bumped by the page when a device reports a sync, so the listing follows the storage. */
  refresh: number
  onClose: () => void
}

type Tab = 'files' | 'versions' | 'trash'

/** A restore that hit existing files: what was asked, and which paths are in the way. */
interface PendingConflict { items: PlainRestoreItem[]; conflicts: string[] }

const baseName = (path: string) => path.slice(path.lastIndexOf('/') + 1)

/**
 * The files of a Mirror/TwoWay folder as they are on the storage, with each file's earlier versions and the trash.
 * Restores go through the folder's lease (refused while a device syncs) and the devices then pull the result.
 */
export default function PlainFileBrowser({ folder, refresh, onClose }: Props) {
  const [tab, setTab] = useState<Tab>('files')
  const [dir, setDir] = useState<PlainDirectory | null>(null)
  const [path, setPath] = useState('')
  const [versioned, setVersioned] = useState<PlainVersionedFile[] | null>(null)
  const [trash, setTrash] = useState<PlainTrash | null>(null)
  /** The file whose history is open (in the Files or Versions tab). */
  const [historyOf, setHistoryOf] = useState<string | null>(null)
  const [history, setHistory] = useState<PlainFileHistory | null>(null)
  const [conflict, setConflict] = useState<PendingConflict | null>(null)
  /** Key of the row whose Restore button waits for a second click. */
  const [confirming, setConfirming] = useState<string | null>(null)
  const [confirmEmpty, setConfirmEmpty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [done, setDone] = useState<{ message: string; warning: string | null } | null>(null)
  /** Bumped after a restore or an emptied trash, so every view reloads. */
  const [changes, setChanges] = useState(0)

  const uploaders = folder.mode === 'Mirror'
    ? folder.devices.filter(d => d.direction === 'Upload' && !d.paused).map(d => d.deviceName)
    : []

  useEffect(() => {
    if (tab !== 'files') return
    getPlainDirectory(folder.id, path)
      .then(d => { setDir(d); setError('') })
      .catch(err => setError(errorMessage(err, 'Could not open that directory.')))
  }, [folder.id, tab, path, refresh, changes])

  useEffect(() => {
    if (tab !== 'versions') return
    getPlainVersionedFiles(folder.id)
      .then(v => { setVersioned(v); setError('') })
      .catch(err => setError(errorMessage(err, 'Could not load the versions.')))
  }, [folder.id, tab, refresh, changes])

  useEffect(() => {
    if (tab !== 'trash') return
    getPlainTrash(folder.id)
      .then(t => { setTrash(t); setError('') })
      .catch(err => setError(errorMessage(err, 'Could not load the trash.')))
  }, [folder.id, tab, refresh, changes])

  useEffect(() => {
    if (!historyOf) return
    getPlainHistory(folder.id, historyOf)
      .then(setHistory)
      .catch(err => setError(errorMessage(err, 'Could not load the versions of that file.')))
  }, [folder.id, historyOf, refresh, changes])

  const switchTab = (t: Tab) => {
    setTab(t)
    setHistoryOf(null)
    setConfirming(null)
    setConfirmEmpty(false)
    setConflict(null)
  }

  const openDir = (p: string) => {
    setPath(p)
    setHistoryOf(null)
  }

  const download = async (source: PlainSource, p: string, stamp?: string, zip = false) => {
    setError('')
    try { await downloadPlain(folder.id, source, p, stamp, zip) }
    catch (err) { setError(errorMessage(err, 'Could not start the download.')) }
  }

  const restore = useCallback(async (items: PlainRestoreItem[], onConflict: PlainRestoreConflict = 'Refuse') => {
    setBusy(true)
    setError('')
    setDone(null)
    setConfirming(null)
    try {
      const result = await restorePlain(folder.id, items, onConflict)
      setConflict(null)
      const to = result.restoredTo
      setDone({
        message: to.length === 1 ? `Restored “${to[0]}”. The folder's devices pull it on their next sync.`
          : `Restored ${to.length} files. The folder's devices pull them on their next sync.`,
        warning: result.warning,
      })
      setChanges(c => c + 1)
    } catch (err) {
      const conflicts = restoreConflicts(err)
      if (conflicts) setConflict({ items, conflicts })
      else setError(errorMessage(err, 'Could not restore.'))
    } finally {
      setBusy(false)
    }
  }, [folder.id])

  const empty = async () => {
    setBusy(true)
    setError('')
    setDone(null)
    try {
      const { deleted } = await emptyPlainTrash(folder.id)
      setDone({ message: `Deleted ${deleted} file${deleted === 1 ? '' : 's'} from the trash for good.`, warning: null })
      setConfirmEmpty(false)
      setChanges(c => c + 1)
    } catch (err) { setError(errorMessage(err, 'Could not empty the trash.')) }
    finally { setBusy(false) }
  }

  /** A Restore button that asks for a second click. */
  const restoreButton = (id: string, items: PlainRestoreItem[], label = 'Restore') =>
    confirming === id ? (
      <>
        <button type="button" className="small" disabled={busy} onClick={() => restore(items)}>Confirm</button>
        <button type="button" className="secondary small" onClick={() => setConfirming(null)}>Cancel</button>
      </>
    ) : (
      <button type="button" className="secondary small" disabled={busy} onClick={() => setConfirming(id)}>{label}</button>
    )

  const crumbs = path.split('/').filter(Boolean)

  const historyPanel = historyOf && (
    <div className="subform history-panel">
      <div className="tree-toolbar">
        <strong className="crumbs">🕘 Versions of <code>{historyOf}</code></strong>
        <button type="button" className="secondary small" onClick={() => setHistoryOf(null)}>Close</button>
      </div>
      {history?.path !== historyOf ? <div className="loading">Loading…</div> : (
        <>
          <p className="muted small-text">
            {history.exists
              ? <>Current file: {formatBytes(history.size ?? 0)}{history.modified && <>, modified {formatDate(history.modified)}</>}. Restoring a version keeps the current file as a version.</>
              : <>The file does not exist now (look for it in the trash). Restoring a version puts it back.</>}
          </p>
          {history.versions.length === 0 ? <p className="empty">No earlier versions.</p> : (
            <div className="table-wrap inset">
              <table className="stack">
                <thead><tr><th>Replaced</th><th>Size</th><th>Modified</th><th></th></tr></thead>
                <tbody>
                  {history.versions.map(v => (
                    <tr key={v.stamp}>
                      <td className="title-cell"><span title={formatDate(v.replacedAt)}>{formatDate(v.replacedAt)} <span className="muted">· {relativeTime(v.replacedAt)}</span></span></td>
                      <td data-label="Size" className="muted nowrap"><span>{formatBytes(v.size)}</span></td>
                      <td data-label="Modified" className="muted nowrap">{v.modified ? <span>{formatDate(v.modified)}</span> : null}</td>
                      <td className="actions-cell">
                        <div className="row-actions">
                          <button type="button" className="secondary small" onClick={() => download('Version', history.path, v.stamp)}>Download</button>
                          {restoreButton(`v:${v.stamp}`, [{ source: 'Version', path: history.path, stamp: v.stamp }])}
                        </div>
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
  )

  return (
    <div className="card section plain-browser">
      <div className="header">
        <h2>📂 Files of {folder.name}</h2>
        <button type="button" className="secondary small" onClick={onClose}>Close</button>
      </div>

      <div className="tabs" role="tablist">
        {(['files', 'versions', 'trash'] as Tab[]).map(t => (
          <button key={t} type="button" role="tab" aria-selected={tab === t} className={`tab${tab === t ? ' selected' : ''}`} onClick={() => switchTab(t)}>
            {t === 'files' ? 'Files' : t === 'versions' ? 'Versions' : 'Trash'}
          </button>
        ))}
      </div>

      {uploaders.length > 0 && (tab !== 'files' || historyOf) && (
        <p className="notice">
          <strong>{uploaders.join(', ')}</strong> upload{uploaders.length === 1 ? 's' : ''} this folder as a mirror. On {uploaders.length === 1 ? 'its' : 'their'} next
          run the storage is made equal to {uploaders.length === 1 ? 'that device\'s' : 'their'} local folder, so a restored file is replaced or deleted
          again. Download the file and copy it onto that device instead, or restore and pause that device first.
        </p>
      )}
      {error && <p className="error-msg">{error}</p>}
      {done && (
        <div>
          <p className="success-msg">{done.message}</p>
          {done.warning && <p className="notice">{done.warning}</p>}
        </div>
      )}
      {conflict && (
        <div className="notice">
          <p>
            {conflict.conflicts.length === 1
              ? <>A file exists at <code>{conflict.conflicts[0]}</code> now.</>
              : <>{conflict.conflicts.length} files exist at their old paths now: {conflict.conflicts.map((c, i) => <span key={c}>{i > 0 && ', '}<code>{c}</code></span>)}.</>}
          </p>
          <div className="actions">
            <button type="button" className="small" disabled={busy} onClick={() => restore(conflict.items, 'Rename')}>Keep both (restore under a new name)</button>
            <button type="button" className="secondary small" disabled={busy} onClick={() => restore(conflict.items, 'Replace')}>Replace (the current file becomes a version)</button>
            <button type="button" className="secondary small" onClick={() => setConflict(null)}>Cancel</button>
          </div>
        </div>
      )}

      {tab === 'files' && (
        <div className="snapshot-tree">
          <div className="tree-toolbar">
            <nav className="crumbs">
              <button type="button" className="link" onClick={() => openDir('')}>{folder.name}</button>
              {crumbs.map((c, i) => (
                <span key={i}>
                  {' / '}
                  <button type="button" className="link" onClick={() => openDir(crumbs.slice(0, i + 1).join('/'))}>{c}</button>
                </span>
              ))}
            </nav>
            {dir && dir.entries.length > 0 && <button type="button" className="secondary small" onClick={() => download('Current', dir.path, undefined, true)}>Download as ZIP</button>}
          </div>
          {historyPanel}
          {!dir ? <div className="loading">Loading…</div> : (
            <div className="table-wrap">
              <table className="stack">
                <thead><tr><th>Name</th><th>Size</th><th>Modified</th><th></th></tr></thead>
                <tbody>
                  {dir.entries.length === 0 && (
                    <tr><td colSpan={4} className="muted">{dir.path ? 'Empty directory' : 'No files on the storage yet. They appear after a device syncs this folder.'}</td></tr>
                  )}
                  {dir.entries.map(e => (
                    <tr key={e.name}>
                      <td className="title-cell">
                        {e.isDirectory
                          ? <button type="button" className="link" onClick={() => openDir(e.path)}>📁 {e.name}</button>
                          : <span>📄 {e.name}</span>}
                      </td>
                      <td data-label="Size" className="muted nowrap">{e.isDirectory ? null : <span>{formatBytes(e.size)}</span>}</td>
                      <td data-label="Modified" className="muted nowrap">{e.modified ? <span>{formatDate(e.modified)}</span> : null}</td>
                      <td className="actions-cell">
                        <div className="row-actions">
                          {e.isDirectory
                            ? <button type="button" className="secondary small" onClick={() => download('Current', e.path, undefined, true)}>ZIP</button>
                            : <button type="button" className="secondary small" onClick={() => download('Current', e.path)}>Download</button>}
                          {e.versions > 0 && (
                            <button type="button" className={`secondary small${historyOf === e.path ? ' active' : ''}`} onClick={() => setHistoryOf(historyOf === e.path ? null : e.path)}>
                              Versions ({e.versions})
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'versions' && (
        <div className="snapshot-tree">
          <p className="hint">Files whose earlier versions are kept (also files that were deleted since). Versions are kept for {folder.versionsKeepDays} days.</p>
          {historyPanel}
          {versioned === null ? <div className="loading">Loading…</div> : versioned.length === 0 ? (
            <p className="empty">No earlier versions yet. When a device replaces a file on the storage, the old one is kept here.</p>
          ) : (
            <div className="table-wrap">
              <table className="stack">
                <thead><tr><th>File</th><th>Versions</th><th>Last replaced</th><th></th></tr></thead>
                <tbody>
                  {versioned.map(v => (
                    <tr key={v.path}>
                      <td className="title-cell wrap"><span>📄 {v.path}</span></td>
                      <td data-label="Versions" className="muted nowrap"><span>{v.versions} · {formatBytes(v.versionsSize)}</span></td>
                      <td data-label="Last replaced" className="muted nowrap"><span title={formatDate(v.latestReplacedAt)}>{relativeTime(v.latestReplacedAt)}</span></td>
                      <td className="actions-cell">
                        <div className="row-actions">
                          <button type="button" className="secondary small" onClick={() => setHistoryOf(historyOf === v.path ? null : v.path)}>
                            {historyOf === v.path ? 'Hide' : 'Show'}
                          </button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'trash' && (
        <div className="snapshot-tree">
          {trash === null ? <div className="loading">Loading…</div> : trash.groups.length === 0 ? (
            <p className="empty">The trash is empty. Files deleted by a device are kept here for {folder.trashKeepDays} days.</p>
          ) : (
            <>
              <div className="tree-toolbar">
                <span className="muted small-text">{trash.files} file{trash.files === 1 ? '' : 's'} · {formatBytes(trash.size)} · kept for {folder.trashKeepDays} days</span>
                {confirmEmpty ? (
                  <div className="row-actions">
                    <span className="small-text error-text">Delete {trash.files} file{trash.files === 1 ? '' : 's'} for good?</span>
                    <button type="button" className="danger small" disabled={busy} onClick={empty}>Empty trash</button>
                    <button type="button" className="secondary small" onClick={() => setConfirmEmpty(false)}>Cancel</button>
                  </div>
                ) : <button type="button" className="danger small" onClick={() => setConfirmEmpty(true)}>Empty trash…</button>}
              </div>
              {trash.groups.map(g => {
                const items: PlainRestoreItem[] = g.items.map(i => ({ source: 'Trash', path: i.path, stamp: i.stamp }))
                return (
                  <div key={g.deletedAt} className="trash-group">
                    <div className="tree-toolbar">
                      <strong className="small-text">Deleted {formatDate(g.deletedAt)} <span className="muted">· {relativeTime(g.deletedAt)} · {g.items.length} file{g.items.length === 1 ? '' : 's'}</span></strong>
                      {g.items.length > 1 && <div className="row-actions">{restoreButton(`g:${g.deletedAt}`, items, 'Restore all')}</div>}
                    </div>
                    <div className="table-wrap">
                      <table className="stack">
                        <thead><tr><th>File</th><th>Size</th><th></th></tr></thead>
                        <tbody>
                          {g.items.map(i => (
                            <tr key={`${i.stamp}/${i.path}`}>
                              <td className="title-cell wrap"><span title={i.path}>📄 {baseName(i.path)}{i.path.includes('/') && <span className="muted"> in {i.path.slice(0, i.path.lastIndexOf('/'))}</span>}</span></td>
                              <td data-label="Size" className="muted nowrap"><span>{formatBytes(i.size)}</span></td>
                              <td className="actions-cell">
                                <div className="row-actions">
                                  <button type="button" className="secondary small" onClick={() => download('Trash', i.path, i.stamp)}>Download</button>
                                  {restoreButton(`t:${i.stamp}/${i.path}`, [{ source: 'Trash', path: i.path, stamp: i.stamp }])}
                                </div>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                )
              })}
            </>
          )}
        </div>
      )}
    </div>
  )
}
