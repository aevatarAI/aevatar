export interface ResearchV2Event {
  type: string
  round?: number
  blue_node_count?: number
  new_nodes?: number
  new_edges?: number
  nodes_written?: number
  edges_written?: number
  total_blue_nodes?: number
  attempt?: number
  errors?: string[]
  reason?: string
  error?: string
  delta?: string
}

export interface ResearchStatus {
  is_running: boolean
  current_round: number
}

export function startResearchV2(
  onEvent: (event: ResearchV2Event) => void,
  onDone: () => void,
  onError: (error: Error) => void,
  signal?: AbortSignal,
): void {
  fetch('/api/v2/research/start', {
    method: 'POST',
    signal,
  })
    .then(async (res) => {
      if (res.status === 409) {
        throw new Error('Research loop is already running')
      }
      if (!res.ok) {
        throw new Error(`Start research failed: ${res.status}`)
      }
      const reader = res.body?.getReader()
      if (!reader) {
        throw new Error('No response body')
      }

      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() ?? ''

        for (const line of lines) {
          const trimmed = line.trim()
          if (!trimmed.startsWith('data: ')) continue
          const jsonStr = trimmed.slice(6)
          if (!jsonStr) continue
          try {
            const event: ResearchV2Event = JSON.parse(jsonStr)
            onEvent(event)
          } catch {
            // skip malformed JSON
          }
        }
      }

      if (buffer.trim().startsWith('data: ')) {
        try {
          const event: ResearchV2Event = JSON.parse(buffer.trim().slice(6))
          onEvent(event)
        } catch {
          // skip
        }
      }

      onDone()
    })
    .catch((err) => {
      if (err.name === 'AbortError') {
        onDone()
      } else {
        onError(err)
      }
    })
}

export function subscribeResearch(
  onEvent: (event: ResearchV2Event) => void,
  onDone: () => void,
  onError: (error: Error) => void,
  signal?: AbortSignal,
): void {
  fetch('/api/v2/research/subscribe', { signal })
    .then(async (res) => {
      if (res.status === 404) {
        // Not running — just finish silently
        onDone()
        return
      }
      if (!res.ok) {
        throw new Error(`Subscribe failed: ${res.status}`)
      }
      const reader = res.body?.getReader()
      if (!reader) {
        throw new Error('No response body')
      }

      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break

        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() ?? ''

        for (const line of lines) {
          const trimmed = line.trim()
          if (!trimmed.startsWith('data: ')) continue
          const jsonStr = trimmed.slice(6)
          if (!jsonStr) continue
          try {
            const event: ResearchV2Event = JSON.parse(jsonStr)
            onEvent(event)
          } catch {
            // skip malformed JSON
          }
        }
      }

      if (buffer.trim().startsWith('data: ')) {
        try {
          const event: ResearchV2Event = JSON.parse(buffer.trim().slice(6))
          onEvent(event)
        } catch {
          // skip
        }
      }

      onDone()
    })
    .catch((err) => {
      if (err.name === 'AbortError') {
        onDone()
      } else {
        onError(err)
      }
    })
}

export async function stopResearch(): Promise<void> {
  const res = await fetch('/api/v2/research/stop', { method: 'POST' })
  if (!res.ok && res.status !== 404) {
    throw new Error(`Stop research failed: ${res.status}`)
  }
}

export async function fetchResearchStatus(): Promise<ResearchStatus> {
  const res = await fetch('/api/v2/research/status')
  if (!res.ok) throw new Error(`Failed to fetch research status: ${res.status}`)
  return res.json()
}

export async function exportPaper(): Promise<Blob> {
  const res = await fetch('/api/v2/paper')
  if (res.status === 404) {
    throw new Error('No purified nodes found — run research first')
  }
  if (!res.ok) {
    throw new Error(`Paper export failed: ${res.status}`)
  }
  return res.blob()
}
