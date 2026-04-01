// Call ornn directly (not through NyxID proxy) so JWT auth works for private skills
const ORNN_API = 'https://ornn-api.chrono-ai.fun'

export interface OrnnSkill {
  guid: string
  name: string
  description: string
  isPrivate: boolean
  tags: string[]
  content?: string
}

interface SkillSearchResponse {
  data: {
    items: OrnnSkill[]
    total: number
    page: number
    pageSize: number
  }
  error: unknown
}

const skillCache = new Map<string, OrnnSkill & { content?: string }>()

export async function fetchSkillContent(idOrName: string, token: string): Promise<OrnnSkill & { content?: string }> {
  const cached = skillCache.get(idOrName)
  if (cached) return cached

  const res = await fetch(`${ORNN_API}/api/web/skills/${encodeURIComponent(idOrName)}/json`, {
    headers: { Authorization: `Bearer ${token}` },
  })
  if (!res.ok) throw new Error(`Failed to fetch skill: ${res.status}`)

  const skill = await res.json()
  skillCache.set(idOrName, skill)
  return skill
}

/** Search skills via ornn directly. Returns both public and private skills for the authenticated user. */
export async function searchSkills(query: string, token: string): Promise<OrnnSkill[]> {
  const params = new URLSearchParams({
    query,
    mode: 'keyword',
    scope: 'mixed',
    pageSize: '50',
  })
  const res = await fetch(`${ORNN_API}/api/web/skill-search?${params}`, {
    headers: { Authorization: `Bearer ${token}` },
  })
  if (!res.ok) throw new Error(`Failed to search skills: ${res.status}`)

  const body = await res.json() as SkillSearchResponse
  return body.data?.items ?? []
}
