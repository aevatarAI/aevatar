import { useCallback, useEffect, useRef, useState } from 'react'
import { useNyxID } from '@nyxids/oauth-react'
import type { OAuthUserInfo } from '@nyxids/oauth-core'

/** How many ms before token expiry to trigger a silent refresh */
const REFRESH_BUFFER_MS = 60_000

const NYXID_BASE_URL = import.meta.env.VITE_NYXID_BASE_URL
const NYXID_CLIENT_ID = import.meta.env.VITE_NYXID_CLIENT_ID
const STORAGE_KEY = `nyxid_tokens_${NYXID_CLIENT_ID}`

async function doRefresh(refreshToken: string) {
  const body = new URLSearchParams({
    grant_type: 'refresh_token',
    client_id: NYXID_CLIENT_ID,
    refresh_token: refreshToken,
  })
  const res = await fetch(`${NYXID_BASE_URL}/oauth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: body.toString(),
  })
  if (!res.ok) throw new Error(`Token refresh failed: ${res.status}`)
  return res.json()
}

export function useAuth() {
  const { client, tokens, isAuthenticated, loginWithRedirect, clearSession } = useNyxID()
  const [user, setUser] = useState<OAuthUserInfo | null>(null)
  const refreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const refreshingRef = useRef(false)

  // Fetch user info once authenticated
  useEffect(() => {
    if (!isAuthenticated || !tokens?.accessToken) {
      setUser(null)
      return
    }
    client.getUserInfo(tokens.accessToken)
      .then(setUser)
      .catch((err) => console.error('Failed to fetch user info:', err))
  }, [client, isAuthenticated, tokens?.accessToken])

  // Schedule token refresh before expiry
  useEffect(() => {
    if (!tokens?.refreshToken || !tokens.expiresIn) return

    const refreshIn = Math.max((tokens.expiresIn * 1000) - REFRESH_BUFFER_MS, 5000)
    console.info(`Token refresh scheduled in ${Math.round(refreshIn / 1000)}s`)

    refreshTimerRef.current = setTimeout(async () => {
      if (refreshingRef.current) return
      refreshingRef.current = true
      try {
        const result = await doRefresh(tokens.refreshToken!)
        // Write refreshed tokens into the SDK's localStorage key
        const newTokens = {
          accessToken: result.access_token,
          tokenType: result.token_type ?? 'Bearer',
          expiresIn: result.expires_in,
          refreshToken: result.refresh_token ?? tokens.refreshToken,
          idToken: result.id_token,
          scope: result.scope,
        }
        localStorage.setItem(STORAGE_KEY, JSON.stringify(newTokens))
        console.info('Token refreshed, reloading...')
        window.location.reload()
      } catch (err) {
        console.warn('Token refresh failed, redirecting to login:', err)
        loginWithRedirect().catch(() => {})
      } finally {
        refreshingRef.current = false
      }
    }, refreshIn)

    return () => {
      if (refreshTimerRef.current) clearTimeout(refreshTimerRef.current)
    }
  }, [tokens, loginWithRedirect])

  const login = useCallback(() => loginWithRedirect(), [loginWithRedirect])

  const logout = useCallback(() => {
    clearSession()
    window.location.reload()
  }, [clearSession])

  const getAccessToken = useCallback((): string | null => {
    return tokens?.accessToken ?? null
  }, [tokens])

  return {
    isAuthenticated,
    user,
    tokens,
    login,
    logout,
    getAccessToken,
  }
}
