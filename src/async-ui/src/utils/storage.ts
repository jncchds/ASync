import type { StorageType } from '../api'

export const STORAGE_TYPES: { type: StorageType; label: string; blurb: string }[] = [
  { type: 'S3', label: 'S3-compatible', blurb: 'AWS S3, MinIO, Backblaze B2, Cloudflare R2, Wasabi…' },
  { type: 'WebDav', label: 'WebDAV / Nextcloud', blurb: 'Nextcloud, ownCloud or any WebDAV server' },
  { type: 'Sftp', label: 'SFTP', blurb: 'Any SSH server' },
  { type: 'Smb', label: 'SMB', blurb: 'Windows or Samba share, NAS' },
  { type: 'Local', label: 'Server disk', blurb: "A directory on the ASync server's own storage" },
]

export const typeLabel = (t: StorageType) => STORAGE_TYPES.find(x => x.type === t)?.label ?? t
