import { useCallback } from 'react'
import { useAuth } from '../auth/useAuth'

const PROXY_URL = import.meta.env.VITE_NYXID_PROXY_URL

export function proxyUrl(serviceSlug: string, path: string): string {
  return `${PROXY_URL}/api/v1/proxy/s/${serviceSlug}${path}`
}

export function useApi() {
  const { getAccessToken } = useAuth()

  const apiFetch = useCallback(
    async <T = unknown>(
      url: string,
      options: RequestInit = {},
    ): Promise<T> => {
      const token = getAccessToken()
      if (!token) throw new Error('Not authenticated')

      const headers: Record<string, string> = {
        Authorization: `Bearer ${token}`,
        ...(options.headers as Record<string, string> ?? {}),
      }

      // Set Content-Type for non-GET requests with body
      if (options.body && !headers['Content-Type']) {
        headers['Content-Type'] = 'application/json'
      }

      const res = await fetch(url, { ...options, headers })

      if (!res.ok) {
        const body = await res.text().catch(() => '')
        throw new Error(`API request failed: ${res.status} ${body}`)
      }

      const contentType = res.headers.get('content-type') ?? ''
      if (contentType.includes('application/json')) {
        return res.json()
      }

      return res.text() as unknown as T
    },
    [getAccessToken],
  )

  return { apiFetch, getAccessToken }
}
