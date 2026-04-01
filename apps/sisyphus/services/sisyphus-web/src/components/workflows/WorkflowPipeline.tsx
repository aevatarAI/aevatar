import { Save, Package, RefreshCw, Play, Loader2, ChevronRight } from 'lucide-react'
import type { DeploymentStatus } from '../../types/workflow'

type StageId = 'save' | 'compile' | 'deploy' | 'run'

interface StageConfig {
  id: StageId
  label: string
  icon: typeof Save
}

const STAGES: StageConfig[] = [
  { id: 'save', label: 'Save', icon: Save },
  { id: 'compile', label: 'Compile', icon: Package },
  { id: 'deploy', label: 'Sync to Aevatar', icon: RefreshCw },
  { id: 'run', label: 'Run', icon: Play },
]

function getStageState(
  stageId: StageId,
  dirty: boolean,
  status: DeploymentStatus | undefined,
  loadingStage: StageId | null,
): 'active' | 'done' | 'disabled' | 'loading' | 'warn' {
  if (loadingStage === stageId) return 'loading'

  const s = status ?? 'draft'

  switch (stageId) {
    case 'save':
      return dirty ? 'active' : 'done'
    case 'compile':
      if (dirty) return 'disabled'
      if (s === 'out_of_sync') return 'warn'
      if (s === 'draft') return 'active'
      return s === 'compiled' || s === 'deployed' ? 'done' : 'active'
    case 'deploy':
      if (dirty) return 'disabled'
      if (s === 'draft') return 'disabled'
      if (s === 'out_of_sync') return 'disabled'
      if (s === 'compiled') return 'active'
      return s === 'deployed' ? 'done' : 'disabled'
    case 'run':
      if (s === 'deployed' && !dirty) return 'active'
      return 'disabled'
  }
}

const STATE_STYLES: Record<string, { bg: string; border: string; color: string; opacity: number }> = {
  active:   { bg: 'rgba(125,211,252,0.1)',  border: 'rgba(125,211,252,0.4)',  color: 'var(--neon-cyan)', opacity: 1 },
  done:     { bg: 'rgba(134,239,172,0.08)', border: 'rgba(134,239,172,0.25)', color: 'var(--neon-green)', opacity: 0.8 },
  disabled: { bg: 'transparent',           border: 'rgba(136,136,136,0.2)', color: '#666',    opacity: 0.4 },
  loading:  { bg: 'rgba(125,211,252,0.1)',  border: 'rgba(125,211,252,0.4)',  color: 'var(--neon-cyan)', opacity: 1 },
  warn:     { bg: 'rgba(252,165,165,0.1)',  border: 'rgba(252,165,165,0.3)',  color: 'var(--neon-red)', opacity: 1 },
}

interface WorkflowPipelineProps {
  dirty: boolean
  deploymentStatus?: DeploymentStatus
  loadingStage: StageId | null
  onSave: () => void
  onCompile: () => void
  onDeploy: () => void
  onRun: () => void
}

export default function WorkflowPipeline({
  dirty,
  deploymentStatus,
  loadingStage,
  onSave,
  onCompile,
  onDeploy,
  onRun,
}: WorkflowPipelineProps) {
  const handlers: Record<StageId, () => void> = { save: onSave, compile: onCompile, deploy: onDeploy, run: onRun }

  return (
    <div className="flex items-center gap-0">
      {STAGES.map((stage, i) => {
        const state = getStageState(stage.id, dirty, deploymentStatus, loadingStage)
        const style = STATE_STYLES[state]
        const Icon = stage.icon
        const isDisabled = state === 'disabled'
        const isLoading = state === 'loading'

        return (
          <div key={stage.id} className="flex items-center">
            {i > 0 && (
              <ChevronRight
                size={14}
                style={{ color: 'var(--text-dimmed)', opacity: 0.3, margin: '0 2px' }}
              />
            )}
            <button
              onClick={handlers[stage.id]}
              disabled={isDisabled || isLoading}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium transition-all disabled:cursor-not-allowed"
              style={{
                background: style.bg,
                border: `1px solid ${style.border}`,
                color: style.color,
                opacity: style.opacity,
              }}
              title={
                state === 'disabled' && stage.id !== 'save'
                  ? 'Complete previous steps first'
                  : state === 'warn'
                    ? 'Out of sync — re-compile needed'
                    : stage.label
              }
            >
              {isLoading ? (
                <Loader2 size={13} className="animate-spin" />
              ) : (
                <Icon size={13} />
              )}
              {stage.label}
            </button>
          </div>
        )
      })}
    </div>
  )
}

export type { StageId }
