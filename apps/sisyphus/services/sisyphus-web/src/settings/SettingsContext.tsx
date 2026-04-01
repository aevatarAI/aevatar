import { createContext, useContext, useState, useCallback, useEffect, type ReactNode } from 'react'
import { useAuth } from '../auth/useAuth'
import { proxyUrl } from '../hooks/use-api'

const SERVICE = 'sisyphus-admin'
const FALLBACK_GRAPH_ID = import.meta.env.VITE_DEFAULT_GRAPH_ID ?? ''

export interface SisyphusSettings {
  graphId: string
  verifyCronIntervalHours: number
  eventRetentionDays: number
  defaultResearchMode: 'graph_based' | 'exploration'
  graphViewNodeLimit: number
  updatedAt: string
}

const DEFAULT_SETTINGS: SisyphusSettings = {
  graphId: FALLBACK_GRAPH_ID,
  verifyCronIntervalHours: 6,
  eventRetentionDays: 30,
  defaultResearchMode: 'graph_based',
  graphViewNodeLimit: 200,
  updatedAt: '',
}

interface SettingsContextValue {
  settings: SisyphusSettings
  loading: boolean
  error: string | null
  updateSettings: (patch: Partial<SisyphusSettings>) => Promise<void>
  refresh: () => Promise<void>
}

const SettingsContext = createContext<SettingsContextValue | null>(null)

function apiUrl(path: string): string {
  return proxyUrl(SERVICE, path)
}

export function SettingsProvider({ children }: { children: ReactNode }) {
  const { getAccessToken } = useAuth()
  const [settings, setSettings] = useState<SisyphusSettings>(DEFAULT_SETTINGS)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    const token = getAccessToken()
    if (!token) return
    try {
      const res = await fetch(apiUrl('/settings'), {
        headers: { Authorization: `Bearer ${token}` },
      })
      if (!res.ok) throw new Error(`Failed to fetch settings: ${res.status}`)
      const data = await res.json()
      setSettings({
        graphId: data.graphId || FALLBACK_GRAPH_ID,
        verifyCronIntervalHours: data.verifyCronIntervalHours ?? 6,
        eventRetentionDays: data.eventRetentionDays ?? 30,
        defaultResearchMode: data.defaultResearchMode ?? 'graph_based',
        graphViewNodeLimit: data.graphViewNodeLimit ?? 200,
        updatedAt: data.updatedAt ?? '',
      })
      setError(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load settings')
      // Keep fallback defaults on error
    } finally {
      setLoading(false)
    }
  }, [getAccessToken])

  const updateSettings = useCallback(async (patch: Partial<SisyphusSettings>) => {
    const token = getAccessToken()
    if (!token) throw new Error('Not authenticated')

    const res = await fetch(apiUrl('/settings'), {
      method: 'PUT',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: JSON.stringify(patch),
    })
    if (!res.ok) {
      const body = await res.text().catch(() => '')
      throw new Error(`Failed to update settings: ${res.status} ${body}`)
    }
    const data = await res.json()
    setSettings({
      graphId: data.graphId || FALLBACK_GRAPH_ID,
      verifyCronIntervalHours: data.verifyCronIntervalHours ?? 6,
      eventRetentionDays: data.eventRetentionDays ?? 30,
      defaultResearchMode: data.defaultResearchMode ?? 'graph_based',
      graphViewNodeLimit: data.graphViewNodeLimit ?? 200,
      updatedAt: data.updatedAt ?? '',
    })
  }, [getAccessToken])

  // Fetch settings on mount
  useEffect(() => { refresh() }, [refresh])

  return (
    <SettingsContext.Provider value={{ settings, loading, error, updateSettings, refresh }}>
      {children}
    </SettingsContext.Provider>
  )
}

export function useSettings(): SettingsContextValue {
  const ctx = useContext(SettingsContext)
  if (!ctx) throw new Error('useSettings must be used within SettingsProvider')
  return ctx
}
