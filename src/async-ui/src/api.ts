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
