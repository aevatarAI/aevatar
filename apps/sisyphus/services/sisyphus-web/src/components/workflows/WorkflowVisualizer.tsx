import { useMemo, useCallback, useState } from 'react'
import ReactFlow, {
  type Node,
  type Edge,
  Background,
  Controls,
  MarkerType,
  Position,
} from 'reactflow'
import dagre from 'dagre'
import 'reactflow/dist/style.css'
import SkillPopover from './SkillPopover'
import { X } from 'lucide-react'

interface ParsedStep {
  name: string
  type: string
  connector?: string
  skill_id?: string
  next?: string[]
  children?: string[]
}

interface ConnectorInfo {
  id: string
  name: string
  type: string
  [key: string]: unknown
}

interface WorkflowVisualizerProps {
  yamlContent: string
  roles?: Array<{ name: string; skill_id?: string }>
  onStepClick?: (stepName: string) => void
  connectors?: Array<{ id: string; name: string; type: string; [key: string]: unknown }>
}

const STEP_TYPE_COLORS: Record<string, string> = {
  llm_call: 'var(--neon-gold)',
  connector_call: 'var(--neon-cyan)',
  transform: '#bf7fff',
  while: '#ff6600',
  foreach: '#ff6600',
  parallel: 'var(--neon-green)',
  workflow_call: '#ff00aa',
}

/** Minimal YAML parser for workflow steps — handles both map-style and list-style formats */
function parseWorkflowYaml(yamlStr: string): { steps: ParsedStep[]; roles: Array<{ name: string; skill_id?: string }> } {
  const steps: ParsedStep[] = []
  const roles: Array<{ name: string; skill_id?: string }> = []

  const lines = yamlStr.split('\n')
  let inSteps = false
  let inRoles = false
  let currentStep: Partial<ParsedStep> & { name?: string } = {}
  let currentRole: { name?: string; skill_id?: string } = {}

  const flushStep = () => {
    if (currentStep.name && currentStep.type) {
      steps.push({ name: currentStep.name, type: currentStep.type, connector: currentStep.connector, skill_id: currentStep.skill_id })
    }
    currentStep = {}
  }

  const flushRole = () => {
    if (currentRole.name) {
      roles.push({ name: currentRole.name, skill_id: currentRole.skill_id })
    }
    currentRole = {}
  }

  for (const line of lines) {
    const trimmed = line.trimStart()
    const indent = line.length - trimmed.length

    if (indent === 0 && /^steps:/.test(trimmed)) { flushStep(); flushRole(); inSteps = true; inRoles = false; continue }
    if (indent === 0 && /^roles:/.test(trimmed)) { flushStep(); flushRole(); inSteps = false; inRoles = true; continue }
    if (indent === 0 && /^\w+:/.test(trimmed) && !trimmed.startsWith('-')) { flushStep(); flushRole(); inSteps = false; inRoles = false; continue }

    if (inSteps) {
      const listMatch = trimmed.match(/^-\s+(?:id|name):\s*(.+)/)
      if (listMatch) { flushStep(); currentStep.name = listMatch[1].trim(); continue }
      if (indent === 2 && trimmed.endsWith(':') && !trimmed.startsWith('-')) { flushStep(); currentStep.name = trimmed.slice(0, -1).trim(); continue }
      const kv = trimmed.replace(/^-\s*/, '').match(/^(\w+):\s*(.+)?$/)
      if (kv) {
        const [, key, rawVal] = kv
        const val = rawVal?.trim().replace(/^["']|["']$/g, '') ?? ''
        if (key === 'type') currentStep.type = val
        if (key === 'connector') currentStep.connector = val
        if (key === 'skill_id') currentStep.skill_id = val
      }
    }

    if (inRoles) {
      const listMatch = trimmed.match(/^-\s+name:\s*(.+)/)
      if (listMatch) { flushRole(); currentRole.name = listMatch[1].trim(); continue }
      if (indent === 2 && trimmed.endsWith(':') && !trimmed.startsWith('-')) { flushRole(); currentRole.name = trimmed.slice(0, -1).trim(); continue }
      const kv = trimmed.replace(/^-\s*/, '').match(/^(\w+):\s*(.+)?$/)
      if (kv) {
        const [, key, rawVal] = kv
        const val = rawVal?.trim().replace(/^["']|["']$/g, '') ?? ''
        if (key === 'skill_id' || key === 'skillId') currentRole.skill_id = val
      }
    }
  }

  flushStep()
  flushRole()

  return { steps, roles }
}

function getLayoutedElements(
  stepNodes: Node[],
  stepEdges: Edge[],
  connectorNodes: Node[],
  connectorEdges: Edge[],
): { nodes: Node[]; edges: Edge[] } {
  // Layout only step nodes vertically
  const g = new dagre.graphlib.Graph()
  g.setDefaultEdgeLabel(() => ({}))
  g.setGraph({ rankdir: 'TB', nodesep: 40, ranksep: 60 })

  stepNodes.forEach((node) => {
    g.setNode(node.id, { width: 220, height: 80 })
  })

  stepEdges.forEach((edge) => {
    g.setEdge(edge.source, edge.target)
  })

  dagre.layout(g)

  const layoutedSteps = stepNodes.map((node) => {
    const pos = g.node(node.id)
    return {
      ...node,
      position: { x: pos.x - 110, y: pos.y - 40 },
      targetPosition: Position.Top,
      sourcePosition: Position.Bottom,
    }
  })

  // Build a map of step positions for connector placement
  const stepPosMap = new Map<string, { x: number; y: number }>()
  for (const n of layoutedSteps) {
    stepPosMap.set(n.id, n.position)
  }

  // Place connector nodes to the right of their first referencing step
  const placedConnectors = new Map<string, { x: number; y: number }>()
  const layoutedConnectors: Node[] = connectorNodes.map((cNode) => {
    const connName = cNode.id.replace('__connector_', '')
    // Find the first step that uses this connector
    const refEdge = connectorEdges.find((e) => e.target === cNode.id)
    const stepPos = refEdge ? stepPosMap.get(refEdge.source) : undefined
    const pos = stepPos
      ? { x: stepPos.x + 300, y: stepPos.y + 15 }
      : { x: 400, y: placedConnectors.size * 80 }
    placedConnectors.set(connName, pos)
    return {
      ...cNode,
      position: pos,
      targetPosition: Position.Left,
      sourcePosition: Position.Left,
    }
  })

  // Update connector edges to use right→left handles
  const layoutedConnEdges: Edge[] = connectorEdges.map((e) => ({
    ...e,
    sourceHandle: 'right',
    targetHandle: 'left',
  }))

  return {
    nodes: [...layoutedSteps, ...layoutedConnectors],
    edges: [...stepEdges, ...layoutedConnEdges],
  }
}

export default function WorkflowVisualizer({ yamlContent, roles: externalRoles, onStepClick, connectors }: WorkflowVisualizerProps) {
  const [hoveredSkill, setHoveredSkill] = useState<{ skillId: string; x: number; y: number } | null>(null)
  const [viewingConnector, setViewingConnector] = useState<ConnectorInfo | null>(null)

  const connectorMap = useMemo(() => {
    const m = new Map<string, ConnectorInfo>()
    if (connectors) {
      for (const c of connectors) m.set(c.name, c)
    }
    return m
  }, [connectors])

  const { nodes, edges } = useMemo(() => {
    try {
      const { steps, roles } = parseWorkflowYaml(yamlContent)

      const roleSkillMap = new Map<string, string>()
      for (const r of (externalRoles ?? roles)) {
        if (r.skill_id) roleSkillMap.set(r.name, r.skill_id)
      }

      const flowNodes: Node[] = steps.map((step) => {
        const color = STEP_TYPE_COLORS[step.type] ?? '#888'
        const skillId = step.skill_id ?? roleSkillMap.get(step.name)

        return {
          id: step.name,
          data: {
            label: (
              <div className="text-left px-2 py-1 w-full">
                <div className="text-[11px] font-semibold truncate" style={{ color: 'var(--text-primary)' }}>
                  {step.name}
                </div>
                <div className="text-[10px] mt-0.5" style={{ color }}>
                  {step.type}
                </div>
                {step.connector && (
                  <div className="text-[9px] mt-0.5 font-mono" style={{ color: 'var(--text-dimmed)' }}>
                    connector: {step.connector}
                  </div>
                )}
                {skillId && (
                  <div className="text-[9px] mt-0.5 font-mono cursor-help" style={{ color: '#bf7fff' }}>
                    ornn: {skillId}
                  </div>
                )}
              </div>
            ),
            skillId,
          },
          position: { x: 0, y: 0 },
          style: {
            width: 220,
            background: 'rgba(20,20,22,0.95)',
            border: `1px solid ${color}40`,
            borderRadius: 6,
            padding: 0,
            cursor: 'pointer',
          },
        }
      })

      // Sequential edges
      const flowEdges: Edge[] = []
      for (let i = 0; i < steps.length; i++) {
        const step = steps[i]
        if (step.next && step.next.length > 0) {
          for (const target of step.next) {
            flowEdges.push({
              id: `${step.name}->${target}`,
              source: step.name,
              target,
              markerEnd: { type: MarkerType.ArrowClosed, color: '#444' },
              style: { stroke: '#333', strokeWidth: 1 },
            })
          }
        } else if (i < steps.length - 1) {
          flowEdges.push({
            id: `${step.name}->${steps[i + 1].name}`,
            source: step.name,
            target: steps[i + 1].name,
            markerEnd: { type: MarkerType.ArrowClosed, color: '#444' },
            style: { stroke: '#333', strokeWidth: 1 },
          })
        }
      }

      // Connector downstream nodes + edges (separate from step flow)
      const connectorNodes: Node[] = []
      const connectorEdges: Edge[] = []
      const addedConnectors = new Set<string>()
      for (const step of steps) {
        if (!step.connector) continue
        const connId = `__connector_${step.connector}`
        if (!addedConnectors.has(step.connector)) {
          addedConnectors.add(step.connector)
          connectorNodes.push({
            id: connId,
            data: {
              label: (
                <div className="text-center px-2 py-1 w-full">
                  <div className="text-[10px] font-semibold" style={{ color: 'var(--neon-cyan)' }}>
                    {step.connector}
                  </div>
                  <div className="text-[9px]" style={{ color: '#666' }}>downstream</div>
                </div>
              ),
              connectorName: step.connector,
            },
            position: { x: 0, y: 0 },
            style: {
              width: 160,
              background: 'rgba(0,212,255,0.06)',
              border: '1px dashed rgba(0,212,255,0.3)',
              borderRadius: 8,
              padding: 0,
              cursor: 'pointer',
            },
          })
        }
        connectorEdges.push({
          id: `${step.name}->conn:${step.connector}`,
          source: step.name,
          target: connId,
          markerEnd: { type: MarkerType.ArrowClosed, color: 'var(--neon-cyan)40' },
          style: { stroke: 'var(--neon-cyan)30', strokeWidth: 1, strokeDasharray: '4 3' },
          data: { connectorName: step.connector },
        })
      }

      return getLayoutedElements(flowNodes, flowEdges, connectorNodes, connectorEdges)
    } catch {
      return { nodes: [], edges: [] }
    }
  }, [yamlContent, externalRoles])

  const handleNodeMouseEnter = useCallback((_: React.MouseEvent, node: Node) => {
    if (node.data?.skillId) {
      setHoveredSkill({
        skillId: node.data.skillId,
        x: (node.position?.x ?? 0) + 230,
        y: (node.position?.y ?? 0),
      })
    }
  }, [])

  const handleNodeMouseLeave = useCallback(() => {
    setHoveredSkill(null)
  }, [])

  const handleNodeClick = useCallback((_: React.MouseEvent, node: Node) => {
    // If it's a connector downstream node, show connector JSON
    if (node.data?.connectorName) {
      const conn = connectorMap.get(node.data.connectorName)
      if (conn) {
        setViewingConnector(conn)
        return
      }
    }
    // Otherwise it's a step node
    if (onStepClick) onStepClick(node.id)
  }, [onStepClick, connectorMap])

  const handleEdgeClick = useCallback((_: React.MouseEvent, edge: Edge) => {
    if (edge.data?.connectorName) {
      const conn = connectorMap.get(edge.data.connectorName)
      if (conn) setViewingConnector(conn)
    }
  }, [connectorMap])

  return (
    <div className="h-full w-full relative" style={{ background: 'var(--bg-base)' }}>
      <ReactFlow
        nodes={nodes}
        edges={edges}
        onNodeMouseEnter={handleNodeMouseEnter}
        onNodeMouseLeave={handleNodeMouseLeave}
        onNodeClick={handleNodeClick}
        onEdgeClick={handleEdgeClick}
        fitView
        proOptions={{ hideAttribution: true }}
        style={{ background: 'transparent' }}
        nodesDraggable={false}
        nodesConnectable={false}
      >
        <Background color="#222" gap={20} />
        <Controls
          showInteractive={false}
          style={{ background: 'var(--bg-elevated)', borderColor: 'var(--border-default)' }}
        />
      </ReactFlow>

      {hoveredSkill && (
        <SkillPopover
          skillId={hoveredSkill.skillId}
          style={{ position: 'absolute', left: hoveredSkill.x, top: hoveredSkill.y }}
        />
      )}

      {/* Connector JSON viewer */}
      {viewingConnector && (
        <div
          className="absolute top-4 right-4 z-50 w-[380px] max-h-[60vh] flex flex-col rounded-lg overflow-hidden animate-scale-in"
          style={{ background: 'var(--bg-surface)', border: '1px solid var(--border-default)', boxShadow: '0 4px 20px rgba(0,0,0,0.5)' }}
        >
          <div className="flex items-center justify-between px-4 py-2 shrink-0" style={{ borderBottom: '1px solid var(--border-default)' }}>
            <div className="flex items-center gap-2">
              <span className="text-xs font-semibold" style={{ color: 'var(--neon-cyan)' }}>{viewingConnector.name}</span>
              <span
                className="text-[9px] font-medium uppercase px-1.5 py-0.5 rounded"
                style={{
                  background: viewingConnector.type === 'http' ? 'rgba(125,211,252,0.1)' : 'rgba(196,181,253,0.1)',
                  color: viewingConnector.type === 'http' ? 'var(--neon-cyan)' : 'var(--neon-purple)',
                }}
              >
                {viewingConnector.type}
              </span>
            </div>
            <button onClick={() => setViewingConnector(null)} className="icon-btn"><X size={14} /></button>
          </div>
          <pre
            className="flex-1 overflow-auto p-3 text-[10px] font-mono"
            style={{ color: 'var(--text-secondary)', margin: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}
          >
            {JSON.stringify(viewingConnector, null, 2)}
          </pre>
        </div>
      )}
    </div>
  )
}
