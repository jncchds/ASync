import { useState } from 'react'
import { attachFolderDevice, errorMessage, isConflict, type Device, type Folder, type FolderDevice, type MirrorDirection } from '../api'

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
  const available = devices.filter(d => !folder.devices.some(m => m.deviceId === d.id))
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

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    setConflict(false)
    try {
      await attachFolderDevice(folder.id, {
        deviceId, localPath: localPath.trim(), direction: folder.mode === 'Mirror' ? direction : null, paused,
        expectedRevision: startRevision, conditions: { unmeteredOnly, chargingOnly, batteryNotLow },
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
        <p className="hint">All your devices are already in this folder. Add more devices on the Devices page.</p>
        <button type="button" className="secondary small" onClick={onCancel}>Close</button>
      </div>
    )

  return (
    <form className="subform" onSubmit={save}>
      <div className="form-row">
        <label>
          Device
          <select value={deviceId} disabled={existing !== undefined} onChange={e => setDeviceId(e.target.value)} required>
            {existing
              ? <option value={existing.deviceId}>{existing.deviceName}</option>
              : available.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </label>
        <label>
          Folder on the device
          <input value={localPath} onChange={e => setLocalPath(e.target.value)} required maxLength={1000} placeholder="/home/me/Documents" />
        </label>
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
