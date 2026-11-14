import type { AlertChannelType, AlertIncident, AlertKind } from '../api'

export const ALERT_KIND_LABELS: Record<AlertKind, string> = {
  Overdue: 'Overdue',
  Failing: 'Failing',
  CheckFailed: 'Verify failed',
  PruneFailed: 'Freeing space failed',
}

export const CHANNEL_TYPES: { type: AlertChannelType; label: string; blurb: string }[] = [
  { type: 'Ntfy', label: 'ntfy', blurb: 'Push notifications through ntfy.sh or your own ntfy server' },
  { type: 'Webhook', label: 'Webhook', blurb: 'A JSON POST to any URL (Home Assistant, n8n, Slack bridges…)' },
  { type: 'Email', label: 'E-mail', blurb: "Sent through this server's mail settings" },
]

export const channelTypeLabel = (t: AlertChannelType) => CHANNEL_TYPES.find(c => c.type === t)?.label ?? t

/** "photos on laptop", or just "photos" for folder problems. */
export const incidentSubject = (i: AlertIncident) => i.deviceName ? `${i.folderName} on ${i.deviceName}` : i.folderName

/** Open device incidents by `${folderId}:${deviceId}`, for the Folders page's pills. */
export function openByDevice(incidents: AlertIncident[]): Map<string, AlertIncident> {
  const map = new Map<string, AlertIncident>()
  for (const i of incidents) if (!i.resolvedAt && i.deviceId) map.set(`${i.folderId}:${i.deviceId}`, i)
  return map
}
