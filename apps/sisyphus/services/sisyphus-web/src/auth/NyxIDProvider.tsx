import { useMemo, type ReactNode } from 'react'
import { NyxIDClient } from '@nyxids/oauth-core'
import { NyxIDProvider as BaseNyxIDProvider } from '@nyxids/oauth-react'

const nyxClient = new NyxIDClient({
  baseUrl: import.meta.env.VITE_NYXID_BASE_URL,
  clientId: import.meta.env.VITE_NYXID_CLIENT_ID,
  redirectUri: `${window.location.origin}/auth/callback`,
  scope: 'openid profile email proxy',
})

export function NyxIDAppProvider({ children }: { readonly children: ReactNode }) {
  const client = useMemo(() => nyxClient, [])

  return <BaseNyxIDProvider client={client}>{children}</BaseNyxIDProvider>
}

export { nyxClient }
