import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import type { AppUser } from '../api'
import { onSessionChange, refreshSession } from '../api'

interface AuthCtx {
  user: AppUser | null
  loading: boolean
  /** Deployment problems only an admin can fix, shown as a banner. */
  serverWarnings: string[]
}

const AuthContext = createContext<AuthCtx>({ user: null, loading: true, serverWarnings: [] })

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AppUser | null>(null)
  const [serverWarnings, setServerWarnings] = useState<string[]>([])
  const [loading, setLoading] = useState(true)

  // Every sign-in, refresh and sign-out goes through the session store in api.ts — including a refresh
  // that fails because the session ended elsewhere, which drops the user so protected routes redirect.
  useEffect(() => onSessionChange(session => {
    setUser(session?.user ?? null)
    setServerWarnings(session?.serverWarnings ?? [])
  }), [])

  // Sign in silently from the refresh cookie, if there is one.
  useEffect(() => {
    refreshSession().finally(() => setLoading(false))
  }, [])

  return (
    <AuthContext.Provider value={{ user, loading, serverWarnings }}>
      {children}
    </AuthContext.Provider>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
export const useAuth = () => useContext(AuthContext)
