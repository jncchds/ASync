import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { authLogin, authRegister, authSetup, errorMessage, OIDC_LOGIN_URL, type AuthSetup } from '../api'

const SSO_ERRORS: Record<string, string> = {
  'sso-failed': 'Single sign-on did not complete. Try again.',
  'sso-no-account': 'Your single sign-on account is not linked to an ASync account. Ask an admin to create one.',
}

/** Only same-site paths, so a crafted link cannot send people elsewhere after signing in. */
function safeNext(next: string | null) {
  return next && next.startsWith('/') && !next.startsWith('//') ? next : '/'
}

export default function Login() {
  const [params] = useSearchParams()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState(SSO_ERRORS[params.get('error') ?? ''] ?? '')
  const [setup, setSetup] = useState<AuthSetup | null>(null)
  const navigate = useNavigate()
  const next = safeNext(params.get('next'))

  useEffect(() => {
    authSetup().then(setSetup).catch(() => setError('The server cannot be reached.'))
  }, [])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    try {
      await (setup?.needsSetup ? authRegister : authLogin)(username, password) // useAuth picks up the session
      navigate(next)
    } catch (err) {
      setError(errorMessage(err, 'Sign-in failed.'))
    }
  }

  const needsSetup = setup?.needsSetup ?? false

  return (
    <div className="login-page">
      <div className="login-card card">
        <h1><img src="/favicon.svg" alt="" width={32} height={32} /> ASync</h1>
        <h2>{needsSetup ? 'Create the admin account' : 'Sign in'}</h2>
        {needsSetup && <p className="hint">No accounts yet. The first account becomes admin.</p>}
        <form onSubmit={handleSubmit}>
          <label>
            Username
            <input value={username} onChange={e => setUsername(e.target.value)} required autoFocus autoComplete="username" />
          </label>
          <label>
            Password
            <input type="password" value={password} onChange={e => setPassword(e.target.value)} required
              minLength={needsSetup ? 8 : undefined} autoComplete={needsSetup ? 'new-password' : 'current-password'} />
          </label>
          {error && <p className="error-msg">{error}</p>}
          <button type="submit" className="wide">{needsSetup ? 'Create account' : 'Sign in'}</button>
        </form>
        {setup?.oidc.enabled && (
          <>
            <div className="divider"><span>or</span></div>
            <a className="button secondary wide" href={OIDC_LOGIN_URL}>Sign in with {setup.oidc.displayName}</a>
          </>
        )}
      </div>
    </div>
  )
}
