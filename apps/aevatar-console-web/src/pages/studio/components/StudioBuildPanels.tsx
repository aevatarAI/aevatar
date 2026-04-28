import { parseCustomEvent } from '@aevatar-react-sdk/agui';
import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from '@aevatar-react-sdk/types';
import {
  CheckCircleOutlined,
  CodeOutlined,
  PlayCircleOutlined,
  RobotOutlined,
  ApartmentOutlined,
} from '@ant-design/icons';
import {
  Alert,
  Button,
  Empty,
  Input,
  message,
  Radio,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import type { Node } from '@xyflow/react';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import { parseRunContextData } from '@/shared/agui/customEventData';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import {
  buildRuntimeGAgentAssemblyQualifiedName,
  buildRuntimeGAgentTypeLabel,
  type RuntimeGAgentTypeDescriptor,
} from '@/shared/models/runtime/gagents';
import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  addPackageFile,
  createSingleSourcePackage,
  deserializePersistedSource,
  getPackageEntries,
  getSelectedPackageEntry,
  removePackageFile,
  renamePackageFile,
  serializePersistedSource,
  setEntrySourcePath,
  updatePackageFileContent,
  updateEntryBehaviorTypeName,
} from '@/shared/studio/scriptPackage';
import {
  createStepInspectorDraft,
  parseInspectorParameters,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import type {
  DraftRunResult,
  ScopedScriptDetail,
  ScopedScriptSummary,
  ScopeScriptAcceptedSummary,
  ScopeScriptUpsertAcceptedResponse,
  ScriptPromotionDecision,
  ScriptValidationDiagnostic,
  ScriptValidationResult,
} from '@/shared/studio/scriptsModels';
import type { StudioGraphStep } from '@/shared/studio/graph';
import { describeError } from '@/shared/ui/errorText';
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
  joinInteractiveClassNames,
} from '@/shared/ui/interactionStandards';
import ScriptCodeEditor, {
  type ScriptEditorFocusTarget,
  type ScriptEditorMarker,
} from '@/modules/studio/scripts/ScriptCodeEditor';

const buildWorkbenchGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1fr) minmax(340px, 380px)',
  minHeight: 0,
  minWidth: 0,
};

const buildWorkbenchPrimaryColumnStyle: React.CSSProperties = {
  alignSelf: 'start',
  display: 'grid',
  gap: 16,
  minHeight: 0,
  minWidth: 0,
};

const workflowWorkbenchLayoutStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1fr)',
  minHeight: 0,
  minWidth: 0,
};

const workflowEditingSurfaceHeight = 'clamp(560px, calc(100vh - 320px), 760px)';
const SCRIPT_SAVE_OBSERVATION_POLL_DELAYS_MS = [
  1000,
  2000,
  3000,
  5000,
  5000,
  5000,
] as const;

const SCRIPT_STARTER_SOURCE = `using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Studio.Application.Scripts.Contracts;

public sealed class DraftBehavior : ScriptBehavior<AppScriptReadModel, AppScriptReadModel>
{
    protected override void Configure(IScriptBehaviorBuilder<AppScriptReadModel, AppScriptReadModel> builder)
    {
        builder
            .OnCommand<AppScriptCommand>(HandleAsync)
            .OnEvent<AppScriptUpdated>(
                apply: static (_, evt, _) => evt.Current?.Clone() ?? new AppScriptReadModel())
            .ProjectState(static (state, _) => state?.Clone() ?? new AppScriptReadModel());
    }

    private static Task HandleAsync(
        AppScriptCommand input,
        ScriptCommandContext<AppScriptReadModel> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var commandId = context.CommandId ?? input?.CommandId ?? string.Empty;
        var rawInput = input?.Input ?? string.Empty;
        var normalized = rawInput.Trim();
        var current = new AppScriptReadModel
        {
            Input = rawInput,
            Output = normalized.ToUpperInvariant(),
            Status = normalized.Length == 0 ? "empty" : "ok",
            LastCommandId = commandId,
        };

        current.Notes.Add(normalized.Length == 0 ? "no-input" : "trimmed");
        current.Notes.Add("uppercased");

        context.Emit(new AppScriptUpdated
        {
            Current = current,
        });

        return Task.CompletedTask;
    }
}`;

function createScriptStarterPackage() {
  return createSingleSourcePackage(SCRIPT_STARTER_SOURCE);
}

const workflowWorkspaceRowStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'flex',
  gap: 16,
  minHeight: workflowEditingSurfaceHeight,
  minWidth: 0,
};

const buildSurfaceCardStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e8dfd0',
  borderRadius: 24,
  boxShadow: '0 18px 42px rgba(15, 23, 42, 0.06)',
  display: 'grid',
  gap: 18,
  padding: 24,
};

const sectionEyebrowStyle: React.CSSProperties = {
  color: '#8b7b63',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const sectionDescriptionStyle: React.CSSProperties = {
  color: '#5f5b53',
  fontSize: 13,
  lineHeight: '22px',
};

const statusTagStyle: React.CSSProperties = {
  borderRadius: 999,
  fontSize: 11,
  fontWeight: 600,
  lineHeight: '16px',
  padding: '2px 8px',
};

const workflowToolbarStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'space-between',
};

const workflowStageActionsStyle: React.CSSProperties = {
  background: 'rgba(255, 255, 255, 0.96)',
  border: '1px solid #e8dfd0',
  borderRadius: 18,
  display: 'grid',
  gap: 10,
  gridColumn: '1 / -1',
  padding: '12px 16px',
  position: 'sticky',
  top: 0,
  zIndex: 2,
};

const workflowStageActionsRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'space-between',
};

const workflowToolbarActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
};

const workflowViewSwitchStyle: React.CSSProperties = {
  background: '#f8f3e8',
  border: '1px solid #eadfcd',
  borderRadius: 999,
  display: 'inline-flex',
  padding: 4,
};

const workflowCanvasSurfaceStyle: React.CSSProperties = {
  background: '#fdfaf4',
  border: '1px solid #ede5d8',
  borderRadius: 22,
  flex: '1 1 auto',
  minHeight: 0,
  overflow: 'hidden',
  padding: 12,
};

const workflowCanvasPanelStyle: React.CSSProperties = {
  ...buildSurfaceCardStyle,
  display: 'grid',
  flex: '8 1 0',
  gap: 16,
  gridTemplateRows: 'auto minmax(0, 1fr)',
  height: '100%',
  minWidth: 0,
  overflow: 'hidden',
};

const workflowCanvasBodyStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
};

const workflowStepDetailCardStyle: React.CSSProperties = {
  ...buildSurfaceCardStyle,
  display: 'grid',
  flex: '2 1 320px',
  gap: 16,
  gridTemplateRows: 'auto auto minmax(0, 1fr)',
  height: '100%',
  maxWidth: 360,
  minWidth: 0,
  overflow: 'hidden',
};

const workflowStepDetailBodyStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  minHeight: 0,
  overflowY: 'auto',
  paddingRight: 4,
};

const workflowDryRunSectionStyle: React.CSSProperties = {
  alignSelf: 'stretch',
  background: '#ffffff',
  border: '1px solid #e8dfd0',
  borderRadius: 20,
  display: 'grid',
  gap: 12,
  minWidth: 0,
  padding: 18,
  position: 'relative',
  width: '100%',
  zIndex: 0,
};

const workflowDryRunOutputStyle: React.CSSProperties = {
  background: '#faf8f3',
  border: '1px solid #efe7da',
  borderRadius: 14,
  color: '#425466',
  fontFamily: 'Monaco, Menlo, monospace',
  fontSize: 12,
  lineHeight: '20px',
  margin: 0,
  maxHeight: 140,
  minHeight: 96,
  overflow: 'auto',
  padding: 12,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const workflowDetailsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'minmax(0, 1fr)',
};

const workflowFieldStyle: React.CSSProperties = {
  display: 'grid',
  gap: 6,
};

const workflowFieldLabelStyle: React.CSSProperties = {
  color: '#8b7b63',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};

const workflowSectionHeadingStyle: React.CSSProperties = {
  color: '#1f2937',
  fontSize: 12,
  fontWeight: 700,
  lineHeight: '18px',
};

const workflowTypePickerStyle: React.CSSProperties = {
  background: '#fdf9f2',
  border: '1px solid #ede3d1',
  borderRadius: 18,
  display: 'grid',
  gap: 10,
  gridTemplateRows: 'auto auto minmax(0, 1fr)',
  maxHeight: 'min(360px, calc(100vh - 420px))',
  minHeight: 0,
  overflow: 'hidden',
  padding: 14,
};

const workflowTypePickerGridStyle: React.CSSProperties = {
  alignContent: 'start',
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
  minHeight: 0,
  overflowY: 'auto',
  paddingRight: 4,
};

const workflowTypeOptionStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e8dfd0',
  borderRadius: 16,
  cursor: 'pointer',
  display: 'grid',
  gap: 4,
  minHeight: 72,
  padding: 12,
  textAlign: 'left',
};

const workflowInlineMetaStyle: React.CSSProperties = {
  color: '#6b7280',
  fontSize: 12,
  lineHeight: '18px',
};

const workflowAdvancedSectionStyle: React.CSSProperties = {
  background: '#faf8f3',
  border: '1px solid #efe7da',
  borderRadius: 16,
  padding: 12,
};

const dryRunAsideStyle: React.CSSProperties = {
  alignSelf: 'start',
  background: '#ffffff',
  border: '1px solid #e8dfd0',
  borderRadius: 20,
  display: 'grid',
  gap: 14,
  padding: 20,
  position: 'sticky',
  top: 12,
};

const dryRunOutputStyle: React.CSSProperties = {
  background: '#faf8f3',
  border: '1px solid #efe7da',
  borderRadius: 14,
  color: '#425466',
  fontFamily: 'Monaco, Menlo, monospace',
  fontSize: 12,
  lineHeight: '20px',
  margin: 0,
  maxHeight: 240,
  minHeight: 180,
  overflow: 'auto',
  padding: 12,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const dryRunSummaryStyle: React.CSSProperties = {
  ...dryRunOutputStyle,
  color: '#5f5b53',
  maxHeight: 140,
  minHeight: 0,
};

const dryRunDebugDetailsStyle: React.CSSProperties = {
  background: '#fcfaf6',
  border: '1px solid #efe7da',
  borderRadius: 14,
  padding: 12,
};

const dryRunDebugSummaryStyle: React.CSSProperties = {
  color: '#5f5b53',
  cursor: 'pointer',
  fontSize: 12,
  fontWeight: 600,
  listStyle: 'none',
};

const modalCardStyle: React.CSSProperties = {
  background: '#fcfaf6',
  border: '1px solid #efe7da',
  borderRadius: 18,
  display: 'grid',
  gap: 12,
  padding: 18,
};

type DraftRunState = {
  readonly actorId: string;
  readonly assistantText: string;
  readonly commandId: string;
  readonly error: string;
  readonly events: readonly AGUIEvent[];
  readonly finalOutput: string;
  readonly runId: string;
  readonly status: 'idle' | 'running' | 'success' | 'error';
};

const IDLE_DRAFT_RUN_STATE: DraftRunState = {
  actorId: '',
  assistantText: '',
  commandId: '',
  error: '',
  events: [],
  finalOutput: '',
  runId: '',
  status: 'idle',
};

function getRunDebugLines(state: DraftRunState): string[] {
  return [
    state.runId.trim() ? `runId: ${state.runId.trim()}` : '',
    state.actorId.trim() ? `actorId: ${state.actorId.trim()}` : '',
    state.commandId.trim() ? `commandId: ${state.commandId.trim()}` : '',
    state.events.length > 0 ? `events: ${state.events.length}` : '',
  ].filter(Boolean);
}

function renderRunOutput(state: DraftRunState): string {
  if (state.error.trim()) {
    return state.error.trim();
  }

  if (state.finalOutput.trim()) {
    return state.finalOutput.trim();
  }

  if (state.assistantText.trim()) {
    return state.assistantText.trim();
  }

  if (state.status === 'running') {
    return 'Waiting for assistant output...';
  }

  if (state.status === 'success' && getRunDebugLines(state).length > 0) {
    return 'Run completed, but no assistant output was returned.';
  }

  return 'Run the current draft to inspect the assistant output here.';
}

function renderRunSummary(state: DraftRunState): string {
  return getRunDebugLines(state).join('\n');
}

function extractRunFinishedOutput(result: unknown): string {
  if (typeof result === 'string') {
    return result;
  }

  if (!result || typeof result !== 'object' || Array.isArray(result)) {
    return '';
  }

  const record = result as Record<string, unknown>;
  const candidate = record.output ?? record.Output ?? record.message ?? record.text;
  return typeof candidate === 'string' ? candidate : '';
}

function tryParseStepParameters(
  value: string,
): Record<string, unknown> | null {
  try {
    return parseInspectorParameters(value);
  } catch {
    return null;
  }
}

function formatParameterEditorValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (typeof value === 'string') {
    return value;
  }

  if (
    typeof value === 'number' ||
    typeof value === 'boolean'
  ) {
    return String(value);
  }

  return JSON.stringify(value, null, 2);
}

function coerceParameterEditorValue(
  rawValue: string,
  parameterType: string,
): unknown {
  const trimmed = rawValue.trim();
  const normalizedType = parameterType.trim().toLowerCase();

  if (!trimmed) {
    return '';
  }

  if (
    normalizedType === 'bool' ||
    normalizedType === 'boolean'
  ) {
    return trimmed.toLowerCase() === 'true';
  }

  if (
    normalizedType === 'number' ||
    normalizedType === 'int' ||
    normalizedType === 'int32' ||
    normalizedType === 'int64' ||
    normalizedType === 'float' ||
    normalizedType === 'double'
  ) {
    const parsed = Number(trimmed);
    return Number.isFinite(parsed) ? parsed : trimmed;
  }

  if (
    (normalizedType === 'json' ||
      normalizedType === 'object' ||
      normalizedType === 'array' ||
      normalizedType === 'map') &&
    ((trimmed.startsWith('{') && trimmed.endsWith('}')) ||
      (trimmed.startsWith('[') && trimmed.endsWith(']')))
  ) {
    try {
      return JSON.parse(trimmed);
    } catch {
      return trimmed;
    }
  }

  return trimmed;
}

function updateStepDraftParameterValue(
  draft: StudioStepInspectorDraft,
  parameterName: string,
  parameterType: string,
  rawValue: string,
): StudioStepInspectorDraft {
  const nextParameters = tryParseStepParameters(draft.parametersText) ?? {};
  const trimmed = rawValue.trim();

  if (!trimmed) {
    delete nextParameters[parameterName];
  } else {
    nextParameters[parameterName] = coerceParameterEditorValue(rawValue, parameterType);
  }

  return {
    ...draft,
    parametersText: JSON.stringify(nextParameters, null, 2),
  };
}

async function consumeAguiDraftRun(
  response: Response,
  signal: AbortSignal,
    onChange: React.Dispatch<React.SetStateAction<DraftRunState>>,
): Promise<void> {
  for await (const event of parseBackendSSEStream(response, { signal })) {
    if (signal.aborted) {
      break;
    }

    onChange((current) => {
      const nextEvents = [...current.events, event];
      let nextAssistantText = current.assistantText;
      let nextFinalOutput = current.finalOutput;
      let nextActorId = current.actorId;
      let nextCommandId = current.commandId;
      let nextRunId = current.runId;
      let nextError = current.error;
      let nextStatus = current.status;

      if (event.type === AGUIEventType.TEXT_MESSAGE_CONTENT) {
        nextAssistantText += String((event as { delta?: string }).delta || '');
      }

      if (event.type === AGUIEventType.TEXT_MESSAGE_END) {
        const finalAssistantText =
          String(
            (event as { message?: string; delta?: string }).message ||
              (event as { delta?: string }).delta ||
              '',
          ) || '';
        if (!nextAssistantText.trim() && finalAssistantText.trim()) {
          nextAssistantText = finalAssistantText;
        }
        if (finalAssistantText.trim()) {
          nextFinalOutput = finalAssistantText.trim();
        }
      }

      if (event.type === AGUIEventType.RUN_STARTED) {
        nextRunId = String((event as { runId?: string }).runId || nextRunId);
        nextActorId = String(
          (event as { actorId?: string; threadId?: string }).actorId ||
            (event as { threadId?: string }).threadId ||
            nextActorId,
        );
      }

      if (event.type === AGUIEventType.RUN_FINISHED) {
        const finalOutput = extractRunFinishedOutput(
          (event as { result?: unknown }).result,
        );
        if (finalOutput.trim()) {
          nextFinalOutput = finalOutput.trim();
        }
        nextStatus = 'success';
      }

      if (event.type === AGUIEventType.CUSTOM) {
        try {
          const custom = parseCustomEvent(event);
          if (custom.name === CustomEventName.RunContext) {
            const context = parseRunContextData(custom.data);
            nextActorId = context?.actorId || nextActorId;
            nextCommandId = context?.commandId || nextCommandId;
          }
        } catch {
          // Ignore malformed custom frames and keep the visible transcript flowing.
        }
      }

      if (event.type === AGUIEventType.RUN_ERROR) {
        nextError =
          String((event as { message?: string }).message || '').trim() ||
          'Draft run failed.';
        nextStatus = 'error';
      }

      return {
        actorId: nextActorId,
        assistantText: nextAssistantText,
        commandId: nextCommandId,
        error: nextError,
        events: nextEvents,
        finalOutput: nextFinalOutput,
        runId: nextRunId,
        status: nextStatus,
      };
    });
  }
}

function mapScriptMarkers(
  diagnostics: readonly ScriptValidationDiagnostic[] | undefined,
  activeFilePath: string,
): ScriptEditorMarker[] {
  return (diagnostics ?? [])
    .filter((diagnostic) => {
      if (!diagnostic.filePath) {
        return true;
      }

      return diagnostic.filePath === activeFilePath;
    })
    .map((diagnostic) => ({
      startLineNumber: Math.max(diagnostic.startLine || 1, 1),
      startColumn: Math.max(diagnostic.startColumn || 1, 1),
      endLineNumber: Math.max(
        diagnostic.endLine || diagnostic.startLine || 1,
        diagnostic.startLine || 1,
      ),
      endColumn: Math.max(
        diagnostic.endColumn || (diagnostic.startColumn || 1) + 1,
        (diagnostic.startColumn || 1) + 1,
      ),
      severity: diagnostic.severity,
      message: diagnostic.code
        ? `[${diagnostic.code}] ${diagnostic.message}`
        : diagnostic.message,
      code: diagnostic.code || undefined,
      source: diagnostic.origin || undefined,
    }));
}

function formatScriptDiagnosticLocation(diagnostic: ScriptValidationDiagnostic): string {
  const filePath = diagnostic.filePath || 'source';
  if (!diagnostic.startLine || !diagnostic.startColumn) {
    return filePath;
  }

  return `${filePath}:${diagnostic.startLine}:${diagnostic.startColumn}`;
}

function buildScriptDiagnosticFocusTarget(
  diagnostic: ScriptValidationDiagnostic,
  token: string,
): ScriptEditorFocusTarget {
  const startLineNumber = Math.max(diagnostic.startLine || 1, 1);
  const startColumn = Math.max(diagnostic.startColumn || 1, 1);
  return {
    filePath: diagnostic.filePath || 'Behavior.cs',
    startLineNumber,
    startColumn,
    endLineNumber: Math.max(
      diagnostic.endLine || diagnostic.startLine || 1,
      startLineNumber,
    ),
    endColumn: Math.max(
      diagnostic.endColumn || (diagnostic.startColumn || 1) + 1,
      startColumn + 1,
    ),
    token,
  };
}

function ScriptLeaveDialog(props: {
  readonly open: boolean;
  readonly onStay: () => void;
  readonly onLeave: () => void;
}) {
  if (!props.open) {
    return null;
  }

  return (
    <div style={modalCardStyle}>
      <Typography.Text strong style={{ fontSize: 16 }}>
        Leave Script Build?
      </Typography.Text>
      <Typography.Text type="secondary">
        当前脚本草稿还没有保存。离开 Build 会丢掉这次 source editor 里的未保存修改。
      </Typography.Text>
      <Space>
        <Button className={AEVATAR_INTERACTIVE_BUTTON_CLASS} onClick={props.onStay}>
          继续编辑
        </Button>
        <Button
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          danger
          type="primary"
          onClick={props.onLeave}
        >
          离开页面
        </Button>
      </Space>
    </div>
  );
}

export type StudioWorkflowBuildPanelProps = {
  readonly draftYaml: string;
  readonly onSetDraftYaml: (value: string) => void;
  readonly onSaveDraft: () => void;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly saveNotice?: { readonly type: 'success' | 'error'; readonly message: string } | null;
  readonly workflowGraph: {
    readonly steps: readonly StudioGraphStep[];
    readonly nodes: Node[];
    readonly edges: Parameters<typeof GraphCanvas>[0]['edges'];
  };
  readonly selectedGraphNodeId: string;
  readonly onSelectGraphNode: (nodeId: string) => void;
  readonly runtimePrimitives: readonly WorkflowPrimitiveDescriptor[];
  readonly scopeId?: string;
  readonly workflowName: string;
  readonly runPrompt: string;
  readonly onRunPromptChange: (value: string) => void;
  readonly buildWorkflowYamls: () => Promise<string[]>;
  readonly runMetadata?: Record<string, string>;
  readonly dryRunRouteLabel?: string;
  readonly dryRunModelLabel?: string;
  readonly dryRunBlockedReason?: string;
  readonly onOpenRunSetup?: () => void;
  readonly availableStepTypes: readonly string[];
  readonly workflowRoles: readonly {
    readonly id: string;
    readonly name: string;
  }[];
  readonly onInsertStep: (stepType: string) => Promise<void> | void;
  readonly onApplyStepDraft: (
    draft: StudioStepInspectorDraft,
  ) => Promise<void> | void;
  readonly onRemoveSelectedStep: () => Promise<void> | void;
  readonly onDeleteWorkflowNodes: (nodeIds: string[]) => Promise<void> | void;
  readonly onAutoLayout: () => void;
  readonly onConnectNodes: (sourceNodeId: string, targetNodeId: string) => void;
  readonly onNodeLayoutChange: (
    nodes: Node[],
  ) => void;
  readonly onContinueToBind: () => void;
};

export const StudioWorkflowBuildPanel: React.FC<StudioWorkflowBuildPanelProps> = ({
  draftYaml,
  onSetDraftYaml,
  onSaveDraft,
  savePending,
  canSaveWorkflow,
  saveNotice,
  workflowGraph,
  selectedGraphNodeId,
  onSelectGraphNode,
  runtimePrimitives,
  scopeId,
  workflowName,
  runPrompt,
  onRunPromptChange,
  buildWorkflowYamls,
  runMetadata,
  dryRunRouteLabel,
  dryRunModelLabel,
  dryRunBlockedReason,
  onOpenRunSetup,
  availableStepTypes,
  workflowRoles,
  onInsertStep,
  onApplyStepDraft,
  onRemoveSelectedStep,
  onDeleteWorkflowNodes,
  onAutoLayout,
  onConnectNodes,
  onNodeLayoutChange,
  onContinueToBind,
}) => {
  const panelRef = React.useRef<HTMLDivElement | null>(null);
  const [viewMode, setViewMode] = React.useState<'canvas' | 'yaml'>('canvas');
  const [runState, setRunState] = React.useState<DraftRunState>(IDLE_DRAFT_RUN_STATE);
  const [workflowRunError, setWorkflowRunError] = React.useState('');
  const [stepTypePickerOpen, setStepTypePickerOpen] = React.useState(false);
  const [stepDraft, setStepDraft] = React.useState<StudioStepInspectorDraft | null>(
    null,
  );
  const [stepMutationPending, setStepMutationPending] = React.useState<
    '' | 'add' | 'apply' | 'remove'
  >('');
  const [stepMutationError, setStepMutationError] = React.useState('');
  const abortControllerRef = React.useRef<AbortController | null>(null);
  const runPendingRef = React.useRef(false);
  const stepMutationPendingRef = React.useRef(false);
  const stepDraftRef = React.useRef<StudioStepInspectorDraft | null>(null);
  const updateStepDraft = React.useCallback(
    (
      updater:
        | StudioStepInspectorDraft
        | null
        | ((
            current: StudioStepInspectorDraft | null,
          ) => StudioStepInspectorDraft | null),
    ) => {
      setStepDraft((current) => {
        const nextDraft =
          typeof updater === 'function'
            ? updater(current)
            : updater;
        stepDraftRef.current = nextDraft;
        return nextDraft;
      });
    },
    [],
  );
  const selectedStep = React.useMemo(() => {
    const stepId = selectedGraphNodeId.startsWith('step:')
      ? selectedGraphNodeId.slice('step:'.length)
      : '';
    return (
      workflowGraph.steps.find((item) => item.id === stepId) ||
      workflowGraph.steps[0] ||
      null
    );
  }, [selectedGraphNodeId, workflowGraph.steps]);
  const selectedNodeId = React.useMemo(
    () =>
      selectedGraphNodeId ||
      (selectedStep ? `step:${selectedStep.id}` : ''),
    [selectedGraphNodeId, selectedStep],
  );
  const selectedStepId = React.useMemo(
    () =>
      selectedStep
        ? selectedStep.id
        : selectedGraphNodeId.startsWith('step:')
          ? selectedGraphNodeId.slice('step:'.length)
          : '',
    [selectedGraphNodeId, selectedStep],
  );
  const selectedStepDraftSeed = React.useMemo(
    () =>
      selectedStep
        ? createStepInspectorDraft(selectedStep)
        : null,
    [
      selectedStep?.id,
      selectedStep?.type,
      selectedStep?.targetRole,
      selectedStep?.next,
      JSON.stringify(selectedStep?.parameters ?? {}),
      JSON.stringify(selectedStep?.branches ?? {}),
    ],
  );
  const workflowRoleIds = React.useMemo(
    () => workflowRoles.map((item) => item.id).filter(Boolean),
    [workflowRoles],
  );
  const availableNextStepIds = React.useMemo(
    () =>
      workflowGraph.steps
        .map((step) => step.id)
        .filter((stepId) => stepId && stepId !== selectedStepId),
    [selectedStepId, workflowGraph.steps],
  );
  const describedStepTypes = React.useMemo(
    () =>
      availableStepTypes.map((stepType) => {
        const descriptor =
          runtimePrimitives.find((primitive) => {
            if (primitive.name.trim().toLowerCase() === stepType.trim().toLowerCase()) {
              return true;
            }

            return primitive.aliases.some(
              (alias) => alias.trim().toLowerCase() === stepType.trim().toLowerCase(),
            );
          }) ?? null;

        return {
          stepType,
          description:
            descriptor?.description?.trim() || 'Create a new workflow step of this type.',
        };
      }),
    [availableStepTypes, runtimePrimitives],
  );
  const selectedPrimitiveDescriptor = React.useMemo(
    () =>
      runtimePrimitives.find((primitive) => {
        const selectedType = stepDraft?.type || selectedStep?.type || '';
        if (primitive.name.trim().toLowerCase() === selectedType.trim().toLowerCase()) {
          return true;
        }

        return primitive.aliases.some(
          (alias) => alias.trim().toLowerCase() === selectedType.trim().toLowerCase(),
        );
      }) ?? null,
    [runtimePrimitives, selectedStep?.type, stepDraft?.type],
  );
  const parsedStepParameters = React.useMemo(
    () =>
      stepDraft
        ? tryParseStepParameters(stepDraft.parametersText)
        : null,
    [stepDraft],
  );

  React.useEffect(() => {
    if (selectedNodeId) {
      return;
    }

    if (selectedStep) {
      onSelectGraphNode(`step:${selectedStep.id}`);
    }
  }, [onSelectGraphNode, selectedNodeId, selectedStep]);

  React.useEffect(() => {
    if (!selectedStepDraftSeed) {
      updateStepDraft(null);
      setStepMutationError('');
      return;
    }

    updateStepDraft(selectedStepDraftSeed);
    setStepMutationError('');
  }, [selectedStepDraftSeed, updateStepDraft]);

  React.useEffect(
    () => () => {
      abortControllerRef.current?.abort();
    },
    [],
  );

  const handleRun = React.useCallback(async () => {
    if (runPendingRef.current) {
      return;
    }

    if (!scopeId) {
      const visibleMessage = 'Resolve the current scope before running the workflow draft.';
      setWorkflowRunError(visibleMessage);
      void message.error(visibleMessage);
      return;
    }

    if (dryRunBlockedReason?.trim()) {
      const visibleMessage = dryRunBlockedReason.trim();
      setWorkflowRunError(visibleMessage);
      void message.error(visibleMessage);
      return;
    }

    if (!runPrompt.trim()) {
      const visibleMessage = 'Sample input is required before running the workflow draft.';
      setWorkflowRunError(visibleMessage);
      void message.error(visibleMessage);
      return;
    }

    runPendingRef.current = true;
    abortControllerRef.current?.abort();
    const controller = new AbortController();
    abortControllerRef.current = controller;
    setWorkflowRunError('');
    setRunState({
      ...IDLE_DRAFT_RUN_STATE,
      status: 'running',
    });

    try {
      const response = await runtimeRunsApi.streamDraftRun(
        scopeId,
        {
          metadata: runMetadata,
          prompt: runPrompt,
          workflowYamls: await buildWorkflowYamls(),
        },
        controller.signal,
      );

      await consumeAguiDraftRun(response, controller.signal, setRunState);
      setRunState((current) =>
        current.status === 'error' || controller.signal.aborted
          ? current
          : {
              ...current,
              status: current.events.length > 0 ? 'success' : 'idle',
            },
      );
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        return;
      }

      const rawMessage = describeError(error);
      const disconnectedProvider = rawMessage.match(/Provider '([^']+)' not connected/i);
      const visibleMessage =
        disconnectedProvider
          ? `Dry-run 还不能运行，因为 ${disconnectedProvider[1]} provider 还没有连好。先连接可用 provider，再回来运行当前 workflow draft。`
          : rawMessage;
      setWorkflowRunError(
        visibleMessage,
      );
      void message.error(visibleMessage);
      setRunState({
        ...IDLE_DRAFT_RUN_STATE,
        error: rawMessage,
        status: 'error',
      });
    } finally {
      runPendingRef.current = false;
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
      }
    }
  }, [
    buildWorkflowYamls,
    dryRunBlockedReason,
    runMetadata,
    runPrompt,
    scopeId,
  ]);

  const handleInsertStep = React.useCallback(async (stepType: string) => {
    if (stepMutationPendingRef.current) {
      return;
    }

    stepMutationPendingRef.current = true;
    setStepMutationPending('add');
    setStepMutationError('');
    try {
      await onInsertStep(stepType);
      setStepTypePickerOpen(false);
    } catch (error) {
      const visibleMessage = describeError(error);
      setStepMutationError(visibleMessage);
      void message.error(visibleMessage);
    } finally {
      stepMutationPendingRef.current = false;
      setStepMutationPending('');
    }
  }, [onInsertStep]);

  const handleApplyStepChanges = React.useCallback(async () => {
    const currentStepDraft = stepDraftRef.current;
    if (!currentStepDraft || stepMutationPendingRef.current) {
      return;
    }

    stepMutationPendingRef.current = true;
    setStepMutationPending('apply');
    setStepMutationError('');
    try {
      await onApplyStepDraft(currentStepDraft);
    } catch (error) {
      const visibleMessage = describeError(error);
      setStepMutationError(visibleMessage);
      void message.error(visibleMessage);
    } finally {
      stepMutationPendingRef.current = false;
      setStepMutationPending('');
    }
  }, [onApplyStepDraft]);

  const handleRemoveStep = React.useCallback(async () => {
    if (stepMutationPendingRef.current) {
      return;
    }

    stepMutationPendingRef.current = true;
    setStepMutationPending('remove');
    setStepMutationError('');
    try {
      await onRemoveSelectedStep();
    } catch (error) {
      const visibleMessage = describeError(error);
      setStepMutationError(visibleMessage);
      void message.error(visibleMessage);
    } finally {
      stepMutationPendingRef.current = false;
      setStepMutationPending('');
    }
  }, [onRemoveSelectedStep]);

  const handleDeleteNodes = React.useCallback(async (nodeIds: string[]) => {
    const normalizedNodeIds = nodeIds
      .map((nodeId) => String(nodeId ?? '').trim())
      .filter(Boolean);
    if (normalizedNodeIds.length === 0 || stepMutationPendingRef.current) {
      return;
    }

    stepMutationPendingRef.current = true;
    setStepMutationPending('remove');
    setStepMutationError('');
    try {
      await onDeleteWorkflowNodes(normalizedNodeIds);
    } catch (error) {
      const visibleMessage = describeError(error);
      setStepMutationError(visibleMessage);
      void message.error(visibleMessage);
    } finally {
      stepMutationPendingRef.current = false;
      setStepMutationPending('');
    }
  }, [onDeleteWorkflowNodes]);

  const workflowCanvasAutoFitKey = React.useMemo(
    () =>
      JSON.stringify({
        workflowName: workflowName || 'workflow',
        nodeIds: workflowGraph.nodes.map((node) => node.id),
        edgeIds: workflowGraph.edges.map((edge) => edge.id),
      }),
    [workflowGraph.edges, workflowGraph.nodes, workflowName],
  );

  React.useEffect(() => {
    if (viewMode !== 'canvas' || typeof window === 'undefined') {
      return;
    }

    const root = panelRef.current;
    if (!root) {
      return;
    }

    const scrollParent = root.parentElement;
    if (!scrollParent) {
      return;
    }

    const overflowY = window.getComputedStyle(scrollParent).overflowY;
    if (!/(auto|scroll)/.test(overflowY)) {
      return;
    }

    const frame = window.requestAnimationFrame(() => {
      scrollParent.scrollTop = 0;
    });

    return () => window.cancelAnimationFrame(frame);
  }, [viewMode, workflowName]);

  return (
    <div
      ref={panelRef}
      data-testid="studio-workflow-build-panel"
      style={workflowWorkbenchLayoutStyle}
    >
      <div data-testid="workflow-stage-actions" style={workflowStageActionsStyle}>
        <div style={workflowStageActionsRowStyle}>
          <div style={{ alignItems: 'center', display: 'flex', gap: 8 }}>
            <div style={sectionEyebrowStyle}>Build actions</div>
            <Tag color={canSaveWorkflow ? 'gold' : 'default'}>
              {canSaveWorkflow ? 'draft ready' : 'saved'}
            </Tag>
          </div>
          <Space wrap size={[8, 8]}>
            <Button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              disabled={!canSaveWorkflow}
              loading={savePending}
              onClick={onSaveDraft}
            >
              Save draft
            </Button>
            <Button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="primary"
              onClick={onContinueToBind}
            >
              Continue to Bind
            </Button>
          </Space>
        </div>
        {saveNotice ? (
          <Alert
            message={saveNotice.message}
            showIcon
            type={saveNotice.type === 'success' ? 'success' : 'error'}
          />
        ) : null}
      </div>

      <div data-testid="workflow-editor-workspace" style={workflowWorkspaceRowStyle}>
        <section
          data-testid="workflow-build-primary-column"
          style={workflowCanvasPanelStyle}
        >
          <div style={workflowToolbarStyle}>
            <Space wrap size={[8, 8]}>
              <div style={sectionEyebrowStyle}>DAG Canvas</div>
              <Tag color="processing">canvas · live</Tag>
              <Typography.Text type="secondary">
                {workflowName || 'Untitled workflow'}
              </Typography.Text>
            </Space>
            <div style={workflowToolbarActionsStyle}>
              <div style={workflowViewSwitchStyle}>
                <Button
                  className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                  aria-pressed={viewMode === 'canvas'}
                  onClick={() => setViewMode('canvas')}
                  size="small"
                  type={viewMode === 'canvas' ? 'primary' : 'text'}
                >
                  Canvas
                </Button>
                <Button
                  className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                  aria-pressed={viewMode === 'yaml'}
                  onClick={() => setViewMode('yaml')}
                  size="small"
                  type={viewMode === 'yaml' ? 'primary' : 'text'}
                >
                  YAML
                </Button>
              </div>
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={viewMode !== 'canvas' || Boolean(stepMutationPending)}
                loading={stepMutationPending === 'add'}
                onClick={() => setStepTypePickerOpen((current) => !current)}
              >
                Add step
              </Button>
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={viewMode !== 'canvas' || Boolean(stepMutationPending)}
                onClick={onAutoLayout}
              >
                Auto-layout
              </Button>
            </div>
          </div>
          <div style={workflowCanvasBodyStyle}>
            {stepTypePickerOpen ? (
              <div data-testid="workflow-step-type-picker" style={workflowTypePickerStyle}>
                <div style={workflowSectionHeadingStyle}>Choose step type</div>
                <div style={workflowInlineMetaStyle}>
                  先决定要插入哪种 step，再把它接到当前选中的节点后面。
                </div>
                <div
                  data-testid="workflow-step-type-picker-grid"
                  style={workflowTypePickerGridStyle}
                >
                  {describedStepTypes.map((entry) => (
                    <button
                      aria-disabled={stepMutationPending ? 'true' : undefined}
                      className={joinInteractiveClassNames(
                        AEVATAR_INTERACTIVE_BUTTON_CLASS,
                        AEVATAR_INTERACTIVE_CHIP_CLASS,
                      )}
                      disabled={Boolean(stepMutationPending)}
                      key={entry.stepType}
                      onClick={() => void handleInsertStep(entry.stepType)}
                      style={workflowTypeOptionStyle}
                      type="button"
                    >
                      <strong style={{ color: '#1f2937', fontSize: 13 }}>{entry.stepType}</strong>
                      <span style={{ color: '#6b7280', fontSize: 12, lineHeight: '18px' }}>
                        {entry.description}
                      </span>
                    </button>
                  ))}
                </div>
              </div>
            ) : null}
            {viewMode === 'canvas' ? (
              <div style={workflowCanvasSurfaceStyle}>
                <GraphCanvas
                  autoFitKey={workflowCanvasAutoFitKey}
                  bottomInset={0}
                  height="100%"
                  variant="studio"
                  nodes={[...workflowGraph.nodes]}
                  edges={[...workflowGraph.edges]}
                  selectedNodeId={selectedNodeId || undefined}
                  onNodeSelect={onSelectGraphNode}
                  onConnectNodes={onConnectNodes}
                  onDeleteNodes={handleDeleteNodes}
                  onNodeLayoutChange={onNodeLayoutChange}
                />
              </div>
            ) : (
              <section
                data-testid="workflow-yaml-panel"
                style={{ ...buildSurfaceCardStyle, flex: '1 1 auto', minHeight: 0 }}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 8,
                    justifyContent: 'space-between',
                  }}
                >
                  <div style={sectionEyebrowStyle}>Workflow YAML</div>
                  <Tag color="blue">raw draft</Tag>
                </div>
                <Input.TextArea
                  aria-label="定义 YAML"
                  autoSize={{ minRows: 18, maxRows: 28 }}
                  value={draftYaml}
                  onChange={(event) => onSetDraftYaml(event.target.value)}
                />
              </section>
            )}
          </div>
        </section>

        <section
          data-testid="workflow-step-detail-panel"
          style={workflowStepDetailCardStyle}
        >
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 8,
              justifyContent: 'space-between',
            }}
          >
            <div style={{ display: 'grid', gap: 4 }}>
              <div style={sectionEyebrowStyle}>Step Detail</div>
              {selectedStep ? <Typography.Text strong>{selectedStep.id}</Typography.Text> : null}
            </div>
            {selectedStep ? <Tag>{selectedStep.type}</Tag> : null}
          </div>
          {stepMutationError ? <Alert message={stepMutationError} showIcon type="error" /> : null}
          <div style={workflowStepDetailBodyStyle}>
            {selectedStep && stepDraft ? (
              <>
                <div style={workflowDetailsGridStyle}>
                <div style={workflowFieldStyle}>
                  <div style={workflowSectionHeadingStyle}>Basics</div>
                  <label htmlFor="workflow-step-id" style={workflowFieldLabelStyle}>
                    Step ID
                  </label>
                  <Input
                    id="workflow-step-id"
                    aria-label="Step ID"
                    value={stepDraft.id}
                    onChange={(event) =>
                      updateStepDraft((current) =>
                        current
                          ? {
                              ...current,
                              id: event.target.value,
                            }
                          : current,
                      )
                    }
                  />
                  <label htmlFor="workflow-step-type" style={workflowFieldLabelStyle}>
                    Step type
                  </label>
                  <Select
                    aria-label="Step type"
                    id="workflow-step-type"
                    options={availableStepTypes.map((stepType) => ({
                      label: stepType,
                      value: stepType,
                    }))}
                    value={stepDraft.type}
                    onChange={(value) =>
                      updateStepDraft((current) =>
                        current
                          ? {
                              ...current,
                              type: value,
                            }
                          : current,
                      )
                    }
                  />
                </div>
                <div style={workflowFieldStyle}>
                  <div style={workflowSectionHeadingStyle}>Routing</div>
                  <label htmlFor="workflow-step-role" style={workflowFieldLabelStyle}>
                    Target role
                  </label>
                  <Select
                    allowClear
                    aria-label="Target role"
                    id="workflow-step-role"
                    options={workflowRoles.map((role) => ({
                      label: `${role.name} (${role.id})`,
                      value: role.id,
                    }))}
                    placeholder={workflowRoleIds[0] || 'Select role'}
                    value={stepDraft.targetRole || undefined}
                    onChange={(value) =>
                      updateStepDraft((current) =>
                        current
                          ? {
                              ...current,
                              targetRole: value || '',
                            }
                          : current,
                      )
                    }
                  />
                  <label htmlFor="workflow-step-next" style={workflowFieldLabelStyle}>
                    Next step
                  </label>
                  <Select
                    allowClear
                    aria-label="Next step"
                    id="workflow-step-next"
                    options={availableNextStepIds.map((stepId) => ({
                      label: stepId,
                      value: stepId,
                    }))}
                    placeholder="No next step"
                    value={stepDraft.next || undefined}
                    onChange={(value) =>
                      updateStepDraft((current) =>
                        current
                          ? {
                              ...current,
                              next: value || '',
                            }
                          : current,
                      )
                    }
                  />
                </div>
                <div style={{ ...workflowFieldStyle, gridColumn: '1 / -1' }}>
                  <div style={workflowSectionHeadingStyle}>Parameters</div>
                  {selectedPrimitiveDescriptor?.parameters.length ? (
                    <div style={{ display: 'grid', gap: 10 }}>
                      {selectedPrimitiveDescriptor.parameters.map((parameter) => {
                        const currentValue = formatParameterEditorValue(
                          parsedStepParameters?.[parameter.name] ??
                            parameter.default,
                        );

                        return (
                          <div
                            key={parameter.name}
                            style={workflowFieldStyle}
                          >
                            <label
                              htmlFor={`workflow-step-parameter-${parameter.name}`}
                              style={workflowFieldLabelStyle}
                            >
                              {parameter.name}
                              {parameter.required ? ' *' : ''}
                            </label>
                            {parameter.enumValues.length > 0 ? (
                              <Select
                                allowClear={!parameter.required}
                                aria-label={`Parameter ${parameter.name}`}
                                id={`workflow-step-parameter-${parameter.name}`}
                                options={parameter.enumValues.map((value) => ({
                                  label: value,
                                  value,
                                }))}
                                placeholder={parameter.default || 'Select value'}
                                value={currentValue || undefined}
                                onChange={(value) =>
                                  updateStepDraft((current) =>
                                    current
                                      ? updateStepDraftParameterValue(
                                          current,
                                          parameter.name,
                                          parameter.type,
                                          String(value || ''),
                                        )
                                      : current,
                                  )
                                }
                              />
                            ) : (
                              <Input
                                aria-label={`Parameter ${parameter.name}`}
                                id={`workflow-step-parameter-${parameter.name}`}
                                placeholder={parameter.default || parameter.type || 'Value'}
                                value={currentValue}
                                onChange={(event) =>
                                  updateStepDraft((current) =>
                                    current
                                      ? updateStepDraftParameterValue(
                                          current,
                                          parameter.name,
                                          parameter.type,
                                          event.target.value,
                                        )
                                      : current,
                                  )
                                }
                              />
                            )}
                            <div style={workflowInlineMetaStyle}>
                              {parameter.description || `Type: ${parameter.type}`}
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  ) : (
                    <div style={workflowInlineMetaStyle}>
                      当前 step type 没有声明可引导参数，直接使用下面的 raw JSON 编辑。
                    </div>
                  )}
                  <details style={workflowAdvancedSectionStyle}>
                    <summary style={{ ...workflowSectionHeadingStyle, cursor: 'pointer' }}>
                      Raw parameters JSON
                    </summary>
                    <div style={{ display: 'grid', gap: 8, marginTop: 12 }}>
                      <label htmlFor="workflow-step-parameters" style={workflowFieldLabelStyle}>
                        Parameters JSON
                      </label>
                      <Input.TextArea
                        id="workflow-step-parameters"
                        aria-label="Step parameters"
                        autoSize={{ minRows: 8, maxRows: 14 }}
                        value={stepDraft.parametersText}
                        onChange={(event) =>
                          updateStepDraft((current) =>
                            current
                              ? {
                                  ...current,
                                  parametersText: event.target.value,
                                }
                              : current,
                          )
                        }
                      />
                    </div>
                  </details>
                </div>
                <div style={{ ...workflowFieldStyle, gridColumn: '1 / -1' }}>
                  <details style={workflowAdvancedSectionStyle}>
                    <summary style={{ ...workflowSectionHeadingStyle, cursor: 'pointer' }}>
                      Advanced routing JSON
                    </summary>
                    <div style={{ display: 'grid', gap: 8, marginTop: 12 }}>
                      <label htmlFor="workflow-step-branches" style={workflowFieldLabelStyle}>
                        Branches JSON
                      </label>
                      <Input.TextArea
                        id="workflow-step-branches"
                        aria-label="Step branches"
                        autoSize={{ minRows: 5, maxRows: 10 }}
                        value={stepDraft.branchesText}
                        onChange={(event) =>
                          updateStepDraft((current) =>
                            current
                              ? {
                                  ...current,
                                  branchesText: event.target.value,
                                }
                              : current,
                          )
                        }
                      />
                    </div>
                  </details>
                </div>
              </div>
              <div style={workflowStageActionsRowStyle}>
                <Button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  danger
                  disabled={!selectedStepId || Boolean(stepMutationPending)}
                  loading={stepMutationPending === 'remove'}
                  onClick={() => void handleRemoveStep()}
                >
                  Delete step
                </Button>
                <Button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  disabled={!selectedStepId || !stepDraft || Boolean(stepMutationPending)}
                  loading={stepMutationPending === 'apply'}
                  type="primary"
                  onClick={() => void handleApplyStepChanges()}
                >
                  Apply changes
                </Button>
              </div>
            </>
          ) : (
            <Empty description="Select a step from the DAG canvas first." />
          )}
          </div>
        </section>
      </div>

      <section data-testid="workflow-dry-run-panel" style={workflowDryRunSectionStyle}>
        <div style={{ alignItems: 'center', display: 'flex', gap: 8, justifyContent: 'space-between' }}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>Dry-run</div>
            <Typography.Text strong>Workflow draft run</Typography.Text>
          </div>
          <span style={{ ...statusTagStyle, background: '#f6ffed', color: '#237804' }}>
            seeded fixture
          </span>
        </div>
        <div style={{ display: 'grid', gap: 8 }}>
          <div style={workflowInlineMetaStyle}>
            Route: {dryRunRouteLabel || 'Config default'}
          </div>
          <div style={workflowInlineMetaStyle}>
            Model: {dryRunModelLabel || 'Use configured default'}
          </div>
        </div>
        {dryRunBlockedReason ? (
          <Alert
            action={
              onOpenRunSetup ? (
                <Button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  size="small"
                  type="link"
                  onClick={onOpenRunSetup}
                >
                  Connect provider
                </Button>
              ) : undefined
            }
            message={dryRunBlockedReason}
            showIcon
            type="warning"
          />
        ) : null}
        <Input.TextArea
          aria-label="Workflow dry run input"
          autoSize={{ minRows: 4, maxRows: 6 }}
          placeholder="Describe the input you want this workflow member to handle."
          value={runPrompt}
          onChange={(event) => onRunPromptChange(event.target.value)}
        />
        <Space wrap size={[8, 8]}>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            icon={<PlayCircleOutlined />}
            loading={runState.status === 'running'}
            type="primary"
            disabled={Boolean(dryRunBlockedReason?.trim()) || runState.status === 'running'}
            onClick={() => void handleRun()}
          >
            Run
          </Button>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            disabled={runState.status === 'running'}
            onClick={() =>
              onRunPromptChange(
                JSON.stringify(
                  {
                    channel: 'telegram',
                    text: 'refund for order #92817 — 3rd time asking',
                    user: 'alex',
                  },
                  null,
                  2,
                )
              )
            }
          >
            Load fixture
          </Button>
        </Space>
        {workflowRunError ? (
          <Alert message={workflowRunError} showIcon type="error" />
        ) : null}
        <div>
          <div style={sectionEyebrowStyle}>Output</div>
          <pre style={workflowDryRunOutputStyle}>{renderRunOutput(runState)}</pre>
        </div>
        {renderRunSummary(runState) ? (
          <details style={dryRunDebugDetailsStyle}>
            <summary style={dryRunDebugSummaryStyle}>Debug details</summary>
            <pre style={{ ...dryRunSummaryStyle, marginTop: 10 }}>{renderRunSummary(runState)}</pre>
          </details>
        ) : null}
      </section>
    </div>
  );
};

export type StudioScriptBuildPanelProps = {
  readonly scopeId?: string;
  readonly scriptsQuery: {
    readonly isLoading: boolean;
    readonly isError: boolean;
    readonly error: unknown;
    readonly data: readonly ScopedScriptDetail[] | undefined;
  };
  readonly selectedScriptId: string;
  readonly onSelectScriptId: (scriptId: string) => void;
  readonly onCreateScriptDraft?: () => void;
  readonly onRefreshScripts?: () => Promise<unknown> | unknown;
  readonly onContinueToBind: () => void;
  readonly onRegisterLeaveGuard?: (guard: (() => Promise<boolean>) | null) => void;
  readonly onScriptBuildStateChange?: (state: StudioScriptBuildState | null) => void;
  readonly pendingScriptDraft?: StudioPendingScriptDraft | null;
  readonly onPendingScriptDraftChange?: (draft: StudioPendingScriptDraft | null) => void;
  readonly onScriptDraftSaved?: (scriptId: string) => void;
};

export type StudioScriptBuildState = {
  readonly scriptId: string;
  readonly displayName: string;
  readonly scriptRevision: string;
  readonly revisionId?: string;
  readonly sourceHash?: string;
  readonly definitionActorId?: string;
  readonly dirty: boolean;
  readonly validationStatus: 'unknown' | 'valid' | 'invalid';
  readonly saveStatus: 'idle' | 'accepted' | 'applied' | 'failed';
};

export type StudioPendingScriptDraft = {
  readonly scriptId: string;
  readonly displayName: string;
  readonly sourceText?: string;
};

function buildPendingScriptDetail(
  draft: StudioPendingScriptDraft | null | undefined,
  scopeId?: string,
): ScopedScriptDetail | null {
  if (!draft?.scriptId) {
    return null;
  }

  return {
    available: true,
    scopeId: scopeId || '',
    script: {
      scopeId: scopeId || '',
      scriptId: draft.scriptId,
      catalogActorId: '',
      definitionActorId: '',
      activeRevision: '',
      activeSourceHash: '',
      updatedAt: '',
    },
    source: {
      sourceText: draft.sourceText || '',
      definitionActorId: '',
      revision: '',
      sourceHash: '',
    },
  };
}

function buildAppliedScriptDetail(
  scopeId: string,
  acceptedScript: ScopeScriptAcceptedSummary,
  sourceText: string,
  currentScript?: ScopedScriptSummary | null,
): ScopedScriptDetail {
  const summary: ScopedScriptSummary = currentScript || {
    scopeId,
    scriptId: acceptedScript.scriptId,
    catalogActorId: acceptedScript.catalogActorId || '',
    definitionActorId: acceptedScript.definitionActorId,
    activeRevision: acceptedScript.revisionId,
    activeSourceHash: acceptedScript.sourceHash,
    updatedAt: acceptedScript.acceptedAt,
  };

  return {
    available: true,
    scopeId,
    script: summary,
    source: {
      sourceText,
      definitionActorId: summary.definitionActorId || acceptedScript.definitionActorId,
      revision: summary.activeRevision || acceptedScript.revisionId,
      sourceHash: summary.activeSourceHash || acceptedScript.sourceHash,
    },
  };
}

export const StudioScriptBuildPanel: React.FC<StudioScriptBuildPanelProps> = ({
  scopeId,
  scriptsQuery,
  selectedScriptId,
  onSelectScriptId,
  onCreateScriptDraft,
  onRefreshScripts,
  onContinueToBind,
  onRegisterLeaveGuard,
  onScriptBuildStateChange,
  pendingScriptDraft,
  onPendingScriptDraftChange,
  onScriptDraftSaved,
}) => {
  const [scriptPackage, setScriptPackage] = React.useState(() =>
    deserializePersistedSource(''),
  );
  const [selectedFilePath, setSelectedFilePath] = React.useState('Behavior.cs');
  const [validationPending, setValidationPending] = React.useState(false);
  const [validationResult, setValidationResult] =
    React.useState<ScriptValidationResult | null>(null);
  const [validationError, setValidationError] = React.useState('');
  const [savePending, setSavePending] = React.useState(false);
  const [saveNotice, setSaveNotice] = React.useState('');
  const [saveObservationStatus, setSaveObservationStatus] = React.useState<
    'idle' | 'accepted' | 'applied' | 'pending' | 'rejected' | 'failed'
  >('idle');
  const [saveStatus, setSaveStatus] =
    React.useState<StudioScriptBuildState['saveStatus']>('idle');
  const [observedAppliedScript, setObservedAppliedScript] =
    React.useState<ScopedScriptDetail | null>(null);
  const [focusedDiagnostic, setFocusedDiagnostic] =
    React.useState<ScriptEditorFocusTarget | null>(null);
  const [promotionReason, setPromotionReason] = React.useState('');
  const [promotionPending, setPromotionPending] = React.useState(false);
  const [promotionNotice, setPromotionNotice] = React.useState('');
  const [promotionHistory, setPromotionHistory] = React.useState<
    ScriptPromotionDecision[]
  >([]);
  const [runPending, setRunPending] = React.useState(false);
  const [runInput, setRunInput] = React.useState(
    JSON.stringify(
      {
        channel: 'telegram',
        text: 'refund for order #92817 — 3rd time asking',
        user: 'alex',
      },
      null,
      2,
    ),
  );
  const [runOutput, setRunOutput] = React.useState(
    'Run the current script draft to inspect the draft-run result here.',
  );
  const [lastRunResult, setLastRunResult] = React.useState<DraftRunResult | null>(null);
  const [leaveDialogOpen, setLeaveDialogOpen] = React.useState(false);
  const leaveResolverRef = React.useRef<((value: boolean) => void) | null>(null);
  const saveObservationTimerRef = React.useRef<number | null>(null);
  const saveObservationTokenRef = React.useRef(0);
  const availableScripts = React.useMemo(
    () =>
      (scriptsQuery.data ?? []).filter(
        (detail): detail is ScopedScriptDetail => Boolean(detail.available && detail.script),
      ),
    [scriptsQuery.data],
  );
  const pendingScriptDetail = React.useMemo(
    () =>
      pendingScriptDraft?.scriptId === selectedScriptId
        ? buildPendingScriptDetail(pendingScriptDraft, scopeId)
        : null,
    [pendingScriptDraft, scopeId, selectedScriptId],
  );
  const selectedCatalogScript = React.useMemo(
    () => availableScripts.find((detail) => detail.script?.scriptId === selectedScriptId) || null,
    [availableScripts, selectedScriptId],
  );
  const selectedObservedAppliedScript = React.useMemo(
    () =>
      !selectedCatalogScript &&
      observedAppliedScript?.script?.scriptId === selectedScriptId
        ? observedAppliedScript
        : null,
    [observedAppliedScript, selectedCatalogScript, selectedScriptId],
  );
  const activeScript =
    selectedCatalogScript ||
    selectedObservedAppliedScript ||
    pendingScriptDetail ||
    availableScripts[0] ||
    null;
  const activeScriptIsDraft = Boolean(pendingScriptDetail && activeScript === pendingScriptDetail);
  const activeScriptIsObserved = Boolean(
    selectedObservedAppliedScript && activeScript === selectedObservedAppliedScript,
  );
  const persistedSource = React.useMemo(
    () => activeScript?.source?.sourceText || '',
    [activeScript?.source?.sourceText],
  );
  const currentRevision = React.useMemo(
    () =>
      activeScript?.source?.revision ||
      activeScript?.script?.activeRevision ||
      'draft-1',
    [activeScript?.script?.activeRevision, activeScript?.source?.revision],
  );
  const selectedPackageEntry = React.useMemo(
    () => getSelectedPackageEntry(scriptPackage, selectedFilePath),
    [scriptPackage, selectedFilePath],
  );
  const editorMarkers = React.useMemo(
    () => mapScriptMarkers(validationResult?.diagnostics, selectedPackageEntry?.path || ''),
    [selectedPackageEntry?.path, validationResult?.diagnostics],
  );
  const packageEntries = React.useMemo(
    () => getPackageEntries(scriptPackage),
    [scriptPackage],
  );
  const isDirty = React.useMemo(
    () => serializePersistedSource(scriptPackage) !== persistedSource,
    [persistedSource, scriptPackage],
  );

  React.useEffect(() => {
    if (!activeScript) {
      return;
    }

    const nextPackage = activeScriptIsDraft && !activeScript.source?.sourceText
      ? createScriptStarterPackage()
      : deserializePersistedSource(activeScript.source?.sourceText || '');
    const nextEntry =
      getSelectedPackageEntry(nextPackage, nextPackage.entrySourcePath) ||
      getSelectedPackageEntry(nextPackage, '') ||
      null;
    setScriptPackage(nextPackage);
    setSelectedFilePath(nextEntry?.path || nextPackage.entrySourcePath || 'Behavior.cs');
    setValidationResult(null);
    setValidationError('');
    setFocusedDiagnostic(null);
    if (activeScriptIsObserved) {
      setSaveObservationStatus('applied');
      setSaveStatus('applied');
    } else {
      setSaveNotice('');
      setSaveObservationStatus('idle');
      setSaveStatus('idle');
    }
    setLastRunResult(null);
  }, [
    activeScript?.script?.scriptId,
    activeScript?.source?.sourceText,
    activeScriptIsDraft,
    activeScriptIsObserved,
  ]);

  React.useEffect(() => {
    if (selectedScriptId || pendingScriptDraft?.scriptId || !availableScripts[0]?.script?.scriptId) {
      return;
    }

    onSelectScriptId(availableScripts[0].script!.scriptId);
  }, [availableScripts, onSelectScriptId, pendingScriptDraft?.scriptId, selectedScriptId]);

  const cancelSaveObservationPoll = React.useCallback(() => {
    saveObservationTokenRef.current += 1;
    if (saveObservationTimerRef.current) {
      window.clearTimeout(saveObservationTimerRef.current);
      saveObservationTimerRef.current = null;
    }
  }, []);

  React.useEffect(
    () => () => {
      cancelSaveObservationPoll();
    },
    [cancelSaveObservationPoll],
  );

  React.useEffect(() => {
    cancelSaveObservationPoll();
  }, [activeScript?.script?.scriptId, cancelSaveObservationPoll]);

  React.useEffect(() => {
    onRegisterLeaveGuard?.(
      async () =>
        new Promise<boolean>((resolve) => {
          if (!isDirty) {
            resolve(true);
            return;
          }

          leaveResolverRef.current = resolve;
          setLeaveDialogOpen(true);
        }),
    );

    return () => {
      onRegisterLeaveGuard?.(null);
    };
  }, [isDirty, onRegisterLeaveGuard]);

  const validationStatus: StudioScriptBuildState['validationStatus'] =
    validationResult
      ? validationResult.success
        ? 'valid'
        : 'invalid'
      : validationError
        ? 'invalid'
        : 'unknown';
  const effectiveSaveStatus: StudioScriptBuildState['saveStatus'] =
    isDirty
      ? saveStatus === 'failed'
        ? 'failed'
        : 'idle'
      : saveStatus === 'accepted' || saveStatus === 'failed'
        ? saveStatus
        : activeScript?.script?.activeRevision
          ? 'applied'
          : saveStatus;
  const scriptReadyToBind = Boolean(
    activeScript?.script?.scriptId &&
      !isDirty &&
      effectiveSaveStatus === 'applied',
  );
  const hasActiveScript = Boolean(activeScript?.script?.scriptId);
  const lifecycleStatus = !activeScript?.script?.scriptId
    ? 'No script'
    : activeScriptIsDraft
      ? 'Draft'
      : isDirty
        ? 'Unsaved edits'
        : saveObservationStatus === 'accepted' || effectiveSaveStatus === 'accepted'
          ? 'Save accepted'
          : saveObservationStatus === 'pending'
            ? 'Waiting for catalog'
            : effectiveSaveStatus === 'failed'
              ? 'Save needs attention'
              : scriptReadyToBind
                ? 'Catalog applied'
                : 'Catalog script';
  const lifecycleStatusColor =
    lifecycleStatus === 'Catalog applied'
      ? 'green'
      : lifecycleStatus === 'Save needs attention'
        ? 'red'
        : lifecycleStatus === 'Waiting for catalog' || lifecycleStatus === 'Save accepted'
          ? 'gold'
          : activeScriptIsDraft
            ? 'blue'
            : 'default';
  const bindReadinessLabel =
    !activeScript?.script?.scriptId
      ? 'Create or select a script'
      : validationStatus === 'invalid'
        ? 'Fix validation errors'
        : isDirty && validationStatus !== 'valid'
          ? 'Validate current source'
          : isDirty
            ? 'Save revision'
        : effectiveSaveStatus === 'accepted'
          ? 'Waiting for catalog'
          : scriptReadyToBind
            ? 'Ready to bind'
            : 'Save revision';
  const saveObservationInFlight =
    saveObservationStatus === 'accepted' || saveObservationStatus === 'pending';
  const saveDisabled = Boolean(
    !activeScript?.script?.scriptId ||
      validationPending ||
      saveObservationInFlight ||
      validationStatus === 'invalid' ||
      (isDirty && validationStatus !== 'valid'),
  );

  React.useEffect(() => {
    if (!activeScript?.script?.scriptId) {
      onScriptBuildStateChange?.(null);
      return;
    }

    onScriptBuildStateChange?.({
      scriptId: activeScript.script.scriptId,
      displayName: pendingScriptDraft?.displayName || activeScript.script.scriptId,
      scriptRevision:
        activeScript.source?.revision ||
        activeScript.script.activeRevision ||
        currentRevision,
      revisionId:
        activeScript.source?.revision ||
        activeScript.script.activeRevision ||
        currentRevision,
      sourceHash:
        activeScript.source?.sourceHash ||
        activeScript.script.activeSourceHash ||
        '',
      definitionActorId:
        activeScript.source?.definitionActorId ||
        activeScript.script.definitionActorId ||
        '',
      dirty: isDirty,
      validationStatus,
      saveStatus: effectiveSaveStatus,
    });
  }, [
    activeScript?.script?.activeRevision,
    activeScript?.script?.activeSourceHash,
    activeScript?.script?.definitionActorId,
    activeScript?.script?.scriptId,
    activeScript?.source?.definitionActorId,
    activeScript?.source?.revision,
    activeScript?.source?.sourceHash,
    currentRevision,
    effectiveSaveStatus,
    isDirty,
    onScriptBuildStateChange,
    pendingScriptDraft?.displayName,
    validationStatus,
  ]);

  const resolveLeave = React.useCallback((value: boolean) => {
    leaveResolverRef.current?.(value);
    leaveResolverRef.current = null;
    setLeaveDialogOpen(false);
  }, []);

  const commitScriptPackage = React.useCallback(
    (nextPackage: typeof scriptPackage, nextSelectedFilePath?: string) => {
      setScriptPackage(nextPackage);
      if (nextSelectedFilePath) {
        setSelectedFilePath(nextSelectedFilePath);
      }
      setValidationResult(null);
      setValidationError('');
      setFocusedDiagnostic(null);
      setSaveNotice('');
      setSaveObservationStatus('idle');
      setSaveStatus('idle');
      if (activeScriptIsDraft && pendingScriptDraft) {
        onPendingScriptDraftChange?.({
          ...pendingScriptDraft,
          sourceText: serializePersistedSource(nextPackage),
        });
      }
    },
    [activeScriptIsDraft, onPendingScriptDraftChange, pendingScriptDraft],
  );

  const handleValidate = React.useCallback(async () => {
    if (!activeScript?.script?.scriptId) {
      return;
    }

    setValidationPending(true);
    setValidationError('');
    try {
      const result = await scriptsApi.validateDraft({
        scriptId: activeScript.script.scriptId,
        scriptRevision: currentRevision,
        source: serializePersistedSource(scriptPackage),
        package: scriptPackage,
      });
      setValidationResult(result);
      setFocusedDiagnostic(null);
      if (result.success && isDirty) {
        setSaveStatus('idle');
      }
    } catch (error) {
      setValidationError(describeError(error));
      setValidationResult(null);
      setFocusedDiagnostic(null);
    } finally {
      setValidationPending(false);
    }
  }, [activeScript?.script?.scriptId, currentRevision, isDirty, scriptPackage]);

  const markSaveObservationApplied = React.useCallback(
    async (
      accepted: ScopeScriptUpsertAcceptedResponse,
      savedSourceText: string,
      currentScript: ScopedScriptSummary | null | undefined,
    ) => {
      setObservedAppliedScript(
        buildAppliedScriptDetail(
          scopeId || '',
          accepted.acceptedScript,
          savedSourceText,
          currentScript,
        ),
      );
      onScriptDraftSaved?.(accepted.acceptedScript.scriptId);
      await onRefreshScripts?.();
      setSaveObservationStatus('applied');
      setSaveStatus('applied');
      setSaveNotice(
        `Save applied for ${accepted.acceptedScript.scriptId} · revision ${accepted.acceptedScript.revisionId}.`,
      );
    },
    [onRefreshScripts, onScriptDraftSaved, scopeId],
  );

  const pollSaveObservation = React.useCallback(
    async (
      accepted: ScopeScriptUpsertAcceptedResponse,
      savedSourceText: string,
      attemptIndex: number,
      token: number,
    ) => {
      if (!scopeId) {
        return;
      }

      try {
        const observation = await scriptsApi.observeSaveScript(
          scopeId,
          accepted.acceptedScript.scriptId,
          {
            revisionId: accepted.acceptedScript.revisionId,
            definitionActorId: accepted.acceptedScript.definitionActorId,
            sourceHash: accepted.acceptedScript.sourceHash,
            proposalId: accepted.acceptedScript.proposalId,
            expectedBaseRevision: accepted.acceptedScript.expectedBaseRevision,
            acceptedAt: accepted.acceptedScript.acceptedAt,
          },
        );
        if (saveObservationTokenRef.current !== token) {
          return;
        }

        if (observation.status === 'applied') {
          saveObservationTimerRef.current = null;
          await markSaveObservationApplied(
            accepted,
            savedSourceText,
            observation.currentScript,
          );
          return;
        }

        if (observation.status === 'rejected') {
          saveObservationTimerRef.current = null;
          await onRefreshScripts?.();
          setSaveObservationStatus('rejected');
          setSaveStatus('failed');
          setSaveNotice(
            observation.message ||
              `Save rejected for ${accepted.acceptedScript.scriptId} · revision ${accepted.acceptedScript.revisionId}.`,
          );
          return;
        }

        setSaveObservationStatus('pending');
        const nextDelay = SCRIPT_SAVE_OBSERVATION_POLL_DELAYS_MS[attemptIndex];
        if (nextDelay == null) {
          saveObservationTimerRef.current = null;
          setSaveNotice(
            `Save accepted for ${accepted.acceptedScript.scriptId} · revision ${accepted.acceptedScript.revisionId}. Still waiting for catalog; use Refresh catalog to check again.`,
          );
          return;
        }

        setSaveNotice(
          `Save accepted for ${accepted.acceptedScript.scriptId} · revision ${accepted.acceptedScript.revisionId}. Waiting for catalog; checking again in ${Math.round(nextDelay / 1000)}s.`,
        );
        saveObservationTimerRef.current = window.setTimeout(() => {
          void pollSaveObservation(
            accepted,
            savedSourceText,
            attemptIndex + 1,
            token,
          );
        }, nextDelay);
      } catch (error) {
        if (saveObservationTokenRef.current !== token) {
          return;
        }
        saveObservationTimerRef.current = null;
        setSaveObservationStatus('failed');
        setSaveStatus('failed');
        setSaveNotice(describeError(error));
      }
    },
    [markSaveObservationApplied, onRefreshScripts, scopeId],
  );

  const handleSave = React.useCallback(async () => {
    if (!scopeId || !activeScript?.script?.scriptId) {
      setSaveNotice('Resolve the current scope and select a script before saving.');
      return;
    }

    cancelSaveObservationPoll();
    setSavePending(true);
    setSaveNotice('');
    try {
      const accepted = await scriptsApi.saveScript(scopeId, {
        scriptId: activeScript.script.scriptId,
        revisionId: currentRevision,
        expectedBaseRevision: activeScriptIsDraft
          ? undefined
          : activeScript.script.activeRevision || undefined,
        sourceText: serializePersistedSource(scriptPackage),
      });
      setSaveStatus('accepted');
      setSaveObservationStatus('accepted');
      const savedSourceText = serializePersistedSource(scriptPackage);
      const token = saveObservationTokenRef.current;
      await pollSaveObservation(accepted, savedSourceText, 0, token);
    } catch (error) {
      setSaveObservationStatus('failed');
      setSaveStatus('failed');
      setSaveNotice(describeError(error));
    } finally {
      setSavePending(false);
    }
  }, [
    activeScript?.script?.activeRevision,
    activeScript?.script?.scriptId,
    activeScriptIsDraft,
    cancelSaveObservationPoll,
    currentRevision,
    pollSaveObservation,
    scopeId,
    scriptPackage,
  ]);

  const handleAddPackageFile = React.useCallback(
    (kind: 'csharp' | 'proto') => {
      const fallbackPath = kind === 'csharp' ? 'Behavior.cs' : 'schema.proto';
      const nextPath = window.prompt(
        kind === 'csharp' ? 'C# source path' : 'Proto file path',
        fallbackPath,
      );
      if (!nextPath?.trim()) {
        return;
      }

      const nextPackage = addPackageFile(scriptPackage, kind, nextPath.trim());
      commitScriptPackage(nextPackage, nextPath.trim());
    },
    [commitScriptPackage, scriptPackage],
  );

  const handleRenamePackageFile = React.useCallback(() => {
    if (!selectedPackageEntry) {
      return;
    }

    const nextPath = window.prompt('Rename file', selectedPackageEntry.path);
    if (!nextPath?.trim() || nextPath.trim() === selectedPackageEntry.path) {
      return;
    }

    const nextPackage = renamePackageFile(
      scriptPackage,
      selectedPackageEntry.path,
      nextPath.trim(),
    );
    commitScriptPackage(nextPackage, nextPath.trim());
  }, [commitScriptPackage, scriptPackage, selectedPackageEntry]);

  const handleRemovePackageFile = React.useCallback(() => {
    if (!selectedPackageEntry || packageEntries.length <= 1) {
      return;
    }

    const nextPackage = removePackageFile(scriptPackage, selectedPackageEntry.path);
    const nextEntry =
      getSelectedPackageEntry(nextPackage, nextPackage.entrySourcePath) ||
      getSelectedPackageEntry(nextPackage, '');
    commitScriptPackage(nextPackage, nextEntry?.path);
  }, [commitScriptPackage, packageEntries.length, scriptPackage, selectedPackageEntry]);

  const handleSetEntrySource = React.useCallback(() => {
    if (!selectedPackageEntry || selectedPackageEntry.kind !== 'csharp') {
      return;
    }

    commitScriptPackage(
      setEntrySourcePath(scriptPackage, selectedPackageEntry.path),
      selectedPackageEntry.path,
    );
  }, [commitScriptPackage, scriptPackage, selectedPackageEntry]);

  const handlePromoteEvolution = React.useCallback(async () => {
    if (!scopeId || !activeScript?.script?.scriptId) {
      setPromotionNotice('Resolve the current scope and script before proposing evolution.');
      return;
    }

    setPromotionPending(true);
    setPromotionNotice('');
    try {
      const decision = await scriptsApi.proposeEvolution(
        scopeId,
        activeScript.script.scriptId,
        {
          baseRevision: activeScript.script.activeRevision || undefined,
          candidateRevision: currentRevision,
          candidateSource: serializePersistedSource(scriptPackage),
          reason: promotionReason.trim() || undefined,
        },
      );
      setPromotionHistory((current) => [decision, ...current].slice(0, 6));
      setPromotionNotice(
        decision.accepted
          ? `Promotion accepted: ${decision.candidateRevision || decision.proposalId}.`
          : decision.failureReason || `Promotion ${decision.status || 'not accepted'}.`,
      );
    } catch (error) {
      setPromotionNotice(describeError(error));
    } finally {
      setPromotionPending(false);
    }
  }, [
    activeScript?.script?.activeRevision,
    activeScript?.script?.scriptId,
    currentRevision,
    promotionReason,
    scopeId,
    scriptPackage,
  ]);

  const handleRun = React.useCallback(async () => {
    if (!scopeId || !activeScript?.script?.scriptId) {
      setRunOutput('Resolve the current scope and select a script before running.');
      return;
    }

    setRunPending(true);
    try {
      const result = await scriptsApi.runDraftScript({
        scopeId,
        scriptId: activeScript.script.scriptId,
        scriptRevision: currentRevision,
        source: serializePersistedSource(scriptPackage),
        input: runInput,
        definitionActorId:
          activeScript.source?.definitionActorId ||
          activeScript.script.definitionActorId ||
          undefined,
        package: scriptPackage,
      });
      setLastRunResult(result);
      setRunOutput(JSON.stringify(result, null, 2));
    } catch (error) {
      setLastRunResult(null);
      setRunOutput(describeError(error));
    } finally {
      setRunPending(false);
    }
  }, [
    activeScript?.script?.definitionActorId,
    activeScript?.script?.scriptId,
    activeScript?.source?.definitionActorId,
    currentRevision,
    runInput,
    scopeId,
    scriptPackage,
  ]);

  if (scriptsQuery.isLoading) {
    return (
      <div data-testid="studio-script-build-panel" style={buildSurfaceCardStyle}>
        <Typography.Text type="secondary">
          Loading scope scripts...
        </Typography.Text>
      </div>
    );
  }

  if (scriptsQuery.isError) {
    return (
      <div data-testid="studio-script-build-panel" style={buildSurfaceCardStyle}>
        <Alert
          message={describeError(scriptsQuery.error)}
          showIcon
          type="error"
        />
      </div>
    );
  }

  return (
    <div data-testid="studio-script-build-panel" style={buildWorkbenchGridStyle}>
      <div style={{ display: 'grid', gap: 16, minWidth: 0 }}>
        <section style={buildSurfaceCardStyle}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>Script Source</div>
            <div style={sectionDescriptionStyle}>
              Script mode 只做一件事：围绕当前 script draft 的 typed source、lints 和 dry-run 迭代实现。
            </div>
          </div>
          <div style={{ alignItems: 'center', display: 'flex', gap: 8, justifyContent: 'space-between' }}>
            <Space wrap size={[8, 8]}>
              {hasActiveScript ? (
                <>
                  <Tag color={lifecycleStatusColor}>{lifecycleStatus}</Tag>
                  <Tag color={scriptReadyToBind ? 'green' : 'default'}>
                    {bindReadinessLabel}
                  </Tag>
                </>
              ) : null}
              <Select
                aria-label="Script ID"
                style={{ minWidth: 220 }}
                placeholder="Create or select a script"
                value={activeScript?.script?.scriptId || undefined}
                onChange={onSelectScriptId}
                options={[
                  ...(pendingScriptDraft?.scriptId
                    ? [
                        {
                          label: `${pendingScriptDraft.scriptId} (draft)`,
                          value: pendingScriptDraft.scriptId,
                        },
                      ]
                    : []),
                  ...(observedAppliedScript?.script?.scriptId &&
                  !pendingScriptDraft?.scriptId &&
                  !availableScripts.some(
                    (detail) =>
                      detail.script?.scriptId === observedAppliedScript.script?.scriptId,
                  )
                    ? [
                        {
                          label: `${observedAppliedScript.script.scriptId} (applied)`,
                          value: observedAppliedScript.script.scriptId,
                        },
                      ]
                    : []),
                  ...availableScripts.map((detail) => ({
                    label: detail.script?.scriptId || 'script',
                    value: detail.script?.scriptId || '',
                  })),
                ]}
              />
            </Space>
            <Space wrap size={[8, 8]}>
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={!activeScript?.script?.scriptId || validationPending}
                loading={validationPending}
                onClick={() => void handleValidate()}
              >
                Validate
              </Button>
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={saveDisabled}
                icon={<CheckCircleOutlined />}
                loading={savePending}
                onClick={() => void handleSave()}
              >
                Save revision
              </Button>
            </Space>
          </div>
          {saveNotice ? (
            <Alert
              message={saveNotice}
              showIcon
              type={
                saveObservationStatus === 'applied'
                  ? 'success'
                  : saveObservationStatus === 'rejected' || saveObservationStatus === 'failed'
                    ? 'error'
                    : 'warning'
              }
              action={
                saveObservationStatus === 'pending' ? (
                  <Button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    size="small"
                    onClick={() => void onRefreshScripts?.()}
                  >
                    Refresh catalog
                  </Button>
                ) : undefined
              }
            />
          ) : null}
          {hasActiveScript ? (
            <div
              aria-label="Script lifecycle status"
              style={{
                alignItems: 'center',
                color: '#667085',
                display: 'flex',
                flexWrap: 'wrap',
                fontSize: 12,
                gap: 8,
                lineHeight: '18px',
              }}
            >
              <Typography.Text type="secondary">
                {activeScript?.script?.scriptId || '-'} · {lifecycleStatus} · validation{' '}
                {validationStatus} · save {saveObservationStatus} · rev {currentRevision}
              </Typography.Text>
            </div>
          ) : null}
          {validationError ? (
            <Alert message={validationError} showIcon type="error" />
          ) : null}
          {hasActiveScript && selectedPackageEntry ? (
            <div style={{ display: 'grid', gap: 12 }}>
              <details
                aria-label="Script package tree"
                style={{
                  border: '1px solid #efe7da',
                  borderRadius: 16,
                  padding: 12,
                }}
              >
                <summary
                  style={{
                    alignItems: 'center',
                    cursor: 'pointer',
                    display: 'flex',
                    flexWrap: 'wrap',
                    gap: 8,
                    justifyContent: 'space-between',
                    listStyle: 'none',
                  }}
                >
                  <span style={sectionEyebrowStyle}>Advanced package</span>
                  <Typography.Text type="secondary">
                    {packageEntries.length} file{packageEntries.length === 1 ? '' : 's'} ·{' '}
                    {scriptPackage.entrySourcePath || 'no entry'} entry
                  </Typography.Text>
                </summary>
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    flexWrap: 'wrap',
                    gap: 8,
                    justifyContent: 'space-between',
                    marginTop: 12,
                  }}
                >
                  <div>
                    <div style={sectionEyebrowStyle}>Package</div>
                    <Typography.Text type="secondary">
                      Entry: {scriptPackage.entrySourcePath || '-'} · Behavior:{' '}
                      {scriptPackage.entryBehaviorTypeName || '-'}
                    </Typography.Text>
                  </div>
                  <Space wrap size={[8, 8]}>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      size="small"
                      onClick={() => handleAddPackageFile('csharp')}
                    >
                      Add C#
                    </Button>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      size="small"
                      onClick={() => handleAddPackageFile('proto')}
                    >
                      Add proto
                    </Button>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      size="small"
                      onClick={handleRenamePackageFile}
                    >
                      Rename
                    </Button>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      disabled={packageEntries.length <= 1}
                      size="small"
                      onClick={handleRemovePackageFile}
                    >
                      Remove
                    </Button>
                  </Space>
                </div>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                  {packageEntries.map((entry) => (
                    <button
                      key={entry.path}
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      type="button"
                      onClick={() => setSelectedFilePath(entry.path)}
                      style={{
                        background:
                          entry.path === selectedPackageEntry.path ? '#111827' : '#fffdf8',
                        border: '1px solid #efe7da',
                        borderRadius: 999,
                        color:
                          entry.path === selectedPackageEntry.path ? '#fffdf8' : '#374151',
                        cursor: 'pointer',
                        fontSize: 12,
                        fontWeight: 700,
                        padding: '6px 10px',
                      }}
                    >
                      {entry.kind === 'csharp' ? 'C#' : 'proto'} · {entry.path}
                      {entry.path === scriptPackage.entrySourcePath ? ' · entry' : ''}
                    </button>
                  ))}
                </div>
                <div style={{ display: 'grid', gap: 10, gridTemplateColumns: 'minmax(0, 1fr) auto' }}>
                  <Input
                    aria-label="Entry behavior type"
                    placeholder="Entry behavior type, for example DraftBehavior"
                    value={scriptPackage.entryBehaviorTypeName}
                    onChange={(event) =>
                      commitScriptPackage(
                        updateEntryBehaviorTypeName(scriptPackage, event.target.value),
                        selectedPackageEntry.path,
                      )
                    }
                  />
                  <Button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={selectedPackageEntry.kind !== 'csharp'}
                    onClick={handleSetEntrySource}
                  >
                    Set entry source
                  </Button>
                </div>
              </details>
              <div style={{ minHeight: 520 }}>
                <ScriptCodeEditor
                  filePath={selectedPackageEntry.path}
                  language={selectedPackageEntry.kind === 'csharp' ? 'csharp' : 'plaintext'}
                  markers={editorMarkers}
                  value={selectedPackageEntry.content}
                  onChange={(value) => {
                    commitScriptPackage(
                      updatePackageFileContent(
                        scriptPackage,
                        selectedPackageEntry.path,
                        value,
                      ),
                      selectedPackageEntry.path,
                    );
                  }}
                  focusTarget={focusedDiagnostic}
                />
              </div>
              <div
                style={{
                  alignItems: 'center',
                  background: '#faf8f3',
                  border: '1px solid #efe7da',
                  borderRadius: 16,
                  display: 'flex',
                  gap: 12,
                  justifyContent: 'space-between',
                  padding: '12px 14px',
                }}
              >
                <div>
                  <div style={sectionEyebrowStyle}>Compiler</div>
                  <Typography.Text type="secondary">
                    {validationResult
                      ? validationResult.success
                        ? 'Validation completed without blocking errors.'
                        : 'Validation returned blocking diagnostics.'
                      : 'Run Validate to refresh compiler diagnostics.'}
                  </Typography.Text>
                </div>
                <Space wrap size={[8, 8]}>
                  {validationResult?.diagnostics?.length ? (
                    <Tag color={validationResult.success ? 'blue' : 'red'}>
                      Problems {validationResult.diagnostics.length}
                    </Tag>
                  ) : (
                    <Tag color="green">Clean</Tag>
                  )}
                </Space>
              </div>
              {validationResult?.diagnostics?.length ? (
                <div
                  aria-label="Script validation diagnostics"
                  style={{
                    border: '1px solid #efe7da',
                    borderRadius: 16,
                    display: 'grid',
                    gap: 8,
                    padding: 12,
                  }}
                >
                  <div style={sectionEyebrowStyle}>Diagnostics</div>
                  {validationResult.diagnostics.map((diagnostic, index) => {
                    const diagnosticKey = `${diagnostic.filePath || 'source'}:${diagnostic.startLine || 0}:${diagnostic.startColumn || 0}:${diagnostic.code || index}`;
                    const severityColor =
                      diagnostic.severity === 'error'
                        ? '#b42318'
                        : diagnostic.severity === 'warning'
                          ? '#ad6800'
                          : '#2563eb';
                    return (
                      <button
                        key={diagnosticKey}
                        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                        type="button"
                        onClick={() => {
                          const focusTarget = buildScriptDiagnosticFocusTarget(
                            diagnostic,
                            `${diagnosticKey}:${Date.now()}`,
                          );
                          setSelectedFilePath(focusTarget.filePath);
                          setFocusedDiagnostic(focusTarget);
                        }}
                        style={{
                          background: '#fffdf8',
                          border: '1px solid #efe7da',
                          borderRadius: 12,
                          color: '#1f2937',
                          cursor: 'pointer',
                          display: 'grid',
                          gap: 4,
                          padding: '10px 12px',
                          textAlign: 'left',
                        }}
                      >
                        <span style={{ alignItems: 'center', display: 'flex', gap: 8 }}>
                          <Tag color={diagnostic.severity === 'error' ? 'red' : diagnostic.severity === 'warning' ? 'gold' : 'blue'}>
                            {diagnostic.severity}
                          </Tag>
                          <span style={{ color: '#6b5d4a', fontSize: 12 }}>
                            {formatScriptDiagnosticLocation(diagnostic)}
                          </span>
                          {diagnostic.code ? (
                            <span style={{ color: severityColor, fontSize: 12 }}>
                              {diagnostic.code}
                            </span>
                          ) : null}
                        </span>
                        <span style={{ color: '#374151', fontSize: 13, lineHeight: '18px' }}>
                          {diagnostic.message}
                        </span>
                      </button>
                    );
                  })}
                </div>
              ) : null}
            </div>
          ) : (
            <Empty
              description="Create a Script draft or select a saved scope script to start editing."
            >
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                onClick={onCreateScriptDraft}
                type="primary"
              >
                Add script
              </Button>
            </Empty>
          )}
        </section>

        <div style={{ alignItems: 'center', display: 'flex', gap: 12, justifyContent: 'space-between' }}>
          <Typography.Text type="secondary">
            {scriptReadyToBind
              ? 'Script revision is catalog-applied. Continue to Bind to publish the callable member contract.'
              : `Script Build keeps code editing here. ${bindReadinessLabel}.`}
          </Typography.Text>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            disabled={!scriptReadyToBind}
            type="primary"
            onClick={onContinueToBind}
          >
            Continue to Bind
          </Button>
        </div>

        <ScriptLeaveDialog
          open={leaveDialogOpen}
          onStay={() => resolveLeave(false)}
          onLeave={() => resolveLeave(true)}
        />
      </div>

      <aside style={dryRunAsideStyle}>
        <div style={{ alignItems: 'center', display: 'flex', gap: 8, justifyContent: 'space-between' }}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>Dry-run</div>
            <Typography.Text strong>Script draft run</Typography.Text>
          </div>
          <span style={{ ...statusTagStyle, background: '#fffbe6', color: '#ad6800' }}>
            seeded fixture
          </span>
        </div>
        <div style={sectionDescriptionStyle}>
          Draft-run 会直接调用当前 source editor 里的脚本，不需要先把 scope 默认服务切到这个 script。
        </div>
        <Input.TextArea
          aria-label="Script dry run input"
          autoSize={{ minRows: 6, maxRows: 10 }}
          disabled={!hasActiveScript}
          value={runInput}
          onChange={(event) => setRunInput(event.target.value)}
        />
        <Space wrap size={[8, 8]}>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            icon={<PlayCircleOutlined />}
            disabled={!hasActiveScript}
            loading={runPending}
            type="primary"
            onClick={() => void handleRun()}
          >
            Run
          </Button>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={() =>
              setRunInput(
                JSON.stringify(
                  {
                    channel: 'telegram',
                    text: 'refund for order #92817 — 3rd time asking',
                    user: 'alex',
                  },
                  null,
                  2,
                ),
              )
            }
          >
            Load fixture
          </Button>
        </Space>
        <div>
          {lastRunResult ? (
            <div
              aria-label="Script dry run facts"
              style={{
                border: '1px solid #efe7da',
                borderRadius: 14,
                display: 'grid',
                gap: 6,
                marginBottom: 12,
                padding: 12,
              }}
            >
              <div style={sectionEyebrowStyle}>Run facts</div>
              {[
                ['Run', lastRunResult.runId],
                ['Runtime', lastRunResult.runtimeActorId],
                ['Definition', lastRunResult.definitionActorId],
                ['Source hash', lastRunResult.sourceHash],
                ['Read model', lastRunResult.readModelUrl],
              ].map(([label, value]) => (
                <div key={label} style={{ display: 'grid', gap: 2 }}>
                  <span style={{ color: '#8b7b63', fontSize: 11, fontWeight: 700 }}>
                    {label}
                  </span>
                  <Typography.Text copyable={Boolean(value)} ellipsis style={{ fontSize: 12 }}>
                    {value || '-'}
                  </Typography.Text>
                </div>
              ))}
            </div>
          ) : null}
          <div style={sectionEyebrowStyle}>Output</div>
          <pre style={dryRunOutputStyle}>{runOutput}</pre>
        </div>
        {hasActiveScript ? (
          <details
            aria-label="Script promotion history"
            style={{
              border: '1px solid #efe7da',
              borderRadius: 14,
              padding: 12,
            }}
          >
            <summary style={{ ...sectionEyebrowStyle, cursor: 'pointer' }}>
              Promotion
            </summary>
            <div style={{ display: 'grid', gap: 10, marginTop: 12 }}>
              <Input.TextArea
                aria-label="Promotion reason"
                autoSize={{ minRows: 2, maxRows: 4 }}
                placeholder="Why is this revision ready to promote?"
                value={promotionReason}
                onChange={(event) => setPromotionReason(event.target.value)}
              />
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={promotionPending}
                loading={promotionPending}
                onClick={() => void handlePromoteEvolution()}
              >
                Propose evolution
              </Button>
              {promotionNotice ? (
                <Alert
                  showIcon
                  message={promotionNotice}
                  type={promotionNotice.startsWith('Promotion accepted') ? 'success' : 'warning'}
                />
              ) : null}
              {promotionHistory.length > 0 ? (
                <div style={{ display: 'grid', gap: 8 }}>
                  {promotionHistory.map((decision) => (
                    <div
                      key={decision.proposalId || `${decision.scriptId}:${decision.candidateRevision}`}
                      style={{
                        background: '#fffdf8',
                        border: '1px solid #efe7da',
                        borderRadius: 12,
                        display: 'grid',
                        gap: 4,
                        padding: 10,
                      }}
                    >
                      <Typography.Text strong>
                        {decision.accepted ? 'Accepted' : decision.status || 'Decision'}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        {decision.scriptId} · {decision.baseRevision || '-'} →{' '}
                        {decision.candidateRevision || '-'}
                      </Typography.Text>
                      {decision.failureReason ? (
                        <Typography.Text type="danger">
                          {decision.failureReason}
                        </Typography.Text>
                      ) : null}
                    </div>
                  ))}
                </div>
              ) : (
                <Typography.Text type="secondary">
                  No promotion decisions in this session.
                </Typography.Text>
              )}
            </div>
          </details>
        ) : null}
      </aside>
    </div>
  );
};

export type StudioGAgentBuildPanelProps = {
  readonly scopeId?: string;
  readonly currentMemberLabel: string;
  readonly gAgentTypes: readonly RuntimeGAgentTypeDescriptor[];
  readonly gAgentTypesLoading: boolean;
  readonly gAgentTypesError: unknown;
  readonly selectedGAgentTypeName: string;
  readonly onSelectGAgentTypeName: (value: string) => void;
  readonly onContinueToBind: () => void;
};

export const StudioGAgentBuildPanel: React.FC<StudioGAgentBuildPanelProps> = ({
  scopeId,
  currentMemberLabel,
  gAgentTypes,
  gAgentTypesLoading,
  gAgentTypesError,
  selectedGAgentTypeName,
  onSelectGAgentTypeName,
  onContinueToBind,
}) => {
  const [displayName, setDisplayName] = React.useState(currentMemberLabel || 'Member GAgent');
  const [role, setRole] = React.useState('intake-classifier');
  const [initialPrompt, setInitialPrompt] = React.useState(
    'You are the team member gagent. Own long-lived state and answer through the selected tools.',
  );
  const [toolsDraft, setToolsDraft] = React.useState('classify_intent, detect_language');
  const [persistenceMode, setPersistenceMode] = React.useState<'grain' | 'ephemeral'>(
    'grain',
  );
  const [runPrompt, setRunPrompt] = React.useState(
    'Classify this refund request and keep the member state in context.',
  );
  const [runState, setRunState] = React.useState<DraftRunState>(IDLE_DRAFT_RUN_STATE);
  const abortControllerRef = React.useRef<AbortController | null>(null);
  const selectedType = React.useMemo(
    () =>
      gAgentTypes.find((descriptor) =>
        buildRuntimeGAgentAssemblyQualifiedName(descriptor) === selectedGAgentTypeName,
      ) || null,
    [gAgentTypes, selectedGAgentTypeName],
  );
  const selectedTypeName =
    selectedGAgentTypeName ||
    (gAgentTypes[0] ? buildRuntimeGAgentAssemblyQualifiedName(gAgentTypes[0]) : '');
  const toolTags = React.useMemo(
    () =>
      toolsDraft
        .split(',')
        .map((item) => item.trim())
        .filter(Boolean),
    [toolsDraft],
  );

  React.useEffect(() => {
    if (!selectedGAgentTypeName && selectedTypeName) {
      onSelectGAgentTypeName(selectedTypeName);
    }
  }, [onSelectGAgentTypeName, selectedGAgentTypeName, selectedTypeName]);

  React.useEffect(() => {
    setDisplayName((current) => current || currentMemberLabel || 'Member GAgent');
  }, [currentMemberLabel]);

  React.useEffect(
    () => () => {
      abortControllerRef.current?.abort();
    },
    [],
  );

  const handleRun = React.useCallback(async () => {
    if (!scopeId || !selectedTypeName.trim() || !runPrompt.trim()) {
      setRunState({
        ...IDLE_DRAFT_RUN_STATE,
        error: 'Scope, GAgent type, and prompt are required before running.',
        status: 'error',
      });
      return;
    }

    abortControllerRef.current?.abort();
    const controller = new AbortController();
    abortControllerRef.current = controller;
    setRunState({
      ...IDLE_DRAFT_RUN_STATE,
      status: 'running',
    });

    try {
      const response = await runtimeGAgentApi.streamDraftRun(
        scopeId,
        {
          actorTypeName: selectedTypeName,
          prompt: runPrompt,
        },
        controller.signal,
      );

      await consumeAguiDraftRun(response, controller.signal, setRunState);
      setRunState((current) =>
        current.status === 'error' || controller.signal.aborted
          ? current
          : {
              ...current,
              status: current.events.length > 0 ? 'success' : 'idle',
            },
      );
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        return;
      }

      setRunState({
        ...IDLE_DRAFT_RUN_STATE,
        error: describeError(error),
        status: 'error',
      });
    } finally {
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
      }
    }
  }, [runPrompt, scopeId, selectedTypeName]);

  return (
    <div data-testid="studio-gagent-build-panel" style={buildWorkbenchGridStyle}>
      <div style={{ display: 'grid', gap: 16, minWidth: 0 }}>
        <section style={buildSurfaceCardStyle}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>GAgent Definition</div>
            <div style={sectionDescriptionStyle}>
              GAgent mode 在 Build 里定义当前 member 的 actor 类型、展示名、角色、初始提示词、工具和状态持久化语义。
            </div>
          </div>
          <div style={{ alignItems: 'center', display: 'flex', gap: 8, justifyContent: 'space-between' }}>
            <Space wrap size={[8, 8]}>
              <Tag color="green">template · seeded</Tag>
              {selectedType ? (
                <Tag>{buildRuntimeGAgentTypeLabel(selectedType)}</Tag>
              ) : null}
            </Space>
          </div>
          {gAgentTypesError ? (
            <Alert message={describeError(gAgentTypesError)} showIcon type="error" />
          ) : null}
          <div
            style={{
              display: 'grid',
              gap: 16,
              gridTemplateColumns: '160px minmax(0, 1fr)',
            }}
          >
            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>Type URL</div>
            <Select
              aria-label="GAgent type"
              loading={gAgentTypesLoading}
              value={selectedTypeName || undefined}
              onChange={onSelectGAgentTypeName}
              options={gAgentTypes.map((descriptor) => ({
                label: buildRuntimeGAgentTypeLabel(descriptor),
                value: buildRuntimeGAgentAssemblyQualifiedName(descriptor),
              }))}
              placeholder="Select a typed GAgent"
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>Display name</div>
            <Input
              aria-label="GAgent display name"
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>Role</div>
            <Input
              aria-label="GAgent role"
              value={role}
              onChange={(event) => setRole(event.target.value)}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>Initial prompt</div>
            <Input.TextArea
              aria-label="GAgent initial prompt"
              autoSize={{ minRows: 4, maxRows: 8 }}
              value={initialPrompt}
              onChange={(event) => setInitialPrompt(event.target.value)}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>Tools</div>
            <div style={{ display: 'grid', gap: 10 }}>
              <Input
                aria-label="GAgent tools"
                value={toolsDraft}
                onChange={(event) => setToolsDraft(event.target.value)}
                placeholder="classify_intent, detect_language"
              />
              <Space wrap size={[8, 8]}>
                {toolTags.length > 0 ? (
                  toolTags.map((tool) => (
                    <Tag key={tool} color="blue">
                      {tool}
                    </Tag>
                  ))
                ) : (
                  <Tag>+ add tool</Tag>
                )}
              </Space>
            </div>

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>State persistence</div>
            <Radio.Group
              value={persistenceMode}
              onChange={(event) => setPersistenceMode(event.target.value)}
            >
              <Space direction="vertical">
                <Radio value="grain">Orleans grain</Radio>
                <Radio value="ephemeral">Ephemeral</Radio>
              </Space>
            </Radio.Group>
          </div>
        </section>

        <div style={{ alignItems: 'center', display: 'flex', gap: 12, justifyContent: 'space-between' }}>
          <Typography.Text type="secondary">
            GAgent Build 只负责定义 actor 语义；真正发布 service / endpoint 还是下一步去 Bind。
          </Typography.Text>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            type="primary"
            onClick={onContinueToBind}
          >
            Continue to Bind
          </Button>
        </div>
      </div>

      <aside style={dryRunAsideStyle}>
        <div style={{ alignItems: 'center', display: 'flex', gap: 8, justifyContent: 'space-between' }}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>Dry-run</div>
            <Typography.Text strong>GAgent draft run</Typography.Text>
          </div>
          <span style={{ ...statusTagStyle, background: '#f6ffed', color: '#237804' }}>
            seeded fixture
          </span>
        </div>
        <div style={sectionDescriptionStyle}>
          这里用当前选中的 GAgent 类型直接做一次草稿运行，验证 prompt 和 transcript 是否符合预期。
        </div>
        <Input.TextArea
          aria-label="GAgent dry run input"
          autoSize={{ minRows: 6, maxRows: 10 }}
          value={runPrompt}
          onChange={(event) => setRunPrompt(event.target.value)}
        />
        <Space wrap size={[8, 8]}>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            icon={<PlayCircleOutlined />}
            loading={runState.status === 'running'}
            type="primary"
            onClick={() => void handleRun()}
          >
            Run
          </Button>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={() =>
              setRunPrompt('Classify this support ticket, keep the member state, and decide whether to escalate.')
            }
          >
            Load fixture
          </Button>
        </Space>
        <div>
          <div style={sectionEyebrowStyle}>Output</div>
          <pre style={dryRunOutputStyle}>{renderRunOutput(runState)}</pre>
        </div>
        {renderRunSummary(runState) ? (
          <details style={dryRunDebugDetailsStyle}>
            <summary style={dryRunDebugSummaryStyle}>Debug details</summary>
            <pre style={{ ...dryRunSummaryStyle, marginTop: 10 }}>{renderRunSummary(runState)}</pre>
          </details>
        ) : null}
      </aside>
    </div>
  );
};

export type StudioBuildModeCard = {
  readonly key: 'workflow' | 'script' | 'gagent';
  readonly label: string;
  readonly description: string;
  readonly hint: string;
  readonly disabled?: boolean;
};

export function getDefaultBuildModeCards(scriptsEnabled: boolean): readonly StudioBuildModeCard[] {
  return [
    {
      key: 'workflow',
      label: 'Workflow',
      description:
        'Compose steps as a DAG. Best when the flow is known and parallel fan-out matters.',
      hint: 'When · Multiple agents hand off predictably',
    },
    {
      key: 'script',
      label: 'Script',
      description:
        'Write a typed script that handles deterministic business logic and code-level branches.',
      hint: scriptsEnabled
        ? 'When · You need code-level control'
        : '当前环境暂未启用脚本能力。',
      disabled: !scriptsEnabled,
    },
    {
      key: 'gagent',
      label: 'GAgent',
      description:
        'Wire a typed GAgent actor with long-lived state. Best when one member owns durable behavior.',
      hint: 'When · State lives with one agent',
    },
  ];
}
