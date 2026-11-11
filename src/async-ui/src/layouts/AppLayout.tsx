import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { authLogout } from '../api'
import { useAuth } from '../hooks/useAuth'

export default function AppLayout() {
  const { user, serverWarnings } = useAuth()
  const navigate = useNavigate()

  const signOut = async () => {
    await authLogout()
    navigate('/login')
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <img src="/favicon.svg" alt="" width={24} height={24} />
          <span>ASync</span>
        </div>
        <nav>
          <NavLink to="/devices">Devices</NavLink>
          <NavLink to="/storage">Storage</NavLink>
          {user?.isAdmin && <NavLink to="/admin/users">Users</NavLink>}
        </nav>
        <div className="topbar-user">
          <span className="muted">{user?.displayName}</span>
          <button className="secondary" onClick={signOut}>Sign out</button>
        </div>
      </header>
      {serverWarnings.map(w => <div key={w} className="banner warning">{w}</div>)}
      <main className="container">
        <Outlet />
      </main>
      <footer className="footer muted">ASync {__APP_VERSION__}</footer>
    </div>
  )
}
