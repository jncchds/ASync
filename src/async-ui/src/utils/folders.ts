import type { DevicePlatform, FolderKind, SyncMode } from '../api'

export const MODES: { mode: SyncMode; label: string; blurb: string }[] = [
  { mode: 'Backup', label: 'Backup', blurb: 'Snapshots you can go back to, with retention rules' },
  { mode: 'TwoWay', label: 'Two-way sync', blurb: 'Changes flow both ways; clashing edits keep both copies' },
  { mode: 'Mirror', label: 'Mirror', blurb: 'One-way copy; each device uploads or downloads' },
]

export const modeLabel = (m: SyncMode) => MODES.find(x => x.mode === m)?.label ?? m

export const KINDS: { kind: FolderKind; label: string }[] = [
  { kind: 'Files', label: 'Files' },
  { kind: 'Photos', label: 'Photos & videos' },
  { kind: 'Contacts', label: 'Contacts' },
  { kind: 'Calendars', label: 'Calendars' },
]

export const kindLabel = (k: FolderKind) => KINDS.find(x => x.kind === k)?.label ?? k

/** Devices with a photo library ASync can back up (the server refuses the others for Photos folders). */
export const hasPhotoLibrary = (d: { platform: DevicePlatform }) => d.platform === 'Android' || d.platform === 'IOS'

/** "12 photos, 3 videos". */
export function countText(photos: number, videos: number): string {
  const p = `${photos.toLocaleString()} photo${photos === 1 ? '' : 's'}`
  return videos > 0 ? `${p}, ${videos.toLocaleString()} video${videos === 1 ? '' : 's'}` : p
}

/** The cap for a quiet period, as the server's ChangeDelayDefaults.CapFor: four times it, at least 10 minutes, at most a day. */
export const changeCapFor = (quiet: number) => Math.min(86400, Math.max(600, quiet * 4))

/** 5 → "5 s", 300 → "5 min", 5400 → "1.5 h". */
export const formatWait = (seconds: number) =>
  seconds < 60 ? `${seconds} s` : seconds < 3600 ? `${Math.round(seconds / 60)} min` : `${+(seconds / 3600).toFixed(1)} h`
