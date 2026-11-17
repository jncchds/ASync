import { useState } from 'react'
import {
  attachFolderDevice, BROWSE_HINTS, errorMessage, EVERYTHING, isConflict, type Device, type Folder, type FolderDevice, type MediaSelection,
  type MirrorDirection,
} from '../api'
import DeviceFolderPicker from './DeviceFolderPicker'
import MediaSelectionFields from './MediaSelectionFields'
import { hasPhotoLibrary } from '../utils/folders'

interface Props {
  folder: Folder
  /** The user's active devices, for adding one. */
  devices: Device[]
  /** Editing this device's settings; absent = adding a device. */
  existing?: FolderDevice
  onSaved: () => void
  onCancel: () => void
  onReload: () => void
}

/** Adds a device to a folder, or changes where and how it syncs there. */
export default function FolderDeviceForm({ folder, devices, existing, onSaved, onCancel, onReload }: Props) {
  const photos = folder.kind === 'Photos'
  const available = devices.filter(d => !folder.devices.some(m => m.deviceId === d.id) && (!photos || hasPhotoLibrary(d)))
  const [media, setMedia] = useState<MediaSelection>(existing?.media ?? EVERYTHING)
  const [startRevision] = useState(folder.revision)
  const [deviceId, setDeviceId] = useState(existing?.deviceId ?? available[0]?.id ?? '')
  const [localPath, setLocalPath] = useState(existing?.localPath ?? '')
  const [direction, setDirection] = useState<MirrorDirection>(existing?.direction ?? 'Upload')
  const [paused, setPaused] = useState(existing?.paused ?? false)
  const [unmeteredOnly, setUnmeteredOnly] = useState(existing?.conditions?.unmeteredOnly ?? false)
  const [chargingOnly, setChargingOnly] = useState(existing?.conditions?.chargingOnly ?? false)
  const [batteryNotLow, setBatteryNotLow] = useState(existing?.conditions?.batteryNotLow ?? false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [conflict, setConflict] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const device = devices.find(d => d.id === deviceId)
  const deviceName = existing?.deviceName ?? device?.name ?? 'the device'
  // Older servers send no availability: let the device's answer say why it cannot be browsed.
  const browse = device?.browse ?? (device ? 'Available' : null)
  const placeholder = device?.platform === 'Windows' ? 'C:\\Users\\me\\Documents'
    : device?.platform === 'Android' ? '/storage/emulated/0/DCIM' : '/home/me/Documents'

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (photos && !media.all && (media.albums ?? []).length === 0) { setError('Choose at least one album, or back up all photos and videos.'); return }
    setBusy(true)
    setError('')
    setConflict(false)
    try {
      await attachFolderDevice(folder.id, {
        deviceId, localPath: photos ? '' : localPath.trim(), direction: folder.mode === 'Mirror' ? direction : null, paused,
        expectedRevision: startRevision, conditions: { unmeteredOnly, chargingOnly, batteryNotLow },
        ...(photos ? { media: { ...media, albums: media.all ? [] : media.albums } } : {}),
      })
      onSaved()
    } catch (err) {
      if (isConflict(err)) setConflict(true)
      else setError(errorMessage(err, 'Could not save.'))
    } finally {
      setBusy(false)
    }
  }

  if (!existing && available.length === 0)
    return (
      <div className="subform">
        <p className="hint">
          {photos
            ? 'All your phones are already in this folder (only phones back up their photos). Add a phone on the Devices page.'
            : 'All your devices are already in this folder. Add more devices on the Devices page.'}
        </p>
        <button type="button" className="secondary small" onClick={onCancel}>Close</button>
      </div>
    )

  return (
    <form className="subform" onSubmit={save}>
      <div className="form-row">
        <label>
          Device
          <select value={deviceId} disabled={existing !== undefined} onChange={e => { setDeviceId(e.target.value); setBrowsing(false) }} required>
            {existing
              ? <option value={existing.deviceId}>{existing.deviceName}</option>
              : available.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </label>
        {!photos && <label className="path-field">
          Folder on the device
          <span className="input-with-button">
            <input value={localPath} onChange={e => setLocalPath(e.target.value)} required maxLength={1000} placeholder={placeholder} />
            <button type="button" className="secondary small" onClick={() => setBrowsing(b => !b)} disabled={!deviceId}
              title={browse && browse !== 'Available' ? BROWSE_HINTS[browse] : 'Pick the folder on the device'}
              aria-expanded={browsing}>
              Browse…
            </button>
          </span>
          {browse && browse !== 'Available' && !browsing && (
            <span className="hint">{BROWSE_HINTS[browse]}{browse === 'Wakeable' ? '' : ' Type the path instead.'}</span>
          )}
        </label>}
        {folder.mode === 'Mirror' && (
          <label>
            Direction
            <select value={direction} onChange={e => setDirection(e.target.value as MirrorDirection)}>
              <option value="Upload">Upload: this device is the original</option>
              <option value="Download">Download: this device gets a copy</option>
            </select>
          </label>
        )}
      </div>
      {photos && deviceId && (
        <MediaSelectionFields key={deviceId} deviceId={deviceId} deviceName={deviceName} value={media} onChange={setMedia}
          waking={browse === 'Wakeable'} />
      )}
      {!photos && browsing && deviceId && (
        <DeviceFolderPicker
          key={deviceId}
          deviceId={deviceId}
          deviceName={deviceName}
          waking={browse === 'Wakeable'}
          initialPath={localPath}
          onPick={p => { setLocalPath(p); setBrowsing(false) }}
          onClose={() => setBrowsing(false)}
        />
      )}
      <label className="checkbox">
        <input type="checkbox" checked={paused} onChange={e => setPaused(e.target.checked)} /> Paused
      </label>
      <fieldset className="fieldset">
        <legend>Run by itself only</legend>
        <label className="checkbox">
          <input type="checkbox" checked={unmeteredOnly} onChange={e => setUnmeteredOnly(e.target.checked)} /> On Wi-Fi (not on mobile data or a metered hotspot)
        </label>
        <label className="checkbox">
          <input type="checkbox" checked={chargingOnly} onChange={e => setChargingOnly(e.target.checked)} /> While charging
        </label>
        <label className="checkbox">
          <input type="checkbox" checked={batteryNotLow} onChange={e => setBatteryNotLow(e.target.checked)} /> When the battery is not low
        </label>
        <p className="hint">
          Scheduled runs, file changes and wake-ups wait for these; Sync now on the device does not. Phones honour all three;
          computers the power rules, and metered networks on Linux (NetworkManager).
        </p>
      </fieldset>
      {conflict && (
        <p className="error-msg">
          The folder was changed meanwhile. <button type="button" className="secondary small" onClick={onReload}>Reload</button> and
          apply your change again.
        </p>
      )}
      {error && <p className="error-msg">{error}</p>}
      <div className="actions">
        <button type="submit" className="small" disabled={busy}>{existing ? 'Save' : 'Add device'}</button>
        <button type="button" className="secondary small" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  )
}
