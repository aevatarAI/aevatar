import { useState, useCallback, useEffect } from 'react'
import { Save, Loader2, RefreshCw, Settings as SettingsIcon } from 'lucide-react'
import { useSettings, type SisyphusSettings } from '../../settings/SettingsContext'

export default function SettingsPage() {
  const { settings, loading, error: loadError, updateSettings, refresh } = useSettings()

  const [form, setForm] = useState<SisyphusSettings>(settings)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  // Sync form when settings load/change
  useEffect(() => { setForm(settings) }, [settings])

  const dirty = JSON.stringify(form) !== JSON.stringify(settings)

  const handleSave = useCallback(async () => {
    setSaving(true)
    setError(null)
    setSaved(false)
    try {
      await updateSettings(form)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Save failed')
    } finally {
      setSaving(false)
    }
  }, [form, updateSettings])

  if (loading) {
    return (
      <div className="h-full flex items-center justify-center">
        <Loader2 size={20} className="animate-spin" style={{ color: 'var(--text-dimmed)' }} />
      </div>
    )
  }

  return (
    <div className="h-full overflow-auto p-6">
      <div className="w-full">
        {/* Header */}
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-3">
            <SettingsIcon size={20} style={{ color: 'var(--accent-blue)' }} />
            <div>
              <h1 className="text-lg font-semibold" style={{ color: 'var(--text-primary)' }}>
                Settings
              </h1>
              <p className="text-xs mt-0.5" style={{ color: 'var(--text-dimmed)' }}>
                Global Sisyphus configuration
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <button onClick={refresh} className="icon-btn" title="Refresh">
              <RefreshCw size={14} />
            </button>
            <button
              onClick={handleSave}
              disabled={saving || !dirty}
              className="btn-neon-green text-xs gap-1.5 py-1.5 px-3 disabled:opacity-50"
            >
              {saving ? <Loader2 size={14} className="animate-spin" /> : <Save size={14} />}
              Save
            </button>
          </div>
        </div>

        {(error || loadError) && (
          <div className="px-4 py-3 rounded mb-4" style={{ background: 'rgba(252,165,165,0.08)', border: '1px solid rgba(252,165,165,0.2)' }}>
            <span className="text-xs" style={{ color: 'var(--accent-red)' }}>{error || loadError}</span>
          </div>
        )}

        {saved && (
          <div className="px-4 py-3 rounded mb-4" style={{ background: 'rgba(134,239,172,0.08)', border: '1px solid rgba(134,239,172,0.2)' }}>
            <span className="text-xs" style={{ color: 'var(--neon-green)' }}>Settings saved successfully</span>
          </div>
        )}

        {/* Settings Form */}
        <div className="space-y-6">
          {/* Graph ID */}
          <div className="card p-5">
            <h2 className="text-sm font-semibold mb-1" style={{ color: 'var(--text-primary)' }}>
              Knowledge Graph
            </h2>
            <p className="text-[11px] mb-3" style={{ color: 'var(--text-dimmed)' }}>
              The chrono-graph instance Sisyphus operates on. All queries, ingestion, and visualization use this graph.
            </p>
            <label className="block">
              <span className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>Graph ID</span>
              <input
                className="input mt-1 w-full font-mono text-xs"
                value={form.graphId}
                onChange={(e) => setForm((p) => ({ ...p, graphId: e.target.value }))}
                placeholder="e.g. 8f917b59-ebfd-4a8b-912f-21bd262a5514"
              />
            </label>
          </div>

          {/* Research Config */}
          <div className="card p-5">
            <h2 className="text-sm font-semibold mb-1" style={{ color: 'var(--text-primary)' }}>
              Research
            </h2>
            <p className="text-[11px] mb-3" style={{ color: 'var(--text-dimmed)' }}>
              Default parameters for workflow execution.
            </p>
            <div className="grid grid-cols-2 gap-4">
              <label className="block">
                <span className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>Default Mode</span>
                <select
                  className="input mt-1 w-full text-xs"
                  value={form.defaultResearchMode}
                  onChange={(e) => setForm((p) => ({ ...p, defaultResearchMode: e.target.value as 'graph_based' | 'exploration' }))}
                >
                  <option value="graph_based">Graph-based</option>
                  <option value="exploration">Exploration</option>
                </select>
              </label>
              <label className="block">
                <span className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>Verify Cron Interval (hours)</span>
                <input
                  type="number"
                  className="input mt-1 w-full text-xs"
                  min={1}
                  max={168}
                  value={form.verifyCronIntervalHours}
                  onChange={(e) => setForm((p) => ({ ...p, verifyCronIntervalHours: parseInt(e.target.value) || 6 }))}
                />
              </label>
            </div>
          </div>

          {/* Display & Retention */}
          <div className="card p-5">
            <h2 className="text-sm font-semibold mb-1" style={{ color: 'var(--text-primary)' }}>
              Display & Retention
            </h2>
            <div className="grid grid-cols-2 gap-4">
              <label className="block">
                <span className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>Graph View Node Limit</span>
                <input
                  type="number"
                  className="input mt-1 w-full text-xs"
                  min={50}
                  max={5000}
                  value={form.graphViewNodeLimit}
                  onChange={(e) => setForm((p) => ({ ...p, graphViewNodeLimit: parseInt(e.target.value) || 200 }))}
                />
              </label>
              <label className="block">
                <span className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>Event Retention (days)</span>
                <input
                  type="number"
                  className="input mt-1 w-full text-xs"
                  min={1}
                  max={365}
                  value={form.eventRetentionDays}
                  onChange={(e) => setForm((p) => ({ ...p, eventRetentionDays: parseInt(e.target.value) || 30 }))}
                />
              </label>
            </div>
          </div>

          {/* Last updated */}
          {settings.updatedAt && (
            <p className="text-[10px] text-right" style={{ color: 'var(--text-dimmed)' }}>
              Last updated: {new Date(settings.updatedAt).toLocaleString()}
            </p>
          )}
        </div>
      </div>
    </div>
  )
}
