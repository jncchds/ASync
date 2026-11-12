import { Outlet, useNavigate } from 'react-router-dom'
import { authLogout } from '../api'
import Sidebar, { SidebarBtn, SidebarDivider, SidebarLink } from '../components/Sidebar'
import { useAuth } from '../hooks/useAuth'

export default function AppLayout() {
  const { user, serverWarnings } = useAuth()
  const navigate = useNavigate()

  const signOut = async () => {
    await authLogout()
    navigate('/login')
  }

  return (
    <div className="app-layout">
      <Sidebar bottomChildren={
        <>
          <div className="sidebar-user hide-when-collapsed" title={user?.username}>
            <span className="s-user-name">{user?.displayName}</span>
            {user?.isAdmin && <span className="pill pill-accent">admin</span>}
          </div>
          <SidebarBtn icon="🚪" label="Sign out" title={`Sign out (${user?.username ?? '…'})`} onClick={signOut} />
        </>
      }>
        <SidebarLink to="/folders" icon="📁" label="Folders" />
        <SidebarLink to="/devices" icon="💻" label="Devices" />
        <SidebarLink to="/storage" icon="🗄️" label="Storage" />
        {user?.isAdmin && (
          <>
            <SidebarDivider />
            <SidebarLink to="/admin/users" icon="👥" label="Users" />
          </>
        )}
      </Sidebar>
      <main className="app-main">
        {serverWarnings.map(w => <div key={w} className="banner warning">{w}</div>)}
        <div className="page">
          <Outlet />
        </div>
      </main>
    </div>
  )
}
