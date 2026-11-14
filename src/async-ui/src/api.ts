import axios from 'axios'

const api = axios.create({ baseURL: '/api' })

// ── Session ───────────────────────────────────────────────────────────────────
// Sign-in uses a short-lived JWT access token kept only in memory (never in storage), sent as
// `Authorization: Bearer`. It is renewed with POST /auth/refresh, which reads an httpOnly cookie the
// browser sends only to /api/auth — so a reload or a new tab signs in silently while that cookie lives.

export interface AppUser {
  id: number
  username: string
  displayName: string
  isAdmin: boolean
  /** False for accounts that only sign in with single sign-on. */
  hasPassword: boolean
}

export interface Session {
  user: AppUser
  accessToken: string
  accessTokenExpiresAt: string
  /** Deployment problems only an admin can fix (empty for everyone else). */
  serverWarnings: string[]
}

let accessToken: string | null = null
let accessTokenExpiresAt = 0
let refreshInFlight: Promise<Session | null> | null = null
const sessionListeners = new Set<(session: Session | null) => void>()

function applySession(session: Session | null) {
  accessToken = session?.accessToken ?? null
  accessTokenExpiresAt = session ? Date.parse(session.accessTokenExpiresAt) : 0
  sessionListeners.forEach(l => l(session))
}

/** Notified on every sign-in, refresh and sign-out (null = signed out). Returns an unsubscribe. */
export function onSessionChange(listener: (session: Session | null) => void): () => void {
  sessionListeners.add(listener)
  return () => { sessionListeners.delete(listener) }
}

/** Renews the access token from the refresh cookie; concurrent callers share one request. */
export function refreshSession(): Promise<Session | null> {
  refreshInFlight ??= axios.post<Session>('/api/auth/refresh')
    .then(r => { applySession(r.data); return r.data })
    .catch(err => {
      // Only a rejected cookie ends the session; a network error leaves it for the next attempt.
      if (axios.isAxiosError(err) && err.response?.status === 401) applySession(null)
      return null
    })
    .finally(() => { refreshInFlight = null })
  return refreshInFlight
}

/** A usable access token (refreshed first when it expires within a minute), or null when signed out. */
export async function getAccessToken(): Promise<string | null> {
  if (accessToken && accessTokenExpiresAt - Date.now() > 60_000) return accessToken
  return (await refreshSession())?.accessToken ?? null
}

/** Endpoints that work without an access token; a 401 there means "wrong password", not "session expired". */
const PUBLIC_AUTH_ENDPOINTS = new Set(['/auth/setup', '/auth/login', '/auth/register', '/auth/logout', '/auth/refresh'])

api.interceptors.request.use(async config => {
  if (!PUBLIC_AUTH_ENDPOINTS.has(config.url ?? '')) {
    const token = await getAccessToken()
    if (token) config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// A 401 outside the auth endpoints means the access token was rejected (e.g. the server's signing key
// changed): refresh once and retry; if that fails too, the session is over and listeners see null.
api.interceptors.response.use(undefined, async error => {
  const config = axios.isAxiosError(error) ? error.config as (typeof error.config & { _retried?: boolean }) : undefined
  if (axios.isAxiosError(error) && error.response?.status === 401 && config && !config._retried && !PUBLIC_AUTH_ENDPOINTS.has(config.url ?? '')) {
    config._retried = true
    accessToken = null
    if (await refreshSession()) return api.request(config)
  }
  return Promise.reject(error)
})

/** The server's `{ message }` for a failed request, or a fallback. */
export function errorMessage(err: unknown, fallback = 'Something went wrong.'): string {
  if (axios.isAxiosError(err)) {
    const msg = (err.response?.data as { message?: string } | undefined)?.message
    if (msg) return msg
    if (err.response?.status === 429) return 'Too many attempts. Wait a minute and try again.'
  }
  return fallback
}

// ── Auth ──────────────────────────────────────────────────────────────────────

export interface AuthSetup {
  needsSetup: boolean
  oidc: { enabled: boolean; displayName: string }
}

export const authSetup = () => api.get<AuthSetup>('/auth/setup').then(r => r.data)

export const authLogin = (username: string, password: string) =>
  api.post<Session>('/auth/login', { username, password }).then(r => { applySession(r.data); return r.data })

export const authRegister = (username: string, password: string) =>
  api.post<Session>('/auth/register', { username, password }).then(r => { applySession(r.data); return r.data })

export async function authLogout() {
  try { await api.post('/auth/logout') } finally { applySession(null) }
}

/** Full-page navigation: the identity provider's sign-in page cannot be reached with XHR. */
export const OIDC_LOGIN_URL = '/api/auth/oidc/login'

// ── Devices ───────────────────────────────────────────────────────────────────

export type DevicePlatform = 'Unknown' | 'Windows' | 'Linux' | 'MacOS' | 'IOS' | 'Android' | 'Cli'

export interface Device {
  id: string
  name: string
  platform: DevicePlatform
  appVersion: string | null
  createdAt: string
  lastSeenAt: string | null
  revokedAt: string | null
}

export interface Enrollment {
  id: number
  label: string | null
  reusable: boolean
  usedCount: number
  createdAt: string
  expiresAt: string
}

export interface EnrollmentCreated {
  id: number
  code: string
  pairUri: string
  expiresAt: string
  reusable: boolean
}

export interface PendingDevice {
  userCode: string
  name: string
  platform: DevicePlatform
  expiresAt: string
}

export const getDevices = () => api.get<Device[]>('/devices').then(r => r.data)
export const renameDevice = (id: string, name: string) => api.patch<Device>(`/devices/${id}`, { name }).then(r => r.data)
export const revokeDevice = (id: string) => api.delete(`/devices/${id}`)

export const getEnrollments = () => api.get<Enrollment[]>('/devices/enrollments').then(r => r.data)
export const createEnrollment = (req: { reusable: boolean; validMinutes?: number; label?: string }) =>
  api.post<EnrollmentCreated>('/devices/enrollments', req).then(r => r.data)
export const revokeEnrollment = (id: number) => api.delete(`/devices/enrollments/${id}`)

export const getPendingDevice = (userCode: string) =>
  api.get<PendingDevice>(`/devices/approvals/${encodeURIComponent(userCode)}`).then(r => r.data)
export const decideDevice = (userCode: string, approve: boolean) => api.post('/devices/approvals', { userCode, approve })

// ── Admin: users ──────────────────────────────────────────────────────────────

export interface AdminUser {
  id: number
  username: string
  displayName: string
  isAdmin: boolean
  hasPassword: boolean
  hasOidc: boolean
  createdAt: string
}

export const getUsers = () => api.get<AdminUser[]>('/users').then(r => r.data)
export const createUser = (username: string, password: string, isAdmin: boolean) =>
  api.post<AdminUser>('/users', { username, password, isAdmin }).then(r => r.data)
export const setUserPassword = (id: number, newPassword: string) => api.put(`/users/${id}/password`, { newPassword })
export const setUserRole = (id: number, isAdmin: boolean) => api.put<AdminUser>(`/users/${id}/role`, { isAdmin }).then(r => r.data)

/** What deleting a user removes, for the confirmation. */
export interface UserDeletionPreview {
  id: number
  username: string
  devices: number
  sessions: number
  enrollmentCodes: number
  alertChannels: number
  openIncidents: number
  folders: string[]
  storageBackends: string[]
  foldersOnSharedStorage: number
}

export const getUserDeletionPreview = (id: number) => api.get<UserDeletionPreview>(`/users/${id}/deletion`).then(r => r.data)
/** With `purge`, `confirm` must be the username; the answer lists the purges started for their folders. */
export const deleteUser = (id: number, purge: boolean, confirm?: string) =>
  api.delete<{ purges: PurgeJob[] }>(`/users/${id}`, { params: purge ? { purge: true, confirm } : {} }).then(r => r.data)

// ── Own account ───────────────────────────────────────────────────────────────

/** Signs out every other browser session; this one stays (the server sets a new refresh cookie). */
export const changeOwnPassword = (currentPassword: string, newPassword: string) =>
  api.post('/auth/password', { currentPassword, newPassword })

// ── Purges of deleted folders' stored data ────────────────────────────────────

export type PurgeStatus = 'Pending' | 'Waiting' | 'Running' | 'Done' | 'Failed'

export interface PurgeJob {
  id: string
  folderId: number
  folderName: string
  ownerName: string | null
  status: PurgeStatus
  filesDeleted: number
  bytesDeleted: number
  filesLeft: number
  message: string | null
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
}

export const getPurges = () => api.get<PurgeJob[]>('/purges').then(r => r.data)
export const isPurgeFinished = (j: PurgeJob) => j.status === 'Done' || j.status === 'Failed'

// ── Storage back-ends ─────────────────────────────────────────────────────────

export type StorageType = 'Local' | 'S3' | 'WebDav' | 'Sftp' | 'Smb'

/** Connection settings; `type` selects the shape. Secrets come back as null (see `secretsSet`). */
export type StorageSettings = { type: StorageType } & Record<string, string | number | boolean | null | undefined>

export interface StorageBackend {
  id: number
  name: string
  type: StorageType
  settings: StorageSettings
  secretsSet: string[]
  description: string
  shared: boolean
  canEdit: boolean
  createdAt: string
  lastCheckedAt: string | null
  lastCheckOk: boolean | null
  lastCheckError: string | null
}

export interface StorageOptions { localRoots: string[]; canCreateLocal: boolean; canCreateShared: boolean }

export interface StorageTestResult { ok: boolean; error: string | null; latencyMs: number | null; hostKeyFingerprint: string | null }

export interface SaveStorageBackend { name: string; settings: StorageSettings; shared: boolean }

export const getStorageBackends = () => api.get<StorageBackend[]>('/storage-backends').then(r => r.data)
export const getStorageOptions = () => api.get<StorageOptions>('/storage-backends/options').then(r => r.data)
export const createStorageBackend = (req: SaveStorageBackend) => api.post<StorageBackend>('/storage-backends', req).then(r => r.data)
export const updateStorageBackend = (id: number, req: SaveStorageBackend) => api.put<StorageBackend>(`/storage-backends/${id}`, req).then(r => r.data)
export const deleteStorageBackend = (id: number) => api.delete(`/storage-backends/${id}`)
export const testStorageSettings = (settings: StorageSettings, backendId?: number) =>
  api.post<StorageTestResult>('/storage-backends/test', { settings, backendId }).then(r => r.data)
export const testStorageBackend = (id: number) => api.post<StorageTestResult>(`/storage-backends/${id}/test`).then(r => r.data)

// ── Folders ───────────────────────────────────────────────────────────────────

export type SyncMode = 'Backup' | 'Mirror' | 'TwoWay'
export type FolderEncryption = 'Transport' | 'EndToEnd'
export type FolderKind = 'Files' | 'Photos' | 'Contacts' | 'Calendars'
export type MirrorDirection = 'Upload' | 'Download'

/** Which snapshots a Backup folder keeps. All zero/null = keep everything. */
export interface Retention {
  last: number
  hourly: number
  daily: number
  weekly: number
  monthly: number
  yearly: number
  withinHours: number | null
}

export interface FolderDevice {
  deviceId: string
  deviceName: string
  platform: DevicePlatform
  localPath: string
  direction: MirrorDirection | null
  paused: boolean
  lastSyncAt: string | null
  lastSyncOk: boolean | null
  lastSyncMessage: string | null
}

export interface Folder {
  id: number
  name: string
  mode: SyncMode
  encryption: FolderEncryption
  kind: FolderKind
  storageBackendId: number
  storageBackendName: string
  rootPath: string
  intervalMinutes: number
  excludes: string[]
  retention: Retention
  versionsKeepDays: number
  trashKeepDays: number
  /** Grows with every change; edits send it back so concurrent edits are detected (409). */
  revision: number
  /** False for an end-to-end folder until a device has set up its repository. */
  repositoryReady: boolean
  devices: FolderDevice[]
  createdAt: string
  /** Backup folders: days between prunes and between integrity checks (0 = never), and how much data a check reads back. */
  pruneIntervalDays: number
  checkIntervalDays: number
  checkReadDataPercent: number
  lastPrune: MaintenanceStatus | null
  lastCheck: MaintenanceStatus | null
  /** Standard-encryption Backup folders: when the current recovery key was made (null = none yet). */
  recoveryKeyCreatedAt: string | null
  /** Only in the answer that created the folder: its recovery key, shown once. */
  recoveryKey?: string | null
}

/** A new recovery key, shown once, and where the repository is on the folder's storage (for an offline restore). */
export interface RecoveryKey {
  recoveryKey: string
  createdAt: string
  storagePath: string
}

/** The last prune or check of a Backup folder; `by` is "server" or a device's name. */
export interface MaintenanceStatus {
  at: string
  ok: boolean
  message: string | null
  by: string | null
}

export interface SaveFolder {
  name: string
  mode: SyncMode
  encryption: FolderEncryption
  kind: FolderKind
  storageBackendId: number
  rootPath: string
  intervalMinutes: number
  excludes: string[]
  retention: Retention
  versionsKeepDays: number
  trashKeepDays: number
  pruneIntervalDays: number
  checkIntervalDays: number
  checkReadDataPercent: number
  expectedRevision?: number
  /** Creating a standard-encryption Backup folder: take over the repository already in the directory. */
  recoveryKey?: string
}

export interface AttachDevice {
  deviceId: string
  localPath: string
  direction: MirrorDirection | null
  paused: boolean
  expectedRevision?: number
}

export const getFolders = () => api.get<Folder[]>('/folders').then(r => r.data)
export const createFolder = (req: SaveFolder) => api.post<Folder>('/folders', req).then(r => r.data)
export const updateFolder = (id: number, req: SaveFolder) => api.put<Folder>(`/folders/${id}`, req).then(r => r.data)
/** With `purge`, `confirm` must be the folder's name: the server answers 202 with the job that removes its stored data. */
export const deleteFolder = (id: number, purge = false, confirm?: string) =>
  api.delete<PurgeJob | ''>(`/folders/${id}`, { params: purge ? { purge: true, confirm } : {} }).then(r => r.data || null)
/** Makes a new recovery key (the previous one stops working). Send the password, or `confirm` for single-sign-on accounts. */
export const issueRecoveryKey = (id: number, req: { password?: string; confirm?: boolean }) =>
  api.post<RecoveryKey>(`/folders/${id}/recovery-key`, req).then(r => r.data)
export const attachFolderDevice = (id: number, req: AttachDevice) => api.put<Folder>(`/folders/${id}/devices`, req).then(r => r.data)
export const detachFolderDevice = (id: number, deviceId: string) => api.delete<Folder>(`/folders/${id}/devices/${deviceId}`).then(r => r.data)

/** True for a 409: someone changed the folder since it was loaded. */
export const isConflict = (err: unknown) => axios.isAxiosError(err) && err.response?.status === 409

// ── Snapshots (Transport-encrypted Backup folders) ────────────────────────────

export interface Snapshot {
  id: string
  time: string
  source: string
  deviceId: string | null
  deviceName: string
  hostname: string | null
  tags: string[]
  filesNew: number
  filesChanged: number
  filesUnmodified: number
  bytesProcessed: number
  bytesAdded: number
  errors: number
}

export type SnapshotNodeType = 'File' | 'Directory' | 'Symlink'

export interface SnapshotNode {
  name: string
  path: string
  type: SnapshotNodeType
  size: number
  modified: string
  linkTarget: string | null
}

export interface SnapshotTree { snapshotId: string; path: string; nodes: SnapshotNode[] }

export const getSnapshots = (folderId: number) => api.get<Snapshot[]>(`/folders/${folderId}/snapshots`).then(r => r.data)
export const getSnapshotTree = (folderId: number, snapshotId: string, path: string) =>
  api.get<SnapshotTree>(`/folders/${folderId}/snapshots/${snapshotId}/tree`, { params: { path } }).then(r => r.data)

/**
 * Starts a browser download of a file (or a directory as a ZIP). The server hands out a link valid for a few
 * minutes, so the browser streams the download to disk instead of it being buffered here.
 */
export async function downloadFromSnapshot(folderId: number, snapshotId: string, path: string, zip: boolean) {
  const { data } = await api.post<{ url: string; expiresAt: string }>(`/folders/${folderId}/snapshots/${snapshotId}/download-link`, { path, zip })
  const a = document.createElement('a')
  a.href = data.url
  a.rel = 'noopener'
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ── Files, versions and trash (Mirror/TwoWay folders, plain layout) ───────────

export interface PlainEntry { name: string; path: string; isDirectory: boolean; size: number; modified: string | null; versions: number }
export interface PlainDirectory { path: string; entries: PlainEntry[] }
export interface PlainVersion { stamp: string; replacedAt: string; size: number; modified: string | null }
export interface PlainFileHistory { path: string; exists: boolean; size: number | null; modified: string | null; versions: PlainVersion[] }
export interface PlainVersionedFile { path: string; versions: number; latestReplacedAt: string; versionsSize: number }
export interface PlainTrashItem { path: string; stamp: string; deletedAt: string; size: number }
export interface PlainTrashGroup { deletedAt: string; items: PlainTrashItem[] }
export interface PlainTrash { groups: PlainTrashGroup[]; files: number; size: number }
export type PlainSource = 'Current' | 'Version' | 'Trash'
export type PlainRestoreConflict = 'Refuse' | 'Rename' | 'Replace'
export interface PlainRestoreItem { source: PlainSource; path: string; stamp: string }
export interface PlainRestoreResult { restoredTo: string[]; warning: string | null }

export const getPlainDirectory = (folderId: number, path: string) =>
  api.get<PlainDirectory>(`/folders/${folderId}/files`, { params: { path } }).then(r => r.data)
export const getPlainHistory = (folderId: number, path: string) =>
  api.get<PlainFileHistory>(`/folders/${folderId}/files/history`, { params: { path } }).then(r => r.data)
export const getPlainVersionedFiles = (folderId: number) =>
  api.get<PlainVersionedFile[]>(`/folders/${folderId}/files/versions`).then(r => r.data)
export const getPlainTrash = (folderId: number) => api.get<PlainTrash>(`/folders/${folderId}/files/trash`).then(r => r.data)
export const emptyPlainTrash = (folderId: number) =>
  api.delete<{ deleted: number }>(`/folders/${folderId}/files/trash`, { params: { confirm: true } }).then(r => r.data)
/** 409 with `conflicts` when a trashed file's path is taken (and `onConflict` is Refuse), or while a device syncs the folder. */
export const restorePlain = (folderId: number, items: PlainRestoreItem[], onConflict: PlainRestoreConflict = 'Refuse') =>
  api.post<PlainRestoreResult>(`/folders/${folderId}/files/restore`, { items, onConflict }).then(r => r.data)
/** The paths in the way when a restore answered 409 because files exist (not when the folder was busy). */
export function restoreConflicts(err: unknown): string[] | null {
  if (!axios.isAxiosError(err) || err.response?.status !== 409) return null
  const conflicts = (err.response.data as { conflicts?: string[] } | undefined)?.conflicts
  return conflicts?.length ? conflicts : null
}

/** Starts a browser download of a current file, a version or a trashed file, or a current directory as a ZIP. */
export async function downloadPlain(folderId: number, source: PlainSource, path: string, stamp?: string, zip = false) {
  const { data } = await api.post<{ url: string; expiresAt: string }>(`/folders/${folderId}/files/download-link`, { source, path, stamp, zip })
  const a = document.createElement('a')
  a.href = data.url
  a.rel = 'noopener'
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ── Alerts ────────────────────────────────────────────────────────────────────

export type AlertKind = 'Overdue' | 'Failing' | 'CheckFailed' | 'PruneFailed'
export type AlertChannelType = 'Webhook' | 'Ntfy' | 'Email'

export type AlertChannelSettings = { type: AlertChannelType } & Record<string, string | null | undefined>

export interface AlertChannel {
  id: number
  name: string
  type: AlertChannelType
  enabled: boolean
  /** Secrets come back as null; `secretsSet` names the ones that are stored. */
  settings: AlertChannelSettings
  secretsSet: string[]
  description: string
  createdAt: string
  lastSentAt: string | null
  lastOk: boolean | null
  lastError: string | null
}

export interface SaveAlertChannel { name: string; settings: AlertChannelSettings; enabled: boolean }

export interface AlertTestResult { ok: boolean; error: string | null }

export interface AlertDefaults { graceFactor: number; minimumGraceHours: number; failureThreshold: number; reminderHours: number }

/** Null fields use the server's default. */
export interface AlertSettings {
  graceFactor: number | null
  minimumGraceHours: number | null
  failureThreshold: number | null
  reminderHours: number | null
  defaults: AlertDefaults
  emailAvailable: boolean
  pushMode: string
}

export type SaveAlertSettings = Pick<AlertSettings, 'graceFactor' | 'minimumGraceHours' | 'failureThreshold' | 'reminderHours'>

export interface AlertIncident {
  id: number
  kind: AlertKind
  folderId: number
  folderName: string
  deviceId: string | null
  deviceName: string | null
  detail: string
  openedAt: string
  resolvedAt: string | null
  notifiedAt: string | null
}

export const getAlertSettings = () => api.get<AlertSettings>('/alerts/settings').then(r => r.data)
export const saveAlertSettings = (req: SaveAlertSettings) => api.put<AlertSettings>('/alerts/settings', req).then(r => r.data)
export const getAlertIncidents = (days = 7) => api.get<AlertIncident[]>('/alerts/incidents', { params: { days } }).then(r => r.data)
export const getAlertChannels = () => api.get<AlertChannel[]>('/alerts/channels').then(r => r.data)
export const createAlertChannel = (req: SaveAlertChannel) => api.post<AlertChannel>('/alerts/channels', req).then(r => r.data)
export const updateAlertChannel = (id: number, req: SaveAlertChannel) => api.put<AlertChannel>(`/alerts/channels/${id}`, req).then(r => r.data)
export const deleteAlertChannel = (id: number) => api.delete(`/alerts/channels/${id}`)
export const testAlertSettings = (settings: AlertChannelSettings, channelId?: number) =>
  api.post<AlertTestResult>('/alerts/channels/test', { settings, channelId }).then(r => r.data)
export const testAlertChannel = (id: number) => api.post<AlertTestResult>(`/alerts/channels/${id}/test`).then(r => r.data)
