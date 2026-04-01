import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import AuthCallback from './auth/AuthCallback'
import ProtectedRoute from './auth/ProtectedRoute'
import { SettingsProvider } from './settings/SettingsContext'
import AppLayout from './components/layout/AppLayout'
import SchemaListPage from './components/schemas/SchemaListPage'
import WorkflowListPage from './components/workflows/WorkflowListPage'
import WorkflowEditorPage from './components/workflows/WorkflowEditorPage'
import WorkflowRunPage from './components/runner/WorkflowRunPage'
import TriggerHistoryPage from './components/runner/TriggerHistoryPage'
import RunDetailView from './components/runner/RunDetailView'
import UploadPage from './components/upload/UploadPage'
import UploadHistoryPage from './components/upload/UploadHistoryPage'
import SettingsPage from './components/settings/SettingsPage'
import SchemaEditorPage from './components/schemas/SchemaEditorPage'
import ConnectorEditorPage from './components/connectors/ConnectorEditorPage'

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/auth/callback" element={<AuthCallback />} />
        <Route
          element={
            <ProtectedRoute>
              <SettingsProvider>
                <AppLayout />
              </SettingsProvider>
            </ProtectedRoute>
          }
        >
          {/* Graph page — AppLayout shows graph background only, no overlay */}
          <Route path="/graph" element={null} />

          {/* All other pages render in the glass overlay */}
          <Route path="/schemas" element={<SchemaListPage />} />
          <Route path="/schemas/:id/edit" element={<SchemaEditorPage />} />
          <Route path="/connectors/:id/edit" element={<ConnectorEditorPage />} />
          <Route path="/workflows" element={<WorkflowListPage />} />
          <Route path="/workflows/:id/edit" element={<WorkflowEditorPage />} />
          <Route path="/workflows/run" element={<WorkflowRunPage />} />
          <Route path="/workflows/history" element={<TriggerHistoryPage />} />
          <Route path="/workflows/history/:id" element={<RunDetailView />} />
          <Route path="/upload" element={<UploadPage />} />
          <Route path="/upload/history" element={<UploadHistoryPage />} />
          <Route path="/settings" element={<SettingsPage />} />

          <Route path="/" element={<Navigate to="/graph" replace />} />
          <Route path="*" element={<Navigate to="/graph" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
