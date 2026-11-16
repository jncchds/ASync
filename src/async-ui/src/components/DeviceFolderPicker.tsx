import { useCallback, useEffect, useRef, useState } from 'react'
import { browseDevice, browseDeviceMkdir, errorMessage, type BrowseEntry, type BrowseListing, type BrowsePlace } from '../api'
import { formatBytes } from '../utils/format'

interface Props {
  deviceId: string
  deviceName: string
  /** The device is asleep: the first request wakes it (UnifiedPush) and may take up to 30 seconds. */
  waking?: boolean
  /** Where to start: the path typed in the form, else the device's home. */
  initialPath?: string
  onPick: (path: string) => void
  onClose: () => void
}

const PLACE_ICONS: Record<BrowsePlace['kind'], string> = {
  Home: '🏠', Desktop: '🖥️', Documents: '📄', Downloads: '⬇️', Pictures: '🖼️', Music: '🎵', Videos: '🎬',
  Drive: '💽', Volume: '💽', Storage: '📱', Root: '🗂️',
}

/**
 * Picks a directory on an online device: the device lists it over its live connection (nothing is stored). Starting
 * points, breadcrumbs, Back and Up, directories to open, files greyed so the place is recognisable, a new folder, and
 * "Use this folder". Typing the path in the form stays possible (offline devices).
 */
export default function DeviceFolderPicker({ deviceId, deviceName, waking, initialPath, onPick, onClose }: Props) {
  const [listing, setListing] = useState<BrowseListing | null>(null)
  const [history, setHistory] = useState<string[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [unavailable, setUnavailable] = useState(false)
  const [showFiles, setShowFiles] = useState(true)
  const [showHidden, setShowHidden] = useState(false)
  const [newName, setNewName] = useState('')
  const [creating, setCreating] = useState(false)
  const request = useRef(0)
  const current = useRef<BrowseListing | null>(null)

  /** Asks the device for a directory; the caller has set `loading` (the first render starts with it). */
  const fetchListing = useCallback(async (path: string | undefined, push: boolean, fallback: boolean) => {
    const id = ++request.current
    const statusOf = (err: unknown) => (err as { response?: { status?: number } }).response?.status
    try {
      let next: BrowseListing
      try {
        next = await browseDevice(deviceId, path)
      } catch (err) {
        // A typed path that is not there (any more): start at the device's home instead.
        const status = statusOf(err)
        if (!(fallback && path && (status === 404 || status === 400 || status === 422))) throw err
        next = await browseDevice(deviceId)
      }
      if (id !== request.current) return
      const prev = current.current
      if (push && prev && prev.path !== next.path) setHistory(h => [...h, prev.path])
      current.current = next
      setListing(next)
      setUnavailable(false)
    } catch (err) {
      if (id !== request.current) return
      const status = statusOf(err)
      setError(errorMessage(err, 'The device did not list the folder.'))
      setUnavailable(status === 409 || status === 504 || status === 502)
    } finally {
      if (id === request.current) setLoading(false)
    }
  }, [deviceId])

  const open = (path: string | undefined, push = true) => {
    setLoading(true)
    setError('')
    void fetchListing(path, push, false)
  }

  const [start] = useState(initialPath?.trim() || undefined) // the form's path when the picker opened, not as it is typed later
  useEffect(() => { void fetchListing(start, false, true) }, [fetchListing, start])

  const back = () => {
    const previous = history[history.length - 1]
    if (previous === undefined) return
    setHistory(h => h.slice(0, -1))
    void open(previous, false)
  }

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!listing || !newName.trim()) return
    setLoading(true)
    setError('')
    try {
      const next = await browseDeviceMkdir(deviceId, listing.path, newName.trim())
      setHistory(h => [...h, listing.path])
      current.current = next
      setListing(next)
      setNewName('')
      setCreating(false)
    } catch (err) {
      setError(errorMessage(err, 'The folder could not be created.'))
    } finally {
      setLoading(false)
    }
  }

  const entries = listing?.entries.filter(e => (showHidden || !e.hidden) && (showFiles || e.kind !== 'File')) ?? []
  const hiddenCount = listing ? listing.entries.filter(e => e.hidden).length : 0

  return (
    <div className="picker" role="dialog" aria-label={`Choose a folder on ${deviceName}`}>
      <div className="picker-head">
        <h3>Choose a folder on {deviceName}</h3>
        <button type="button" className="secondary small" onClick={onClose}>Close</button>
      </div>

      {listing && listing.places.length > 0 && (
        <div className="picker-places" aria-label="Starting points">
          {listing.places.map(p => (
            <button type="button" key={p.path} className={`secondary small${listing.path === p.path ? ' active' : ''}`}
              onClick={() => open(p.path)} title={p.path}>
              {PLACE_ICONS[p.kind]} {p.label}
            </button>
          ))}
        </div>
      )}

      {listing && (
        <div className="picker-toolbar">
          <button type="button" className="secondary small" onClick={back} disabled={history.length === 0 || loading} title="Back">← Back</button>
          <button type="button" className="secondary small" onClick={() => listing.parent && open(listing.parent)}
            disabled={!listing.parent || loading} title="Up one level">↑ Up</button>
          <nav className="crumbs" aria-label="Path">
            {listing.crumbs.map((c, i) => (
              <span key={c.path}>
                {i > 0 && <span className="muted"> › </span>}
                {i === listing.crumbs.length - 1
                  ? <strong>{c.label}</strong>
                  : <button type="button" className="link" onClick={() => open(c.path)}>{c.label}</button>}
              </span>
            ))}
          </nav>
        </div>
      )}

      {error && (
        <div className={`notice${unavailable ? '' : ' danger'}`}>
          <p>{error}</p>
          {unavailable && <p className="hint">You can still type the folder's path in the form.</p>}
          {!unavailable && listing === null && (
            <div className="actions"><button type="button" className="secondary small" onClick={() => open(undefined, false)}>Start at home</button></div>
          )}
        </div>
      )}

      {loading && !listing && (
        <div className="loading">
          {waking ? `Waking ${deviceName}… it is asleep; this can take up to 30 seconds.` : `Asking ${deviceName}…`}
        </div>
      )}

      {listing && (
        <>
          <code className="picker-path" title={listing.path}>{listing.path}</code>
          <ul className={`picker-list${loading ? ' busy' : ''}`} aria-busy={loading}>
            {entries.length === 0 && <li className="muted picker-empty">{listing.entries.length === 0 ? 'Empty folder' : 'Only hidden entries'}</li>}
            {entries.map(e => <Entry key={`${e.kind}:${e.name}`} entry={e} onOpen={p => open(p)} disabled={loading} />)}
            {listing.more > 0 && <li className="muted picker-empty">… and {listing.more.toLocaleString()} more</li>}
          </ul>
          <div className="picker-options">
            <label className="checkbox"><input type="checkbox" checked={showFiles} onChange={e => setShowFiles(e.target.checked)} /> Show files</label>
            {hiddenCount > 0 && (
              <label className="checkbox">
                <input type="checkbox" checked={showHidden} onChange={e => setShowHidden(e.target.checked)} /> Show hidden ({hiddenCount})
              </label>
            )}
          </div>
          {creating ? (
            <form className="inline-form" onSubmit={create}>
              <input autoFocus value={newName} onChange={e => setNewName(e.target.value)} placeholder="New folder name" maxLength={255} aria-label="New folder name" />
              <button type="submit" className="small" disabled={loading || !newName.trim()}>Create</button>
              <button type="button" className="secondary small" onClick={() => { setCreating(false); setNewName('') }}>Cancel</button>
            </form>
          ) : null}
          <div className="actions">
            <button type="button" className="small" onClick={() => onPick(listing.path)} disabled={loading}>Use this folder</button>
            {!creating && <button type="button" className="secondary small" onClick={() => setCreating(true)} disabled={loading}>New folder here…</button>}
            <button type="button" className="secondary small" onClick={onClose}>Cancel</button>
          </div>
        </>
      )}
    </div>
  )
}

function Entry({ entry: e, onOpen, disabled }: { entry: BrowseEntry; onOpen: (path: string) => void; disabled: boolean }) {
  if (e.kind === 'Directory' && !e.unreadable && e.path)
    return (
      <li>
        <button type="button" className="picker-dir" onClick={() => onOpen(e.path!)} disabled={disabled}>
          <span aria-hidden>📁</span> <span className="picker-name">{e.name}</span>
        </button>
      </li>
    )
  if (e.kind === 'Directory')
    return <li className="picker-other" title="This device cannot read this folder"><span aria-hidden>🔒</span> <span className="picker-name">{e.name}</span> <span className="small-text">cannot be read</span></li>
  if (e.kind === 'Symlink')
    return (
      <li className="picker-other" title={`A link to ${e.linkTarget ?? '?'} (links are not followed)`}>
        <span aria-hidden>↪</span> <span className="picker-name">{e.name}</span> <span className="small-text">→ {e.linkTarget}</span>
      </li>
    )
  return (
    <li className="picker-other">
      <span aria-hidden>📄</span> <span className="picker-name">{e.name}</span>
      {e.size !== null && <span className="small-text">{formatBytes(e.size)}</span>}
    </li>
  )
}
