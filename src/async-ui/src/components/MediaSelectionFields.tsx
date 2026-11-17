import { useEffect, useState } from 'react'
import { errorMessage, getDeviceAlbums, type DeviceAlbums, type MediaSelection } from '../api'
import { formatDate, relativeTime } from '../utils/format'
import { countText } from '../utils/folders'

interface Props {
  deviceId: string
  deviceName: string
  value: MediaSelection
  onChange: (value: MediaSelection) => void
  /** The device is asleep but can be woken (asking for its albums wakes it). */
  waking: boolean
}

/**
 * A Photos folder's device settings: all photos and videos, or chosen albums (picked from the device's own list, asked live over
 * the server, or the list it reported last while it is offline), and the filters. Replaces the local path of a files folder.
 */
export default function MediaSelectionFields({ deviceId, deviceName, value, onChange, waking }: Props) {
  const [albums, setAlbums] = useState<DeviceAlbums | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState('')
  const chosen = value.albums ?? []
  const loading = refreshing || (!value.all && albums === null && !error)

  // The list is fetched when albums are wanted (a request may wake a phone: not for "All photos").
  useEffect(() => {
    if (value.all || albums !== null || error) return
    let gone = false
    getDeviceAlbums(deviceId)
      .then(a => { if (!gone) setAlbums(a) })
      .catch(err => { if (!gone) setError(errorMessage(err, 'The albums could not be listed.')) })
    return () => { gone = true }
  }, [deviceId, value.all, albums, error])

  const load = async () => {
    setRefreshing(true)
    setError('')
    try {
      setAlbums(await getDeviceAlbums(deviceId))
    } catch (err) {
      setError(errorMessage(err, 'The albums could not be listed.'))
    } finally {
      setRefreshing(false)
    }
  }

  const toggle = (id: string, name: string, on: boolean) =>
    onChange({ ...value, albums: on ? [...chosen.filter(a => a.id !== id), { id, name }] : chosen.filter(a => a.id !== id) })

  const listed = albums?.albums ?? []
  const missing = chosen.filter(a => !listed.some(l => l.id === a.id))

  return (
    <fieldset className="fieldset media-fields">
      <legend>What {deviceName} backs up</legend>
      <div className="choice-row" role="radiogroup" aria-label="Photos and videos">
        <label className="checkbox">
          <input type="radio" name={`media-all-${deviceId}`} checked={value.all} onChange={() => onChange({ ...value, all: true })} />
          All photos and videos
        </label>
        <label className="checkbox">
          <input type="radio" name={`media-all-${deviceId}`} checked={!value.all} onChange={() => onChange({ ...value, all: false })} />
          Chosen albums
        </label>
      </div>

      {!value.all && (
        <div className="album-picker" aria-label={`Albums on ${deviceName}`}>
          {loading && <div className="loading">{waking ? `Waking ${deviceName}… it is asleep; this can take up to 30 seconds.` : `Asking ${deviceName} for its albums…`}</div>}
          {error && <div className="notice danger"><p>{error}</p></div>}
          {albums && (
            <>
              <div className="album-head">
                {albums.live
                  ? <span className="pill pill-success">Live from {deviceName}</span>
                  : <span className="pill" title={albums.reportedAt ? formatDate(albums.reportedAt) : undefined}>
                      {albums.reportedAt ? `Reported ${relativeTime(albums.reportedAt)}` : 'No list yet'}
                    </span>}
                <button type="button" className="secondary small" onClick={() => void load()} disabled={loading}>Refresh</button>
              </div>
              {albums.message && <p className="hint">{albums.message}</p>}
              {listed.length === 0 && missing.length === 0 && (
                <p className="hint">No albums to show. Back up all photos and videos, or open ASync on {deviceName} and refresh.</p>
              )}
              <ul className="album-list">
                {listed.map(a => (
                  <li key={a.id}>
                    <label className="checkbox" title={a.path ?? undefined}>
                      <input type="checkbox" checked={chosen.some(c => c.id === a.id)} onChange={e => toggle(a.id, a.name, e.target.checked)} />
                      <span className="album-name">{a.name}</span>
                      <span className="small-text muted">{countText(a.photos, a.videos)}</span>
                    </label>
                  </li>
                ))}
                {missing.map(a => (
                  <li key={a.id}>
                    <label className="checkbox" title="Chosen before; the device does not list it now (renamed, emptied or deleted)">
                      <input type="checkbox" checked onChange={e => toggle(a.id, a.name, e.target.checked)} />
                      <span className="album-name">{a.name || a.id}</span>
                      <span className="small-text muted">not on the list now</span>
                    </label>
                  </li>
                ))}
              </ul>
            </>
          )}
          {chosen.length === 0 && albums && <p className="hint">Tick at least one album.</p>}
        </div>
      )}

      <div className="form-row media-filters">
        <label>
          Taken since <span className="muted">(optional)</span>
          <input type="date" value={value.since?.slice(0, 10) ?? ''}
            onChange={e => onChange({ ...value, since: e.target.value ? `${e.target.value}T00:00:00Z` : null })} />
        </label>
      </div>
      <label className="checkbox">
        <input type="checkbox" checked={value.includeVideos} onChange={e => onChange({ ...value, includeVideos: e.target.checked })} /> Videos too
      </label>
      <label className="checkbox">
        <input type="checkbox" checked={value.includeScreenshots} onChange={e => onChange({ ...value, includeScreenshots: e.target.checked })} /> Screenshots too
      </label>
      <p className="hint">
        Originals are kept as they are (no re-encoding), with a small file of their details beside each. A photo in several albums is
        stored once. Photos deleted on the phone stay in older snapshots for as long as the folder keeps them.
      </p>
    </fieldset>
  )
}
