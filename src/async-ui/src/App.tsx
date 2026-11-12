import { BrowserRouter, Routes, Route, Navigate, useLocation } from 'react-router-dom'
import { AuthProvider, useAuth } from './hooks/useAuth'
import AppLayout from './layouts/AppLayout'
import Login from './pages/Login'
import Devices from './pages/Devices'
import ApproveDevice from './pages/ApproveDevice'
import AdminUsers from './pages/AdminUsers'
import Storage from './pages/Storage'
import Folders from './pages/Folders'

function ProtectedRoutes() {
  const { user, loading } = useAuth()
  const location = useLocation()
  if (loading) return <div className="loading">Loading…</div>
  if (!user) {
    // Come back here after signing in (e.g. the approval link a CLI device printed).
    const next = encodeURIComponent(location.pathname + location.search)
    return <Navigate to={`/login?next=${next}`} replace />
  }
  return (
    <Routes>
      <Route element={<AppLayout />}>
        <Route path="/folders" element={<Folders />} />
        <Route path="/devices" element={<Devices />} />
        <Route path="/devices/approve" element={<ApproveDevice />} />
        <Route path="/storage" element={<Storage />} />
        {user.isAdmin && <Route path="/admin/users" element={<AdminUsers />} />}
        <Route path="*" element={<Navigate to="/folders" replace />} />
      </Route>
    </Routes>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/*" element={<ProtectedRoutes />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}
