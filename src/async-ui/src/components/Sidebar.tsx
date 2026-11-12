import { useState, type ReactNode } from 'react'
import { NavLink } from 'react-router-dom'
import { readSetting, useTheme, writeSetting, type Theme } from '../hooks/useTheme'

const COLLAPSED_KEY = 'sidebarCollapsed'
const PHONE = '(max-width: 767px)'

const isPhone = () => window.matchMedia(PHONE).matches

interface SidebarProps {
  children: ReactNode
  bottomChildren?: ReactNode
}

/**
 * The family app shell's sidebar (as in abook): a toggle with the app name and version at the top, the
 * navigation in the middle, and account actions plus the theme switch at the bottom. Collapsed it is an
 * icon rail; on phones the expanded sidebar opens over the page as a drawer.
 */
export default function Sidebar({ children, bottomChildren }: SidebarProps) {
  // No saved choice yet: start collapsed on phones, where the expanded sidebar would cover the page.
  const [collapsed, setCollapsed] = useState(() => {
    const saved = readSetting(COLLAPSED_KEY)
    return saved !== null ? saved === 'true' : isPhone()
  })
  const { theme, setTheme } = useTheme()

  const setAndRemember = (next: boolean) => {
    setCollapsed(next)
    // On phones the drawer always starts closed, so only desktop choices are remembered.
    if (!isPhone()) writeSetting(COLLAPSED_KEY, String(next))
  }

  // Following a link on a phone closes the drawer, so the page it opened is visible.
  const closeOnPhone = () => { if (!collapsed && isPhone()) setCollapsed(true) }

  const nextTheme: Theme = theme === 'light' ? 'dark' : theme === 'dark' ? 'system' : 'light'
  const themeIcon = theme === 'light' ? '☀️' : theme === 'dark' ? '🌙' : '🖥️'
  const themeLabel = `Theme: ${theme === 'system' ? 'system' : theme}`

  return (
    <>
      <aside className={`app-sidebar ${collapsed ? 'collapsed' : 'expanded'}`} onClick={e => {
        if ((e.target as HTMLElement).closest('a.sidebar-btn')) closeOnPhone()
      }}>
        <div className="sidebar-fixed-top">
          <button
            type="button"
            className="sidebar-btn sidebar-toggle-btn"
            onClick={() => setAndRemember(!collapsed)}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            aria-expanded={!collapsed}
          >
            <span className="s-icon">☰</span>
            <span className="s-label s-brand">ASync</span>
            <span className="version-pill">v{__APP_VERSION__}</span>
          </button>
        </div>

        <nav className="sidebar-scroll">{children}</nav>

        <div className="sidebar-fixed-bottom">
          {bottomChildren}
          <button type="button" className="sidebar-btn" onClick={() => setTheme(nextTheme)} title={`${themeLabel} (click for ${nextTheme})`}>
            <span className="s-icon">{themeIcon}</span>
            <span className="s-label">{themeLabel}</span>
          </button>
        </div>
      </aside>
      {!collapsed && <div className="sidebar-backdrop" onClick={() => setCollapsed(true)} aria-hidden />}
    </>
  )
}

export function SidebarLink({ to, icon, label }: { to: string; icon: string; label: string }) {
  return (
    <NavLink to={to} className={({ isActive }) => `sidebar-btn${isActive ? ' active' : ''}`} title={label}>
      <span className="s-icon">{icon}</span>
      <span className="s-label">{label}</span>
    </NavLink>
  )
}

export function SidebarBtn({ icon, label, onClick, title }: { icon: string; label: string; onClick: () => void; title?: string }) {
  return (
    <button type="button" className="sidebar-btn" onClick={onClick} title={title ?? label}>
      <span className="s-icon">{icon}</span>
      <span className="s-label">{label}</span>
    </button>
  )
}

export function SidebarDivider() {
  return <div className="sidebar-divider" />
}
