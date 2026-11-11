import { BrowserRouter, Routes, Route, Navigate, useLocation } from 'react-router-dom'
import { AuthProvider, useAuth } from './hooks/useAuth'
import AppLayout from './layouts/AppLayout'
import Login from './pages/Login'
import Devices from './pages/Devices'
import ApproveDevice from './pages/ApproveDevice'
import AdminUsers from './pages/AdminUsers'

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
        <Route path="/devices" element={<Devices />} />
        <Route path="/devices/approve" element={<ApproveDevice />} />
        {user.isAdmin && <Route path="/admin/users" element={<AdminUsers />} />}
        <Route path="*" element={<Navigate to="/devices" replace />} />
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
