import { useEffect, useState } from 'react'
import {
  DEFAULT_PAST_DAYS, describePast, errorMessage, getDeviceCollections, type CollectionSelection, type DeviceCollections,
} from '../api'
import { formatDate, relativeTime } from '../utils/format'

interface Props {
  kind: 'Contacts' | 'Calendars'
  deviceId: string
  deviceName: string
  value: CollectionSelection
  onChange: (value: CollectionSelection) => void
  /** The device is asleep but can be woken (asking for its list wakes it). */
  waking: boolean
}

const PAST: { days: number | null; label: string }[] = [
  { days: 365, label: 'The last year' },
  { days: DEFAULT_PAST_DAYS, label: 'The last 2 years' },
  { days: 1825, label: 'The last 5 years' },
  { days: null, label: 'Everything' },
]

/**
 * A Contacts or Calendars folder's device settings (task 20), like the Photos folder's album picker: everything, or chosen accounts /
 * calendars from the phone's own list (asked live over the server, or the one it reported last while it is offline); calendars also
 * how far back. Replaces the local path of a files folder.
 */
export default function CollectionSelectionFields({ kind, deviceId, deviceName, value, onChange, waking }: Props) {
  const [list, setList] = useState<DeviceCollections | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState('')
  const contacts = kind === 'Contacts'
  const chosen = value.collections ?? []
  const loading = refreshing || (!value.all && list === null && !error)
  const noun = contacts ? 'accounts' : 'calendars'

  // Fetched only when some are to be chosen (a request may wake a phone).
  useEffect(() => {
    if (value.all || list !== null || error) return
    let gone = false
    getDeviceCollections(deviceId, kind)
      .then(l => { if (!gone) setList(l) })
      .catch(err => { if (!gone) setError(errorMessage(err, `The ${noun} could not be listed.`)) })
    return () => { gone = true }
  }, [deviceId, kind, noun, value.all, list, error])

  const load = async () => {
    setRefreshing(true)
    setError('')
    try {
      setList(await getDeviceCollections(deviceId, kind))
    } catch (err) {
      setError(errorMessage(err, `The ${noun} could not be listed.`))
    } finally {
      setRefreshing(false)
    }
  }

  const toggle = (id: string, name: string, on: boolean) =>
    onChange({ ...value, collections: on ? [...chosen.filter(c => c.id !== id), { id, name }] : chosen.filter(c => c.id !== id) })

  const listed = list?.collections ?? []
  const missing = chosen.filter(c => !listed.some(l => l.id === c.id))
  const pastKnown = PAST.some(p => p.days === value.pastDays)

  return (
    <fieldset className="fieldset media-fields">
      <legend>What {deviceName} backs up</legend>
      <div className="choice-row" role="radiogroup" aria-label={contacts ? 'Contacts' : 'Calendars'}>
        <label className="checkbox">
          <input type="radio" name={`pim-all-${deviceId}`} checked={value.all} onChange={() => onChange({ ...value, all: true })} />
          {contacts ? 'All contacts' : 'All calendars'}
        </label>
        <label className="checkbox">
          <input type="radio" name={`pim-all-${deviceId}`} checked={!value.all} onChange={() => onChange({ ...value, all: false })} />
          {contacts ? 'Chosen accounts' : 'Chosen calendars'}
        </label>
      </div>

      {!value.all && (
        <div className="album-picker" aria-label={`${contacts ? 'Accounts' : 'Calendars'} on ${deviceName}`}>
          {loading && <div className="loading">{waking ? `Waking ${deviceName}… it is asleep; this can take up to 30 seconds.` : `Asking ${deviceName} for its ${noun}…`}</div>}
          {error && <div className="notice danger"><p>{error}</p></div>}
          {list && (
            <>
              <div className="album-head">
                {list.live
                  ? <span className="pill pill-success">Live from {deviceName}</span>
                  : <span className="pill" title={list.reportedAt ? formatDate(list.reportedAt) : undefined}>
                      {list.reportedAt ? `Reported ${relativeTime(list.reportedAt)}` : 'No list yet'}
                    </span>}
                <button type="button" className="secondary small" onClick={() => void load()} disabled={loading}>Refresh</button>
              </div>
              {list.message && <p className="hint">{list.message}</p>}
              {listed.length === 0 && missing.length === 0 && (
                <p className="hint">Nothing to show. Back up {contacts ? 'all contacts' : 'all calendars'}, or open ASync on {deviceName} and refresh.</p>
              )}
              <ul className="album-list">
                {listed.map(c => (
                  <li key={c.id}>
                    <label className="checkbox" title={c.readOnly ? (contacts ? 'Backed up; a restore cannot add to it' : 'Read-only: backed up, not restored into') : undefined}>
                      <input type="checkbox" checked={chosen.some(x => x.id === c.id)} onChange={e => toggle(c.id, c.name, e.target.checked)} />
                      {c.color && <span className="calendar-dot" style={{ background: c.color }} aria-hidden="true" />}
                      <span className="album-name">
                        {c.name}
                        <span className="collection-details">
                          {[c.account, `${c.count.toLocaleString()} ${contacts ? (c.count === 1 ? 'contact' : 'contacts') : (c.count === 1 ? 'event' : 'events')}`,
                            c.readOnly ? 'read-only' : null].filter(Boolean).join(' · ')}
                        </span>
                      </span>
                    </label>
                  </li>
                ))}
                {missing.map(c => (
                  <li key={c.id}>
                    <label className="checkbox" title="Chosen before; the device does not list it now (renamed, removed)">
                      <input type="checkbox" checked onChange={e => toggle(c.id, c.name, e.target.checked)} />
                      <span className="album-name">{c.name || c.id}</span>
                      <span className="small-text muted">not on the list now</span>
                    </label>
                  </li>
                ))}
              </ul>
            </>
          )}
          {chosen.length === 0 && list && <p className="hint">Tick at least one {contacts ? 'account' : 'calendar'}.</p>}
        </div>
      )}

      {!contacts && (
        <div className="form-row media-filters">
          <label>
            Events since
            <select value={value.pastDays === null ? 'all' : String(value.pastDays)}
              onChange={e => onChange({ ...value, pastDays: e.target.value === 'all' ? null : Number(e.target.value) })}>
              {!pastKnown && <option value={String(value.pastDays)}>{describePast(value.pastDays)}</option>}
              {PAST.map(p => <option key={p.label} value={p.days === null ? 'all' : String(p.days)}>{p.label}</option>)}
            </select>
          </label>
        </div>
      )}
      <p className="hint">
        {contacts
          ? 'Each contact is kept as a vCard (.vcf) with its photo, by account; any address book can import them. A restore imports them on the phone and never deletes anything there.'
          : 'Each event is kept as an iCalendar (.ics) file with its repetitions, changes, reminders and guests, by calendar. Events that ended before "since" are left out; repeating ones stay while they repeat. A restore imports them and never deletes anything.'}
      </p>
    </fieldset>
  )
}
