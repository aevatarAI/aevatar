import type { DeploymentStatus } from '../../types/workflow'

const STATUS_CONFIG: Record<DeploymentStatus, { color: string; bg: string; border: string; label: string }> = {
  deployed: { color: 'var(--neon-green)', bg: 'rgba(134,239,172,0.1)', border: 'rgba(134,239,172,0.2)', label: 'Deployed' },
  compiled: { color: 'var(--neon-gold)', bg: 'rgba(252,211,77,0.1)', border: 'rgba(252,211,77,0.2)', label: 'Compiled' },
  out_of_sync: { color: 'var(--neon-red)', bg: 'rgba(252,165,165,0.1)', border: 'rgba(252,165,165,0.2)', label: 'Out of Sync' },
  draft: { color: '#888', bg: 'rgba(136,136,136,0.1)', border: 'rgba(136,136,136,0.2)', label: 'Draft' },
}

export default function DeploymentBadge({ status }: { status?: DeploymentStatus }) {
  const cfg = STATUS_CONFIG[status ?? 'draft']
  return (
    <span
      className="text-[10px] font-medium uppercase px-2 py-0.5 rounded whitespace-nowrap"
      style={{ color: cfg.color, background: cfg.bg, border: `1px solid ${cfg.border}` }}
    >
      {cfg.label}
    </span>
  )
}
