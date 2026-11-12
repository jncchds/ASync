import type { FolderKind, SyncMode } from '../api'

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
