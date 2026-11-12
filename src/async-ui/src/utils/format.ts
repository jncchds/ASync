import type { DevicePlatform } from '../api'

export const PLATFORM_LABELS: Record<DevicePlatform, string> = {
  Unknown: 'Unknown',
  Windows: 'Windows',
  Linux: 'Linux',
  MacOS: 'macOS',
  IOS: 'iOS',
  Android: 'Android',
  Cli: 'CLI agent',
}

const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

/** "3 minutes ago", "in 2 hours". */
export function relativeTime(iso: string, now = Date.now()): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000)
  const abs = Math.abs(seconds)
  if (abs < 60) return rtf.format(seconds, 'second')
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute')
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), 'hour')
  return rtf.format(Math.round(seconds / 86400), 'day')
}

export const formatDate = (iso: string) => new Date(iso).toLocaleString()

/** 1536 → "1.5 KB" (powers of 1024, as file managers show sizes). */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB', 'PB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++ }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`
}

/** 90 → "1 h 30 min", 1440 → "1 day". */
export function formatMinutes(minutes: number): string {
  if (minutes < 60) return `${minutes} min`
  if (minutes % 1440 === 0) return minutes === 1440 ? '1 day' : `${minutes / 1440} days`
  const h = Math.floor(minutes / 60), m = minutes % 60
  return m === 0 ? `${h} h` : `${h} h ${m} min`
}

/**
 * The last 8 hex digits of a snapshot id, as the CLI and sync messages show them. Ids are Guid v7: the leading
 * digits are a timestamp shared by snapshots made around the same time, the trailing ones are random.
 */
export const shortSnapshotId = (id: string) => id.replace(/-/g, '').slice(-8)
