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
