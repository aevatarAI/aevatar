import { parseCustomEvent } from '@aevatar-react-sdk/agui';
import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
} from '@aevatar-react-sdk/types';
import { CheckCircleOutlined, PlayCircleOutlined } from '@ant-design/icons';
import type { Node } from '@xyflow/react';
import {
  Alert,
  Button,
  Empty,
  Input,
  Radio,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import ScriptCodeEditor, {
  type ScriptEditorFocusTarget,
  type ScriptEditorMarker,
} from '@/modules/studio/scripts/ScriptCodeEditor';
import { parseRunContextData } from '@/shared/agui/customEventData';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import { t } from '@/shared/i18n/messages';
import {
  buildRuntimeGAgentKindLabel,
  buildRuntimeGAgentKindValue,
  type RuntimeGAgentKindDescriptor,
} from '@/shared/models/runtime/gagents';
import type { WorkflowPrimitiveDescriptor } from '@/shared/models/runtime/query';
import {
  createStepInspectorDraft,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import type { StudioGraphStep } from '@/shared/studio/graph';
import {
  buildNodeConfigFields,
  findNodeConfigPrimitiveDescriptor,
  formatNodeConfigFieldCopy,
  updateNodeConfigFieldParametersText,
  validateNodeConfigParametersText,
} from '@/shared/studio/nodeConfigFields';
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
  updateEntryBehaviorTypeName,
  updatePackageFileContent,
} from '@/shared/studio/scriptPackage';
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
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import { describeError } from '@/shared/ui/errorText';
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
  joinInteractiveClassNames,
} from '@/shared/ui/interactionStandards';

const buildWorkbenchGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1fr) minmax(340px, 380px)',
  minHeight: 0,
  minWidth: 0,
};

const scriptLaunchpadGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1fr)',
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
  1000, 2000, 3000, 5000, 5000, 5000,
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

export const STUDIO_WORKFLOW_YAML_AUTHORING_DELETION_TRACKING = {
  issue: 'github-devloop/issue/aevatarAI/aevatar/2747',
  legacySurface:
    'apps/aevatar-console-web/src/pages/studio/components/StudioBuildPanels.tsx',
  canonicalSurfaces: [
    '/scopes/:scopeId/teams/:teamId/members/:memberId/workflow',
    '/scopes/:scopeId/teams/:teamId/members/new/workflow',
  ],
  removalCondition:
    'Delete this raw draft YAML editor after pages/studio workflow authoring is migrated to the canonical Team member workflow editor.',
} as const;

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
  letterSpacing: 0,
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
  borderRadius: 8,
  display: 'inline-flex',
  gap: 2,
  padding: 3,
};

const workflowViewSwitchButtonStyle: React.CSSProperties = {
  background: 'transparent',
  border: 0,
  borderRadius: 6,
  color: '#2b3038',
  cursor: 'pointer',
  fontSize: 13,
  fontWeight: 650,
  lineHeight: '20px',
  minHeight: 30,
  minWidth: 76,
  padding: '5px 14px',
};

const workflowViewSwitchButtonActiveStyle: React.CSSProperties = {
  ...workflowViewSwitchButtonStyle,
  background: '#1677ff',
  boxShadow: '0 1px 3px rgba(22, 119, 255, 0.22)',
  color: '#ffffff',
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
  letterSpacing: 0,
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

const GAGENT_DRAFT_RUN_TIMEOUT_MS = 30_000;
const GAGENT_DRAFT_RUN_CLIENT_TIMEOUT_MS = GAGENT_DRAFT_RUN_TIMEOUT_MS + 5_000;

function createGAgentDraftRunTimeoutError(): Error {
  return new Error(
    'GAgent draft run timed out before the backend returned any event.',
  );
}

function getRunDebugLines(state: DraftRunState): string[] {
  return [
    state.runId.trim()
      ? t(
          'pages.studio.studiobuildpanels.current.run.ready',
          'current run: ready',
        )
      : '',
    state.actorId.trim()
      ? t(
          'pages.studio.studiobuildpanels.runtime.actor.ready',
          'runtime actor: ready',
        )
      : '',
    state.commandId.trim()
      ? t(
          'pages.studio.studiobuildpanels.command.accepted',
          'command: accepted',
        )
      : '',
    state.events.length > 0
      ? t('pages.studio.studiobuildpanels.events.count', 'events: {count}', {
          count: state.events.length,
        })
      : '',
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
    return t(
      'pages.studio.studiobuildpanels.waiting.for.assistant.output',
      'Waiting for assistant output...',
    );
  }

  if (state.status === 'success' && getRunDebugLines(state).length > 0) {
    return t(
      'pages.studio.studiobuildpanels.run.completed.but.no',
      'Run completed, but no assistant output was returned.',
    );
  }

  return t(
    'pages.studio.studiobuildpanels.run.the.current.draft',
    'Run the current draft to inspect the assistant output here.',
  );
}

function renderRunSummary(state: DraftRunState): string {
  return getRunDebugLines(state).join('\n');
}

function getGAgentDraftRunRecoveryText(state: DraftRunState): string {
  if (state.status === 'running') {
    return t(
      'pages.studio.studiobuildpanels.draft.run.waiting.backend.events',
      'Draft run is still waiting for backend events. Keep the Build definition visible while this request completes.',
    );
  }

  if (state.status === 'error') {
    return t(
      'pages.studio.studiobuildpanels.build.dry.run.failed.recovery',
      'This only failed the Build dry-run. Adjust the prompt or tools and retry, or continue to Bind when the member definition is ready to publish.',
    );
  }

  if (state.status === 'success') {
    return t(
      'pages.studio.studiobuildpanels.draft.run.finished.continue.bind',
      'Draft run finished. Continue to Bind when you are ready to publish the callable member contract.',
    );
  }

  return '';
}

function extractRunFinishedOutput(result: unknown): string {
  if (typeof result === 'string') {
    return result;
  }

  if (!result || typeof result !== 'object' || Array.isArray(result)) {
    return '';
  }

  const record = result as Record<string, unknown>;
  const candidate =
    record.output ?? record.Output ?? record.message ?? record.text;
  return typeof candidate === 'string' ? candidate : '';
}

function areStepInspectorDraftsEqual(
  left: StudioStepInspectorDraft | null,
  right: StudioStepInspectorDraft | null,
): boolean {
  if (!left || !right) {
    return false;
  }

  return (
    left.id === right.id &&
    left.type === right.type &&
    left.targetRole === right.targetRole &&
    left.next === right.next &&
    left.parametersText === right.parametersText &&
    left.branchesText === right.branchesText
  );
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

function formatScriptDiagnosticLocation(
  diagnostic: ScriptValidationDiagnostic,
): string {
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
        {t(
          'pages.studio.studiobuildpanels.leave.script.build.2',
          'Leave Script Build?',
        )}
      </Typography.Text>
      <Typography.Text type="secondary">
        {t(
          'pages.studio.studiobuildpanels.build.source.editor',
          'The current script draft is not saved. Leaving Build will discard unsaved changes in the source editor.',
        )}
      </Typography.Text>
      <Space>
        <Button
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          onClick={props.onStay}
        >
          {t('pages.studio.studiobuildpanels.copy', 'Keep editing')}
        </Button>
        <Button
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          danger
          type="primary"
          onClick={props.onLeave}
        >
          {t('pages.studio.studiobuildpanels.copy.2', 'Leave page')}
        </Button>
      </Space>
    </div>
  );
}

export type StudioWorkflowBuildPanelProps = {
  readonly draftYaml: string;
  readonly onSetDraftYaml: (value: string) => void;
  readonly onSaveDraft: (
    draft?: {
      readonly stepId: string;
      readonly draft: StudioStepInspectorDraft;
    } | null,
  ) => void;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly saveNotice?: {
    readonly type: 'success' | 'info' | 'error';
    readonly message: string;
  } | null;
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
  readonly buildWorkflowYamls: (
    draft?: {
      readonly stepId: string;
      readonly draft: StudioStepInspectorDraft;
    } | null,
  ) => Promise<string[]>;
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
  readonly onNodeLayoutChange: (nodes: Node[]) => void;
  readonly onContinueToBind: () => void;
};

export const StudioWorkflowBuildPanel: React.FC<
  StudioWorkflowBuildPanelProps
> = ({
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
  const toast = useConsoleToast();
  const workflowActionFailure = t(
    'pages.studio.studiobuildpanels.workflowActionFailed',
    'Could not complete the workflow action. Review the details and try again.',
  );
  const panelRef = React.useRef<HTMLDivElement | null>(null);
  const [viewMode, setViewMode] = React.useState<'canvas' | 'yaml'>('canvas');
  const [runState, setRunState] =
    React.useState<DraftRunState>(IDLE_DRAFT_RUN_STATE);
  const [workflowRunError, setWorkflowRunError] = React.useState('');
  const [stepTypePickerOpen, setStepTypePickerOpen] = React.useState(false);
  const [stepDraft, setStepDraft] =
    React.useState<StudioStepInspectorDraft | null>(null);
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
      const nextDraft =
        typeof updater === 'function' ? updater(stepDraftRef.current) : updater;
      stepDraftRef.current = nextDraft;
      setStepDraft(nextDraft);
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
      selectedGraphNodeId || (selectedStep ? `step:${selectedStep.id}` : ''),
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
    () => (selectedStep ? createStepInspectorDraft(selectedStep) : null),
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
            if (
              primitive.name.trim().toLowerCase() ===
              stepType.trim().toLowerCase()
            ) {
              return true;
            }

            return primitive.aliases.some(
              (alias) =>
                alias.trim().toLowerCase() === stepType.trim().toLowerCase(),
            );
          }) ?? null;

        return {
          stepType,
          description:
            descriptor?.description?.trim() ||
            'Create a new workflow step of this type.',
        };
      }),
    [availableStepTypes, runtimePrimitives],
  );
  const selectedPrimitiveDescriptor = React.useMemo(
    () =>
      findNodeConfigPrimitiveDescriptor(
        runtimePrimitives,
        stepDraft?.type || selectedStep?.type || '',
      ),
    [runtimePrimitives, selectedStep?.type, stepDraft?.type],
  );
  const stepParameterConfig = React.useMemo(
    () =>
      stepDraft
        ? buildNodeConfigFields({
            nodeType: stepDraft.type,
            parametersText: stepDraft.parametersText,
            primitiveDescriptor: selectedPrimitiveDescriptor,
          })
        : null,
    [selectedPrimitiveDescriptor, stepDraft],
  );
  const stepParameterDraftError = React.useMemo(
    () =>
      stepDraft
        ? validateNodeConfigParametersText(stepDraft.parametersText)
        : '',
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

    const currentStepDraft = stepDraftRef.current;
    const currentParameterError = currentStepDraft
      ? validateNodeConfigParametersText(currentStepDraft.parametersText)
      : '';
    if (currentParameterError) {
      setStepMutationError(currentParameterError);
      toast.error(workflowActionFailure);
      return;
    }

    if (!scopeId) {
      const visibleMessage =
        'Resolve the current workspace before running the workflow draft.';
      setWorkflowRunError(visibleMessage);
      toast.error(workflowActionFailure);
      return;
    }

    if (dryRunBlockedReason?.trim()) {
      const visibleMessage = dryRunBlockedReason.trim();
      setWorkflowRunError(visibleMessage);
      toast.error(workflowActionFailure);
      return;
    }

    if (!runPrompt.trim()) {
      const visibleMessage =
        'Sample input is required before running the workflow draft.';
      setWorkflowRunError(visibleMessage);
      toast.error(workflowActionFailure);
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
          workflowYamls: await buildWorkflowYamls(
            currentStepDraft &&
              selectedStepDraftSeed &&
              !areStepInspectorDraftsEqual(
                currentStepDraft,
                selectedStepDraftSeed,
              )
              ? {
                  stepId: selectedStepId,
                  draft: currentStepDraft,
                }
              : null,
          ),
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
      const disconnectedProvider = rawMessage.match(
        /Provider '([^']+)' not connected/i,
      );
      const visibleMessage = disconnectedProvider
        ? t(
            'pages.studio.studiobuildpanels.dry.run.provider.provider',
            'Dry-run cannot run because the {value1} provider is not connected yet. Connect an available provider, then run the current workflow draft again.',
            { value1: disconnectedProvider[1] },
          )
        : rawMessage;
      setWorkflowRunError(visibleMessage);
      toast.error(workflowActionFailure);
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
    selectedStepDraftSeed,
    selectedStepId,
    toast,
    workflowActionFailure,
  ]);

  const handleInsertStep = React.useCallback(
    async (stepType: string) => {
      if (stepMutationPendingRef.current) {
        return;
      }

      stepMutationPendingRef.current = true;
      setStepMutationPending('add');
      setStepMutationError('');
      try {
        await onInsertStep(stepType);
        setStepTypePickerOpen(false);
      } catch {
        toast.error(workflowActionFailure);
      } finally {
        stepMutationPendingRef.current = false;
        setStepMutationPending('');
      }
    },
    [onInsertStep, toast, workflowActionFailure],
  );

  const handleApplyStepChanges = React.useCallback(async () => {
    const currentStepDraft = stepDraftRef.current;
    if (!currentStepDraft || stepMutationPendingRef.current) {
      return;
    }

    const currentParameterError = validateNodeConfigParametersText(
      currentStepDraft.parametersText,
    );
    if (currentParameterError) {
      setStepMutationError(currentParameterError);
      toast.error(workflowActionFailure);
      return;
    }

    stepMutationPendingRef.current = true;
    setStepMutationPending('apply');
    setStepMutationError('');
    try {
      await onApplyStepDraft(currentStepDraft);
    } catch {
      toast.error(workflowActionFailure);
    } finally {
      stepMutationPendingRef.current = false;
      setStepMutationPending('');
    }
  }, [onApplyStepDraft, toast, workflowActionFailure]);

  const handleSaveDraft = React.useCallback(() => {
    const currentStepDraft = stepDraftRef.current;
    const currentParameterError = currentStepDraft
      ? validateNodeConfigParametersText(currentStepDraft.parametersText)
      : '';
    if (currentParameterError) {
      setStepMutationError(currentParameterError);
      toast.error(workflowActionFailure);
      return;
    }

    onSaveDraft(
      currentStepDraft &&
        selectedStepDraftSeed &&
        !areStepInspectorDraftsEqual(currentStepDraft, selectedStepDraftSeed)
        ? {
            stepId: selectedStepId,
            draft: currentStepDraft,
          }
        : null,
    );
  }, [
    onSaveDraft,
    selectedStepDraftSeed,
    selectedStepId,
    toast,
    workflowActionFailure,
  ]);

  const handleRemoveStep = React.useCallback(async () => {
    if (stepMutationPendingRef.current) {
      return;
    }

    stepMutationPendingRef.current = true;
    setStepMutationPending('remove');
    setStepMutationError('');
    try {
      await onRemoveSelectedStep();
    } catch {
      toast.error(workflowActionFailure);
    } finally {
      stepMutationPendingRef.current = false;
      setStepMutationPending('');
    }
  }, [onRemoveSelectedStep, toast, workflowActionFailure]);

  const handleDeleteNodes = React.useCallback(
    async (nodeIds: string[]) => {
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
      } catch {
        toast.error(workflowActionFailure);
      } finally {
        stepMutationPendingRef.current = false;
        setStepMutationPending('');
      }
    },
    [onDeleteWorkflowNodes, toast, workflowActionFailure],
  );

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
      <div
        data-testid="workflow-stage-actions"
        style={workflowStageActionsStyle}
      >
        <div style={workflowStageActionsRowStyle}>
          <div style={{ alignItems: 'center', display: 'flex', gap: 8 }}>
            <div style={sectionEyebrowStyle}>
              {t(
                'pages.studio.studiobuildpanels.build.actions.2',
                'Build actions',
              )}
            </div>
            <Tag color={canSaveWorkflow ? 'gold' : 'default'}>
              {canSaveWorkflow
                ? t(
                    'pages.studio.studiobuildpanels.draft.ready.2',
                    'draft ready',
                  )
                : t('pages.studio.studiobuildpanels.saved', 'saved')}
            </Tag>
          </div>
          <Space wrap size={[8, 8]}>
            <Button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              disabled={!canSaveWorkflow || Boolean(stepParameterDraftError)}
              loading={savePending}
              onClick={handleSaveDraft}
            >
              {t('pages.studio.studiobuildpanels.save.draft.2', 'Save draft')}
            </Button>
            <Button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="primary"
              onClick={onContinueToBind}
            >
              {t(
                'pages.studio.studiobuildpanels.continue.to.bind.4',
                'Continue to Bind',
              )}
            </Button>
          </Space>
        </div>
        <ConsoleOperationNotice
          errorMessage={t(
            'pages.studio.studiobuildpanels.workflowSaveFailed',
            'Could not save workflow. Try again.',
          )}
          notice={saveNotice}
        />
      </div>

      <div
        data-testid="workflow-editor-workspace"
        style={workflowWorkspaceRowStyle}
      >
        <section
          data-testid="workflow-build-primary-column"
          style={workflowCanvasPanelStyle}
        >
          <div style={workflowToolbarStyle}>
            <Space wrap size={[8, 8]}>
              <div style={sectionEyebrowStyle}>
                {t('pages.studio.studiobuildpanels.dag.canvas.2', 'DAG Canvas')}
              </div>
              <Tag color="processing">
                {t(
                  'pages.studio.studiobuildpanels.canvas.live.2',
                  'canvas · live',
                )}
              </Tag>
              <Typography.Text type="secondary">
                {workflowName ||
                  t(
                    'pages.studio.studiobuildpanels.untitled.workflow.2',
                    'Untitled workflow',
                  )}
              </Typography.Text>
            </Space>
            <div style={workflowToolbarActionsStyle}>
              <fieldset
                aria-label={t(
                  'pages.studio.studiobuildpanels.workflow.view.2',
                  'Workflow view',
                )}
                style={{
                  ...workflowViewSwitchStyle,
                  border: 0,
                  margin: 0,
                  padding: 0,
                }}
              >
                <button
                  aria-pressed={viewMode === 'canvas'}
                  className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                  onClick={() => setViewMode('canvas')}
                  style={
                    viewMode === 'canvas'
                      ? workflowViewSwitchButtonActiveStyle
                      : workflowViewSwitchButtonStyle
                  }
                  type="button"
                >
                  {t('pages.studio.studiobuildpanels.canvas.2', 'Canvas')}
                </button>
                <button
                  aria-pressed={viewMode === 'yaml'}
                  className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                  onClick={() => setViewMode('yaml')}
                  style={
                    viewMode === 'yaml'
                      ? workflowViewSwitchButtonActiveStyle
                      : workflowViewSwitchButtonStyle
                  }
                  type="button"
                >
                  {t('pages.studio.studiobuildpanels.yaml.3', 'YAML')}
                </button>
              </fieldset>
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={viewMode !== 'canvas' || Boolean(stepMutationPending)}
                loading={stepMutationPending === 'add'}
                onClick={() => setStepTypePickerOpen((current) => !current)}
              >
                {t('pages.studio.studiobuildpanels.add.step.2', 'Add step')}
              </Button>
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={viewMode !== 'canvas' || Boolean(stepMutationPending)}
                onClick={onAutoLayout}
              >
                {t(
                  'pages.studio.studiobuildpanels.auto.layout.2',
                  'Auto-layout',
                )}
              </Button>
            </div>
          </div>
          <div style={workflowCanvasBodyStyle}>
            {stepTypePickerOpen ? (
              <div
                data-testid="workflow-step-type-picker"
                style={workflowTypePickerStyle}
              >
                <div style={workflowSectionHeadingStyle}>
                  {t(
                    'pages.studio.studiobuildpanels.choose.step.type.2',
                    'Choose step type',
                  )}
                </div>
                <div style={workflowInlineMetaStyle}>
                  {t(
                    'pages.studio.studiobuildpanels.step',
                    'Choose which step type to insert, then connect it after the currently selected node.',
                  )}
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
                      <strong style={{ color: '#1f2937', fontSize: 13 }}>
                        {entry.stepType}
                      </strong>
                      <span
                        style={{
                          color: '#6b7280',
                          fontSize: 12,
                          lineHeight: '18px',
                        }}
                      >
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
                data-canonical-yaml-authoring-surface={STUDIO_WORKFLOW_YAML_AUTHORING_DELETION_TRACKING.canonicalSurfaces.join(
                  ' ',
                )}
                data-deletion-tracking-id={
                  STUDIO_WORKFLOW_YAML_AUTHORING_DELETION_TRACKING.issue
                }
                style={{
                  ...buildSurfaceCardStyle,
                  flex: '1 1 auto',
                  minHeight: 0,
                }}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 8,
                    justifyContent: 'space-between',
                  }}
                >
                  <div style={sectionEyebrowStyle}>
                    {t(
                      'pages.studio.studiobuildpanels.workflow.yaml.2',
                      'Workflow YAML',
                    )}
                  </div>
                  <Tag color="blue">
                    {t(
                      'pages.studio.studiobuildpanels.raw.draft.2',
                      'raw draft',
                    )}
                  </Tag>
                </div>
                <Input.TextArea
                  aria-label={t(
                    'pages.studio.studiobuildpanels.yaml.2',
                    'Define YAML',
                  )}
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
              <div style={sectionEyebrowStyle}>
                {t(
                  'pages.studio.studiobuildpanels.step.detail.2',
                  'Step Detail',
                )}
              </div>
              {selectedStep ? (
                <Typography.Text strong>{selectedStep.id}</Typography.Text>
              ) : null}
            </div>
            {selectedStep ? <Tag>{selectedStep.type}</Tag> : null}
          </div>
          {stepMutationError ? (
            <Alert message={stepMutationError} showIcon type="error" />
          ) : null}
          <div style={workflowStepDetailBodyStyle}>
            {selectedStep && stepDraft ? (
              <>
                <div style={workflowDetailsGridStyle}>
                  <div style={workflowFieldStyle}>
                    <div style={workflowSectionHeadingStyle}>
                      {t('pages.studio.studiobuildpanels.basics.2', 'Basics')}
                    </div>
                    <label
                      htmlFor="workflow-step-id"
                      style={workflowFieldLabelStyle}
                    >
                      {t('pages.studio.studiobuildpanels.step.id.3', 'Step ID')}
                    </label>
                    <Input
                      id="workflow-step-id"
                      aria-label={t(
                        'pages.studio.studiobuildpanels.step.id.4',
                        'Step ID',
                      )}
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
                    <label
                      htmlFor="workflow-step-type"
                      style={workflowFieldLabelStyle}
                    >
                      {t(
                        'pages.studio.studiobuildpanels.step.type.3',
                        'Step type',
                      )}
                    </label>
                    <Select
                      aria-label={t(
                        'pages.studio.studiobuildpanels.step.type.4',
                        'Step type',
                      )}
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
                    <div style={workflowSectionHeadingStyle}>
                      {t('pages.studio.studiobuildpanels.routing.2', 'Routing')}
                    </div>
                    <label
                      htmlFor="workflow-step-role"
                      style={workflowFieldLabelStyle}
                    >
                      {t(
                        'pages.studio.studiobuildpanels.target.role.3',
                        'Target role',
                      )}
                    </label>
                    <Select
                      allowClear
                      aria-label={t(
                        'pages.studio.studiobuildpanels.target.role.4',
                        'Target role',
                      )}
                      id="workflow-step-role"
                      options={workflowRoles.map((role) => ({
                        label: t(
                          'pages.studio.studiobuildpanels.copy.4',
                          '{value1} ({value2})',
                          { value1: role.name, value2: role.id },
                        ),
                        value: role.id,
                      }))}
                      placeholder={
                        workflowRoleIds[0] ||
                        t(
                          'pages.studio.studiobuildpanels.select.role',
                          'Select role',
                        )
                      }
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
                    <label
                      htmlFor="workflow-step-next"
                      style={workflowFieldLabelStyle}
                    >
                      {t(
                        'pages.studio.studiobuildpanels.next.step.3',
                        'Next step',
                      )}
                    </label>
                    <Select
                      allowClear
                      aria-label={t(
                        'pages.studio.studiobuildpanels.next.step.4',
                        'Next step',
                      )}
                      id="workflow-step-next"
                      options={availableNextStepIds.map((stepId) => ({
                        label: stepId,
                        value: stepId,
                      }))}
                      placeholder={t(
                        'pages.studio.studiobuildpanels.no.next.step.2',
                        'No next step',
                      )}
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
                    <div style={workflowSectionHeadingStyle}>
                      {t(
                        'pages.studio.studiobuildpanels.parameters.2',
                        'Parameters',
                      )}
                    </div>
                    {stepParameterConfig?.parseError ? (
                      <Alert
                        message={stepParameterConfig.parseError}
                        showIcon
                        type="error"
                      />
                    ) : null}
                    {stepParameterConfig?.fields.length ? (
                      <div style={{ display: 'grid', gap: 10 }}>
                        {stepParameterConfig.fields.map((field) => {
                          const label = formatNodeConfigFieldCopy(field.label);
                          const description = formatNodeConfigFieldCopy(
                            field.description,
                          );
                          const placeholder = formatNodeConfigFieldCopy(
                            field.placeholder,
                          );
                          const parameterAriaLabel = `Parameter ${label}`;
                          const inputId = `workflow-step-parameter-${field.name}`;
                          return (
                            <div key={field.name} style={workflowFieldStyle}>
                              <label
                                htmlFor={inputId}
                                style={workflowFieldLabelStyle}
                              >
                                {label}
                                {field.required ? ' *' : ''}
                              </label>
                              {field.kind === 'select' ? (
                                <Select
                                  allowClear={!field.required}
                                  aria-label={parameterAriaLabel}
                                  id={inputId}
                                  options={field.options.map((option) => ({
                                    label: formatNodeConfigFieldCopy(
                                      option.label,
                                    ),
                                    value: option.value,
                                  }))}
                                  placeholder={placeholder}
                                  value={field.value || undefined}
                                  onChange={(value) =>
                                    updateStepDraft((current) =>
                                      current
                                        ? {
                                            ...current,
                                            parametersText:
                                              updateNodeConfigFieldParametersText(
                                                {
                                                  field,
                                                  nodeType: current.type,
                                                  parametersText:
                                                    current.parametersText,
                                                  rawValue: String(value || ''),
                                                },
                                              ),
                                          }
                                        : current,
                                    )
                                  }
                                />
                              ) : field.kind === 'json' ? (
                                <Input.TextArea
                                  aria-label={parameterAriaLabel}
                                  autoSize={{ minRows: 3, maxRows: 8 }}
                                  id={inputId}
                                  placeholder={placeholder}
                                  value={field.value}
                                  onChange={(event) =>
                                    updateStepDraft((current) =>
                                      current
                                        ? {
                                            ...current,
                                            parametersText:
                                              updateNodeConfigFieldParametersText(
                                                {
                                                  field,
                                                  nodeType: current.type,
                                                  parametersText:
                                                    current.parametersText,
                                                  rawValue: event.target.value,
                                                },
                                              ),
                                          }
                                        : current,
                                    )
                                  }
                                />
                              ) : (
                                <Input
                                  aria-label={parameterAriaLabel}
                                  id={inputId}
                                  placeholder={placeholder}
                                  value={field.value}
                                  onChange={(event) =>
                                    updateStepDraft((current) =>
                                      current
                                        ? {
                                            ...current,
                                            parametersText:
                                              updateNodeConfigFieldParametersText(
                                                {
                                                  field,
                                                  nodeType: current.type,
                                                  parametersText:
                                                    current.parametersText,
                                                  rawValue: event.target.value,
                                                },
                                              ),
                                          }
                                        : current,
                                    )
                                  }
                                />
                              )}
                              <div style={workflowInlineMetaStyle}>
                                {description}
                              </div>
                            </div>
                          );
                        })}
                      </div>
                    ) : (
                      <div style={workflowInlineMetaStyle}>
                        {t(
                          'pages.studio.studiobuildpanels.step.type.raw.json',
                          'The current step type has no guided parameters declared. Edit the raw JSON below directly.',
                        )}
                      </div>
                    )}
                    <details style={workflowAdvancedSectionStyle}>
                      <summary
                        style={{
                          ...workflowSectionHeadingStyle,
                          cursor: 'pointer',
                        }}
                      >
                        {t(
                          'pages.studio.studiobuildpanels.raw.parameters.json.2',
                          'Raw parameters JSON',
                        )}
                      </summary>
                      <div style={{ display: 'grid', gap: 8, marginTop: 12 }}>
                        <label
                          htmlFor="workflow-step-parameters"
                          style={workflowFieldLabelStyle}
                        >
                          {t(
                            'pages.studio.studiobuildpanels.parameters.json.2',
                            'Parameters JSON',
                          )}
                        </label>
                        <Input.TextArea
                          id="workflow-step-parameters"
                          aria-label={t(
                            'pages.studio.studiobuildpanels.step.parameters.2',
                            'Step parameters',
                          )}
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
                      <summary
                        style={{
                          ...workflowSectionHeadingStyle,
                          cursor: 'pointer',
                        }}
                      >
                        {t(
                          'pages.studio.studiobuildpanels.advanced.routing.json.2',
                          'Advanced routing JSON',
                        )}
                      </summary>
                      <div style={{ display: 'grid', gap: 8, marginTop: 12 }}>
                        <label
                          htmlFor="workflow-step-branches"
                          style={workflowFieldLabelStyle}
                        >
                          {t(
                            'pages.studio.studiobuildpanels.branches.json.2',
                            'Branches JSON',
                          )}
                        </label>
                        <Input.TextArea
                          id="workflow-step-branches"
                          aria-label={t(
                            'pages.studio.studiobuildpanels.step.branches.2',
                            'Step branches',
                          )}
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
                    {t(
                      'pages.studio.studiobuildpanels.delete.step.2',
                      'Delete step',
                    )}
                  </Button>
                  <Button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={
                      !selectedStepId ||
                      !stepDraft ||
                      Boolean(stepMutationPending) ||
                      Boolean(stepParameterDraftError)
                    }
                    loading={stepMutationPending === 'apply'}
                    type="primary"
                    onClick={() => void handleApplyStepChanges()}
                  >
                    {t(
                      'pages.studio.studiobuildpanels.apply.changes.2',
                      'Apply changes',
                    )}
                  </Button>
                </div>
              </>
            ) : (
              <Empty
                description={t(
                  'pages.studio.studiobuildpanels.select.step.from.the.dag.2',
                  'Select a step from the DAG canvas first.',
                )}
              />
            )}
          </div>
        </section>
      </div>

      <section
        data-testid="workflow-dry-run-panel"
        style={workflowDryRunSectionStyle}
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
            <div style={sectionEyebrowStyle}>
              {t('pages.studio.studiobuildpanels.dry.run.4', 'Dry-run')}
            </div>
            <Typography.Text strong>
              {t(
                'pages.studio.studiobuildpanels.workflow.draft.run.2',
                'Workflow draft run',
              )}
            </Typography.Text>
          </div>
          <span
            style={{
              ...statusTagStyle,
              background: '#f6ffed',
              color: '#237804',
            }}
          >
            {t('pages.studio.studiobuildpanels.draft.input', 'Draft input')}
          </span>
        </div>
        <div style={{ display: 'grid', gap: 8 }}>
          <div style={workflowInlineMetaStyle}>
            {t('pages.studio.studiobuildpanels.route.label', 'Route: ')}
            {dryRunRouteLabel ||
              t(
                'pages.studio.studiobuildpanels.config.default.2',
                'Config default',
              )}
          </div>
          <div style={workflowInlineMetaStyle}>
            {t('pages.studio.studiobuildpanels.model.label', 'Model: ')}
            {dryRunModelLabel ||
              t(
                'pages.studio.studiobuildpanels.use.configured.default.2',
                'Use configured default',
              )}
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
                  {t(
                    'pages.studio.studiobuildpanels.connect.provider.2',
                    'Connect provider',
                  )}
                </Button>
              ) : undefined
            }
            message={dryRunBlockedReason}
            showIcon
            type="warning"
          />
        ) : null}
        <Input.TextArea
          aria-label={t(
            'pages.studio.studiobuildpanels.workflow.dry.run.input.2',
            'Workflow dry run input',
          )}
          autoSize={{ minRows: 4, maxRows: 6 }}
          placeholder={t(
            'pages.studio.studiobuildpanels.describe.the.input.you.want.2',
            'Describe the input you want this workflow member to handle.',
          )}
          value={runPrompt}
          onChange={(event) => onRunPromptChange(event.target.value)}
        />
        <Space wrap size={[8, 8]}>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            icon={<PlayCircleOutlined />}
            loading={runState.status === 'running'}
            type="primary"
            disabled={
              Boolean(dryRunBlockedReason?.trim()) ||
              runState.status === 'running' ||
              Boolean(stepParameterDraftError)
            }
            onClick={() => void handleRun()}
          >
            {t('pages.studio.studiobuildpanels.run.4', 'Run')}
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
                ),
              )
            }
          >
            {t(
              'pages.studio.studiobuildpanels.load.sample.input',
              'Load sample input',
            )}
          </Button>
        </Space>
        {workflowRunError ? (
          <Alert message={workflowRunError} showIcon type="error" />
        ) : null}
        <div>
          <div style={sectionEyebrowStyle}>
            {t('pages.studio.studiobuildpanels.output.4', 'Output')}
          </div>
          <pre style={workflowDryRunOutputStyle}>
            {renderRunOutput(runState)}
          </pre>
        </div>
        {renderRunSummary(runState) ? (
          <details style={dryRunDebugDetailsStyle}>
            <summary style={dryRunDebugSummaryStyle}>
              {t(
                'pages.studio.studiobuildpanels.debug.details.3',
                'Debug details',
              )}
            </summary>
            <pre style={{ ...dryRunSummaryStyle, marginTop: 10 }}>
              {renderRunSummary(runState)}
            </pre>
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
  readonly onRegisterLeaveGuard?: (
    guard: (() => Promise<boolean>) | null,
  ) => void;
  readonly onScriptBuildStateChange?: (
    state: StudioScriptBuildState | null,
  ) => void;
  readonly pendingScriptDraft?: StudioPendingScriptDraft | null;
  readonly onPendingScriptDraftChange?: (
    draft: StudioPendingScriptDraft | null,
  ) => void;
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
      definitionActorId:
        summary.definitionActorId || acceptedScript.definitionActorId,
      revision: summary.activeRevision || acceptedScript.revisionId,
      sourceHash: summary.activeSourceHash || acceptedScript.sourceHash,
    },
  };
}

function formatScriptDisplayLabel(
  detail: ScopedScriptDetail | null | undefined,
  fallback = 'Script',
): string {
  const record = detail as
    | (ScopedScriptDetail & {
        script?: { displayName?: string | null; name?: string | null } | null;
        source?: { displayName?: string | null; name?: string | null } | null;
      })
    | null
    | undefined;
  const candidate =
    record?.script?.displayName ||
    record?.script?.name ||
    record?.source?.displayName ||
    record?.source?.name ||
    '';
  return candidate.trim() || fallback;
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
  const [lastRunResult, setLastRunResult] =
    React.useState<DraftRunResult | null>(null);
  const [leaveDialogOpen, setLeaveDialogOpen] = React.useState(false);
  const leaveResolverRef = React.useRef<((value: boolean) => void) | null>(
    null,
  );
  const saveObservationTimerRef = React.useRef<number | null>(null);
  const saveObservationTokenRef = React.useRef(0);
  const activeScriptIdRef = React.useRef('');
  const availableScripts = React.useMemo(
    () =>
      (scriptsQuery.data ?? []).filter((detail): detail is ScopedScriptDetail =>
        Boolean(detail.available && detail.script),
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
    () =>
      availableScripts.find(
        (detail) => detail.script?.scriptId === selectedScriptId,
      ) || null,
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
  activeScriptIdRef.current = activeScript?.script?.scriptId || '';
  const activeScriptIsDraft = Boolean(
    pendingScriptDetail && activeScript === pendingScriptDetail,
  );
  const activeScriptIsObserved = Boolean(
    selectedObservedAppliedScript &&
      activeScript === selectedObservedAppliedScript,
  );
  const persistedSource = React.useMemo(
    () => activeScript?.source?.sourceText || '',
    [activeScript?.source?.sourceText],
  );
  const currentRevision = React.useMemo(
    () =>
      activeScript?.source?.revision ||
      activeScript?.script?.activeRevision ||
      '',
    [activeScript?.script?.activeRevision, activeScript?.source?.revision],
  );
  const selectedPackageEntry = React.useMemo(
    () => getSelectedPackageEntry(scriptPackage, selectedFilePath),
    [scriptPackage, selectedFilePath],
  );
  const editorMarkers = React.useMemo(
    () =>
      mapScriptMarkers(
        validationResult?.diagnostics,
        selectedPackageEntry?.path || '',
      ),
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

    const nextPackage =
      activeScriptIsDraft && !activeScript.source?.sourceText
        ? createScriptStarterPackage()
        : deserializePersistedSource(activeScript.source?.sourceText || '');
    const nextEntry =
      getSelectedPackageEntry(nextPackage, nextPackage.entrySourcePath) ||
      getSelectedPackageEntry(nextPackage, '') ||
      null;
    setScriptPackage(nextPackage);
    setSelectedFilePath(
      nextEntry?.path || nextPackage.entrySourcePath || 'Behavior.cs',
    );
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
    const firstAvailableScriptId = availableScripts[0]?.script?.scriptId;
    if (
      selectedScriptId ||
      pendingScriptDraft?.scriptId ||
      !firstAvailableScriptId
    ) {
      return;
    }

    onSelectScriptId(firstAvailableScriptId);
  }, [
    availableScripts,
    onSelectScriptId,
    pendingScriptDraft?.scriptId,
    selectedScriptId,
  ]);

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
      onScriptBuildStateChange?.(null);
    },
    [cancelSaveObservationPoll, onScriptBuildStateChange],
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
  const effectiveSaveStatus: StudioScriptBuildState['saveStatus'] = isDirty
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
    : isDirty && effectiveSaveStatus !== 'failed'
      ? 'Unsaved edits'
      : saveObservationStatus === 'accepted' ||
          effectiveSaveStatus === 'accepted'
        ? 'Save accepted'
        : saveObservationStatus === 'pending'
          ? 'Waiting for catalog'
          : effectiveSaveStatus === 'failed'
            ? 'Save needs attention'
            : activeScriptIsDraft
              ? 'Draft'
              : scriptReadyToBind
                ? 'Catalog applied'
                : 'Catalog script';
  const lifecycleStatusColor =
    lifecycleStatus === 'Catalog applied'
      ? 'green'
      : lifecycleStatus === 'Save needs attention'
        ? 'red'
        : lifecycleStatus === 'Waiting for catalog' ||
            lifecycleStatus === 'Save accepted'
          ? 'gold'
          : activeScriptIsDraft
            ? 'blue'
            : 'default';
  const bindReadinessLabel = !activeScript?.script?.scriptId
    ? 'Create or select a script'
    : validationStatus === 'invalid'
      ? 'Fix validation errors'
      : isDirty && validationStatus !== 'valid'
        ? 'Validate current source'
        : isDirty
          ? 'Save script'
          : effectiveSaveStatus === 'accepted'
            ? 'Waiting for catalog'
            : scriptReadyToBind
              ? 'Ready to bind'
              : 'Save script';
  const saveObservationInFlight =
    saveObservationStatus === 'accepted' || saveObservationStatus === 'pending';
  const saveNoticeType =
    saveObservationStatus === 'applied'
      ? 'success'
      : saveObservationStatus === 'accepted'
        ? 'info'
        : saveObservationStatus === 'failed'
          ? 'error'
          : 'warning';
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
      displayName:
        pendingScriptDraft?.displayName || activeScript.script.scriptId,
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
      cancelSaveObservationPoll();
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
    [
      activeScriptIsDraft,
      cancelSaveObservationPoll,
      onPendingScriptDraftChange,
      pendingScriptDraft,
    ],
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
        scriptRevision: currentRevision || undefined,
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
        t('pages.studio.studiobuildpanels.save.applied', 'Save applied.'),
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
      const observedScriptId = accepted.acceptedScript.scriptId;

      try {
        const observation = await scriptsApi.observeSaveScript(
          scopeId,
          observedScriptId,
          {
            revisionId: accepted.acceptedScript.revisionId,
            definitionActorId: accepted.acceptedScript.definitionActorId,
            sourceHash: accepted.acceptedScript.sourceHash,
            proposalId: accepted.acceptedScript.proposalId,
            expectedBaseRevision: accepted.acceptedScript.expectedBaseRevision,
            acceptedAt: accepted.acceptedScript.acceptedAt,
          },
        );
        if (
          saveObservationTokenRef.current !== token ||
          activeScriptIdRef.current !== observedScriptId
        ) {
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
              t(
                'pages.studio.studiobuildpanels.save.rejected',
                'Save rejected.',
              ),
          );
          return;
        }

        setSaveObservationStatus('pending');
        const nextDelay = SCRIPT_SAVE_OBSERVATION_POLL_DELAYS_MS[attemptIndex];
        if (nextDelay == null) {
          saveObservationTimerRef.current = null;
          setSaveNotice(
            t(
              'pages.studio.studiobuildpanels.save.accepted.waiting.for.catalog',
              'Save accepted. Still waiting for catalog; use Refresh catalog to check again.',
            ),
          );
          return;
        }

        setSaveNotice(
          t(
            'pages.studio.studiobuildpanels.save.accepted.checking.again',
            'Save accepted. Waiting for catalog; checking again in {value1}s.',
            { value1: Math.round(nextDelay / 1000) },
          ),
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
        if (
          saveObservationTokenRef.current !== token ||
          activeScriptIdRef.current !== observedScriptId
        ) {
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
      setSaveNotice(
        'Resolve the current workspace and select a script before saving.',
      );
      return;
    }

    cancelSaveObservationPoll();
    const savingScriptId = activeScript.script.scriptId;
    setSavePending(true);
    setSaveNotice('');
    try {
      const accepted = await scriptsApi.saveScript(scopeId, {
        scriptId: savingScriptId,
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
      if (activeScriptIdRef.current !== savingScriptId) {
        return;
      }
      setSaveObservationStatus('failed');
      setSaveStatus('failed');
      setSaveNotice(describeError(error));
    } finally {
      if (activeScriptIdRef.current === savingScriptId) {
        setSavePending(false);
      }
    }
  }, [
    activeScript?.script?.activeRevision,
    activeScript?.script?.scriptId,
    activeScriptIsDraft,
    cancelSaveObservationPoll,
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

    const nextPackage = removePackageFile(
      scriptPackage,
      selectedPackageEntry.path,
    );
    const nextEntry =
      getSelectedPackageEntry(nextPackage, nextPackage.entrySourcePath) ||
      getSelectedPackageEntry(nextPackage, '');
    commitScriptPackage(nextPackage, nextEntry?.path);
  }, [
    commitScriptPackage,
    packageEntries.length,
    scriptPackage,
    selectedPackageEntry,
  ]);

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
      setPromotionNotice(
        'Resolve the current workspace and script before proposing evolution.',
      );
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
          candidateRevision: currentRevision || undefined,
          candidateSource: serializePersistedSource(scriptPackage),
          reason: promotionReason.trim() || undefined,
        },
      );
      setPromotionHistory((current) => [decision, ...current].slice(0, 6));
      setPromotionNotice(
        decision.accepted
          ? 'Promotion accepted.'
          : decision.failureReason ||
              `Promotion ${decision.status || 'not accepted'}.`,
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
      setRunOutput(
        'Resolve the current workspace and select a script before running.',
      );
      return;
    }

    setRunPending(true);
    try {
      const result = await scriptsApi.runDraftScript({
        scopeId,
        scriptId: activeScript.script.scriptId,
        scriptRevision: currentRevision || undefined,
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
      <div
        data-testid="studio-script-build-panel"
        style={buildSurfaceCardStyle}
      >
        <Typography.Text type="secondary">
          {t(
            'pages.studio.studiobuildpanels.loading.workspace.scripts.2',
            'Loading workspace scripts...',
          )}
        </Typography.Text>
      </div>
    );
  }

  if (scriptsQuery.isError) {
    return (
      <div
        data-testid="studio-script-build-panel"
        style={buildSurfaceCardStyle}
      >
        <Alert
          message={describeError(scriptsQuery.error)}
          showIcon
          type="error"
        />
      </div>
    );
  }

  return (
    <div
      data-testid="studio-script-build-panel"
      style={
        hasActiveScript ? buildWorkbenchGridStyle : scriptLaunchpadGridStyle
      }
    >
      <div style={{ display: 'grid', gap: 16, minWidth: 0 }}>
        <section style={buildSurfaceCardStyle}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>
              {t(
                'pages.studio.studiobuildpanels.script.source.2',
                'Script Source',
              )}
            </div>
            <div style={sectionDescriptionStyle}>
              {hasActiveScript
                ? t(
                    'pages.studio.studiobuildpanels.script.mode.script.draft',
                    "Script mode does one thing: iterate on the current script draft's typed source, lint results, and dry-run implementation.",
                  )
                : t(
                    'pages.studio.studiobuildpanels.create.script.to.start.editing.saved',
                    'Create a script to start editing. Saved workspace scripts appear here when this catalog has one.',
                  )}
            </div>
          </div>
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 8,
              justifyContent: 'space-between',
            }}
          >
            <Space wrap size={[8, 8]}>
              {hasActiveScript ? (
                <>
                  <Tag color={lifecycleStatusColor}>{lifecycleStatus}</Tag>
                  <Tag color={scriptReadyToBind ? 'green' : 'default'}>
                    {bindReadinessLabel}
                  </Tag>
                </>
              ) : null}
              {hasActiveScript ? (
                <Select
                  aria-label={t(
                    'pages.studio.studiobuildpanels.script.id.2',
                    'Script ID',
                  )}
                  style={{ minWidth: 220 }}
                  placeholder={t(
                    'pages.studio.studiobuildpanels.select.script',
                    'Select a script',
                  )}
                  value={activeScript?.script?.scriptId || undefined}
                  onChange={onSelectScriptId}
                  options={[
                    ...(pendingScriptDraft?.scriptId
                      ? [
                          {
                            label: t(
                              'pages.studio.studiobuildpanels.script.draft',
                              'Script draft',
                            ),
                            value: pendingScriptDraft.scriptId,
                          },
                        ]
                      : []),
                    ...(observedAppliedScript?.script?.scriptId &&
                    !pendingScriptDraft?.scriptId &&
                    !availableScripts.some(
                      (detail) =>
                        detail.script?.scriptId ===
                        observedAppliedScript.script?.scriptId,
                    )
                      ? [
                          {
                            label: t(
                              'pages.studio.studiobuildpanels.script.applied',
                              'Applied script',
                            ),
                            value: observedAppliedScript.script.scriptId,
                          },
                        ]
                      : []),
                    ...availableScripts.map((detail) => ({
                      label: formatScriptDisplayLabel(detail),
                      value: detail.script?.scriptId || '',
                    })),
                  ]}
                />
              ) : null}
            </Space>
            {hasActiveScript ? (
              <Space wrap size={[8, 8]}>
                <Button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  disabled={validationPending}
                  loading={validationPending}
                  onClick={() => void handleValidate()}
                >
                  {t('pages.studio.studiobuildpanels.validate.2', 'Validate')}
                </Button>
                <Button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  disabled={saveDisabled}
                  icon={<CheckCircleOutlined />}
                  loading={savePending}
                  onClick={() => void handleSave()}
                >
                  {t(
                    'pages.studio.studiobuildpanels.save.script.2',
                    'Save script',
                  )}
                </Button>
              </Space>
            ) : null}
          </div>
          {saveNotice ? (
            <Alert
              message={saveNotice}
              showIcon
              type={saveNoticeType}
              action={
                saveObservationStatus === 'pending' ? (
                  <Button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    size="small"
                    onClick={() => void onRefreshScripts?.()}
                  >
                    {t(
                      'pages.studio.studiobuildpanels.refresh.catalog.2',
                      'Refresh catalog',
                    )}
                  </Button>
                ) : undefined
              }
            />
          ) : null}
          {hasActiveScript ? (
            <div
              aria-label={t(
                'pages.studio.studiobuildpanels.script.lifecycle.status.2',
                'Script lifecycle status',
              )}
              role="status"
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
                {formatScriptDisplayLabel(
                  activeScript,
                  t('pages.studio.studiobuildpanels.script', 'Script'),
                )}{' '}
                · {lifecycleStatus}{' '}
                {t(
                  'pages.studio.studiobuildpanels.validation.2',
                  '· validation',
                )}{' '}
                {validationStatus}{' '}
                {t('pages.studio.studiobuildpanels.save.2', '· save')}
                {saveObservationStatus}{' '}
                {t('pages.studio.studiobuildpanels.rev.2', '· rev')}{' '}
                {currentRevision
                  ? t(
                      'pages.studio.studiobuildpanels.version.ready',
                      'version ready',
                    )
                  : t(
                      'pages.studio.studiobuildpanels.generated.on.save.2',
                      'generated on save',
                    )}
              </Typography.Text>
            </div>
          ) : null}
          {validationError ? (
            <Alert message={validationError} showIcon type="error" />
          ) : null}
          {hasActiveScript && selectedPackageEntry ? (
            <div style={{ display: 'grid', gap: 12 }}>
              <details
                aria-label={t(
                  'pages.studio.studiobuildpanels.script.package.tree.2',
                  'Script package tree',
                )}
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
                  <span style={sectionEyebrowStyle}>
                    {t(
                      'pages.studio.studiobuildpanels.advanced.package.2',
                      'Advanced package',
                    )}
                  </span>
                  <Typography.Text type="secondary">
                    {packageEntries.length}{' '}
                    {t('pages.studio.studiobuildpanels.file.2', 'file')}
                    {packageEntries.length === 1 ? '' : 's'} ·{' '}
                    {scriptPackage.entrySourcePath ||
                      t(
                        'pages.studio.studiobuildpanels.no.entry.2',
                        'no entry',
                      )}{' '}
                    {t('pages.studio.studiobuildpanels.entry.2', 'entry')}
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
                    <div style={sectionEyebrowStyle}>
                      {t('pages.studio.studiobuildpanels.package.2', 'Package')}
                    </div>
                    <Typography.Text type="secondary">
                      Entry: {scriptPackage.entrySourcePath || '-'}{' '}
                      {t(
                        'pages.studio.studiobuildpanels.behavior.2',
                        '· Behavior:',
                      )}{' '}
                      {scriptPackage.entryBehaviorTypeName || '-'}
                    </Typography.Text>
                  </div>
                  <Space wrap size={[8, 8]}>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      size="small"
                      onClick={() => handleAddPackageFile('csharp')}
                    >
                      {t('pages.studio.studiobuildpanels.add.2', 'Add C#')}
                    </Button>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      size="small"
                      onClick={() => handleAddPackageFile('proto')}
                    >
                      {t(
                        'pages.studio.studiobuildpanels.add.proto.2',
                        'Add proto',
                      )}
                    </Button>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      size="small"
                      onClick={handleRenamePackageFile}
                    >
                      {t('pages.studio.studiobuildpanels.rename.2', 'Rename')}
                    </Button>
                    <Button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      disabled={packageEntries.length <= 1}
                      size="small"
                      onClick={handleRemovePackageFile}
                    >
                      {t('pages.studio.studiobuildpanels.remove.2', 'Remove')}
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
                          entry.path === selectedPackageEntry.path
                            ? '#111827'
                            : '#fffdf8',
                        border: '1px solid #efe7da',
                        borderRadius: 999,
                        color:
                          entry.path === selectedPackageEntry.path
                            ? '#fffdf8'
                            : '#374151',
                        cursor: 'pointer',
                        fontSize: 12,
                        fontWeight: 700,
                        padding: '6px 10px',
                      }}
                    >
                      {entry.kind === 'csharp' ? 'C#' : 'proto'} · {entry.path}
                      {entry.path === scriptPackage.entrySourcePath
                        ? t('pages.studio.studiobuildpanels.entry.3', '· entry')
                        : ''}
                    </button>
                  ))}
                </div>
                <div
                  style={{
                    display: 'grid',
                    gap: 10,
                    gridTemplateColumns: 'minmax(0, 1fr) auto',
                  }}
                >
                  <Input
                    aria-label={t(
                      'pages.studio.studiobuildpanels.entry.behavior.type.2',
                      'Entry behavior type',
                    )}
                    placeholder={t(
                      'pages.studio.studiobuildpanels.entry.behavior.type.for.example.2',
                      'Entry behavior type, for example DraftBehavior',
                    )}
                    value={scriptPackage.entryBehaviorTypeName}
                    onChange={(event) =>
                      commitScriptPackage(
                        updateEntryBehaviorTypeName(
                          scriptPackage,
                          event.target.value,
                        ),
                        selectedPackageEntry.path,
                      )
                    }
                  />
                  <Button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={selectedPackageEntry.kind !== 'csharp'}
                    onClick={handleSetEntrySource}
                  >
                    {t(
                      'pages.studio.studiobuildpanels.set.entry.source.2',
                      'Set entry source',
                    )}
                  </Button>
                </div>
              </details>
              <div style={{ minHeight: 520 }}>
                <ScriptCodeEditor
                  filePath={selectedPackageEntry.path}
                  language={
                    selectedPackageEntry.kind === 'csharp'
                      ? 'csharp'
                      : 'plaintext'
                  }
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
                  <div style={sectionEyebrowStyle}>
                    {t('pages.studio.studiobuildpanels.compiler.2', 'Compiler')}
                  </div>
                  <Typography.Text type="secondary">
                    {validationResult
                      ? validationResult.success
                        ? t(
                            'pages.studio.studiobuildpanels.validation.completed.without.blocking.errors.2',
                            'Validation completed without blocking errors.',
                          )
                        : t(
                            'pages.studio.studiobuildpanels.validation.returned.blocking.diagnostics.2',
                            'Validation returned blocking diagnostics.',
                          )
                      : t(
                          'pages.studio.studiobuildpanels.run.validate.to.refresh.compiler.diagnostics',
                          'Run Validate to refresh compiler diagnostics.',
                        )}
                  </Typography.Text>
                </div>
                <Space wrap size={[8, 8]}>
                  {validationResult?.diagnostics?.length ? (
                    <Tag color={validationResult.success ? 'blue' : 'red'}>
                      {t(
                        'pages.studio.studiobuildpanels.problems.2',
                        'Problems',
                      )}
                      {validationResult.diagnostics.length}
                    </Tag>
                  ) : (
                    <Tag color="green">
                      {t('pages.studio.studiobuildpanels.clean.2', 'Clean')}
                    </Tag>
                  )}
                </Space>
              </div>
              {validationResult?.diagnostics?.length ? (
                <section
                  aria-label={t(
                    'pages.studio.studiobuildpanels.script.validation.diagnostics.2',
                    'Script validation diagnostics',
                  )}
                  style={{
                    border: '1px solid #efe7da',
                    borderRadius: 16,
                    display: 'grid',
                    gap: 8,
                    padding: 12,
                  }}
                >
                  <div style={sectionEyebrowStyle}>
                    {t(
                      'pages.studio.studiobuildpanels.diagnostics.2',
                      'Diagnostics',
                    )}
                  </div>
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
                        <span
                          style={{
                            alignItems: 'center',
                            display: 'flex',
                            gap: 8,
                          }}
                        >
                          <Tag
                            color={
                              diagnostic.severity === 'error'
                                ? 'red'
                                : diagnostic.severity === 'warning'
                                  ? 'gold'
                                  : 'blue'
                            }
                          >
                            {diagnostic.severity}
                          </Tag>
                          <span style={{ color: '#6b5d4a', fontSize: 12 }}>
                            {formatScriptDiagnosticLocation(diagnostic)}
                          </span>
                          {diagnostic.code ? (
                            <span
                              style={{ color: severityColor, fontSize: 12 }}
                            >
                              {diagnostic.code}
                            </span>
                          ) : null}
                        </span>
                        <span
                          style={{
                            color: '#374151',
                            fontSize: 13,
                            lineHeight: '18px',
                          }}
                        >
                          {diagnostic.message}
                        </span>
                      </button>
                    );
                  })}
                </section>
              ) : null}
            </div>
          ) : (
            <Empty
              description={t(
                'pages.studio.studiobuildpanels.no.script.is.selected.yet',
                'No script is selected yet. Start a script draft to open the editor.',
              )}
            >
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                onClick={onCreateScriptDraft}
                type="primary"
              >
                {t('pages.studio.studiobuildpanels.add.script.2', 'Add script')}
              </Button>
            </Empty>
          )}
        </section>

        {hasActiveScript ? (
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 12,
              justifyContent: 'space-between',
            }}
          >
            <Typography.Text type="secondary">
              {scriptReadyToBind
                ? t(
                    'pages.studio.studiobuildpanels.script.revision.is.catalog.applied.continue',
                    'Script revision is catalog-applied. Continue to Bind to publish the callable member contract.',
                  )
                : t(
                    'pages.studio.studiobuildpanels.script.build.keeps.code.editing.here',
                    'Script Build keeps code editing here. {value1}.',
                    { value1: bindReadinessLabel },
                  )}
            </Typography.Text>
            <Button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              disabled={!scriptReadyToBind}
              type="primary"
              onClick={onContinueToBind}
            >
              {t(
                'pages.studio.studiobuildpanels.continue.to.bind.5',
                'Continue to Bind',
              )}
            </Button>
          </div>
        ) : null}

        <ScriptLeaveDialog
          open={leaveDialogOpen}
          onStay={() => resolveLeave(false)}
          onLeave={() => resolveLeave(true)}
        />
      </div>

      {hasActiveScript ? (
        <aside style={dryRunAsideStyle}>
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 8,
              justifyContent: 'space-between',
            }}
          >
            <div style={{ display: 'grid', gap: 4 }}>
              <div style={sectionEyebrowStyle}>
                {t('pages.studio.studiobuildpanels.dry.run.5', 'Dry-run')}
              </div>
              <Typography.Text strong>
                {t(
                  'pages.studio.studiobuildpanels.script.draft.run.2',
                  'Script draft run',
                )}
              </Typography.Text>
            </div>
            <span
              style={{
                ...statusTagStyle,
                background: '#fffbe6',
                color: '#ad6800',
              }}
            >
              {t('pages.studio.studiobuildpanels.draft.input.2', 'Draft input')}
            </span>
          </div>
          <div style={sectionDescriptionStyle}>
            {t(
              'pages.studio.studiobuildpanels.draft.run.source.editor',
              'Draft-run directly calls the script in the current source editor; you do not need to switch the scope default service to this script first.',
            )}
          </div>
          <Input.TextArea
            aria-label={t(
              'pages.studio.studiobuildpanels.script.dry.run.input.2',
              'Script dry run input',
            )}
            autoSize={{ minRows: 6, maxRows: 10 }}
            value={runInput}
            onChange={(event) => setRunInput(event.target.value)}
          />
          <Space wrap size={[8, 8]}>
            <Button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              icon={<PlayCircleOutlined />}
              loading={runPending}
              type="primary"
              onClick={() => void handleRun()}
            >
              {t('pages.studio.studiobuildpanels.run.5', 'Run')}
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
              {t(
                'pages.studio.studiobuildpanels.load.sample.input.2',
                'Load sample input',
              )}
            </Button>
          </Space>
          <div>
            {lastRunResult ? (
              <section
                aria-label={t(
                  'pages.studio.studiobuildpanels.script.dry.run.facts.2',
                  'Script dry run facts',
                )}
                style={{
                  border: '1px solid #efe7da',
                  borderRadius: 14,
                  display: 'grid',
                  gap: 6,
                  marginBottom: 12,
                  padding: 12,
                }}
              >
                <div style={sectionEyebrowStyle}>
                  {t('pages.studio.studiobuildpanels.run.facts.2', 'Run facts')}
                </div>
                {[
                  ['Command type', lastRunResult.commandTypeUrl],
                  ['Activity', lastRunResult.activityUrl],
                ].map(([label, value]) => (
                  <div key={label} style={{ display: 'grid', gap: 2 }}>
                    <span
                      style={{
                        color: '#8b7b63',
                        fontSize: 11,
                        fontWeight: 700,
                      }}
                    >
                      {label}
                    </span>
                    <Typography.Text
                      copyable={Boolean(value)}
                      ellipsis
                      style={{ fontSize: 12 }}
                    >
                      {value || '-'}
                    </Typography.Text>
                  </div>
                ))}
              </section>
            ) : null}
            <div style={sectionEyebrowStyle}>
              {t('pages.studio.studiobuildpanels.output.5', 'Output')}
            </div>
            <pre style={dryRunOutputStyle}>{runOutput}</pre>
          </div>
          <details
            aria-label={t(
              'pages.studio.studiobuildpanels.script.promotion.history.2',
              'Script promotion history',
            )}
            style={{
              border: '1px solid #efe7da',
              borderRadius: 14,
              padding: 12,
            }}
          >
            <summary style={{ ...sectionEyebrowStyle, cursor: 'pointer' }}>
              {t('pages.studio.studiobuildpanels.promotion.2', 'Promotion')}
            </summary>
            <div style={{ display: 'grid', gap: 10, marginTop: 12 }}>
              <Input.TextArea
                aria-label={t(
                  'pages.studio.studiobuildpanels.promotion.reason.2',
                  'Promotion reason',
                )}
                autoSize={{ minRows: 2, maxRows: 4 }}
                placeholder={t(
                  'pages.studio.studiobuildpanels.why.is.this.revision.ready.2',
                  'Why is this revision ready to promote?',
                )}
                value={promotionReason}
                onChange={(event) => setPromotionReason(event.target.value)}
              />
              <Button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={promotionPending}
                loading={promotionPending}
                onClick={() => void handlePromoteEvolution()}
              >
                {t(
                  'pages.studio.studiobuildpanels.propose.evolution.2',
                  'Propose evolution',
                )}
              </Button>
              {promotionNotice ? (
                <Alert
                  showIcon
                  message={promotionNotice}
                  type={
                    promotionNotice.startsWith('Promotion accepted')
                      ? 'success'
                      : 'warning'
                  }
                />
              ) : null}
              {promotionHistory.length > 0 ? (
                <div style={{ display: 'grid', gap: 8 }}>
                  {promotionHistory.map((decision) => (
                    <div
                      key={
                        decision.proposalId ||
                        `${decision.scriptId}:${decision.candidateRevision}`
                      }
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
                        {decision.accepted
                          ? t(
                              'pages.studio.studiobuildpanels.accepted.2',
                              'Accepted',
                            )
                          : decision.status ||
                            t(
                              'pages.studio.studiobuildpanels.decision.2',
                              'Decision',
                            )}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        {t(
                          'pages.studio.studiobuildpanels.script.promotion.version.summary',
                          'Script promotion version summary',
                        )}
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
                  {t(
                    'pages.studio.studiobuildpanels.no.promotion.decisions.in.this.2',
                    'No promotion decisions in this session.',
                  )}
                </Typography.Text>
              )}
            </div>
          </details>
        </aside>
      ) : null}
    </div>
  );
};

export type StudioGAgentBuildState = {
  readonly agentKind: string;
  readonly displayName: string;
  readonly initialPrompt: string;
  readonly persistenceMode: 'grain' | 'ephemeral';
  readonly role: string;
  readonly tools: readonly string[];
};

export type StudioGAgentBuildPanelProps = {
  readonly scopeId?: string;
  readonly currentMemberLabel: string;
  readonly gAgentKinds: readonly RuntimeGAgentKindDescriptor[];
  readonly gAgentKindsLoading: boolean;
  readonly gAgentKindsError: unknown;
  readonly selectedAgentKind: string;
  readonly onSelectAgentKind: (value: string) => void;
  readonly onBuildStateChange?: (state: StudioGAgentBuildState) => void;
  readonly onContinueToBind: (state: StudioGAgentBuildState) => void;
};

export const StudioGAgentBuildPanel: React.FC<StudioGAgentBuildPanelProps> = ({
  scopeId,
  currentMemberLabel,
  gAgentKinds,
  gAgentKindsLoading,
  gAgentKindsError,
  selectedAgentKind,
  onSelectAgentKind,
  onBuildStateChange,
  onContinueToBind,
}) => {
  const [displayName, setDisplayName] = React.useState(
    currentMemberLabel || 'Member GAgent',
  );
  const [role, setRole] = React.useState('intake-classifier');
  const [initialPrompt, setInitialPrompt] = React.useState(
    'You are the team member gagent. Own long-lived state and answer through the selected tools.',
  );
  const [toolsDraft, setToolsDraft] = React.useState(
    'classify_intent, detect_language',
  );
  const [persistenceMode, setPersistenceMode] = React.useState<
    'grain' | 'ephemeral'
  >('grain');
  const [runPrompt, setRunPrompt] = React.useState(
    'Classify this refund request and keep the member state in context.',
  );
  const [runState, setRunState] =
    React.useState<DraftRunState>(IDLE_DRAFT_RUN_STATE);
  const abortControllerRef = React.useRef<AbortController | null>(null);
  const selectedKindDescriptor = React.useMemo(
    () =>
      gAgentKinds.find(
        (descriptor) =>
          buildRuntimeGAgentKindValue(descriptor) === selectedAgentKind,
      ) || null,
    [gAgentKinds, selectedAgentKind],
  );
  const selectedAgentKindValue =
    selectedAgentKind ||
    (gAgentKinds[0] ? buildRuntimeGAgentKindValue(gAgentKinds[0]) : '');
  const toolTags = React.useMemo(
    () =>
      toolsDraft
        .split(',')
        .map((item) => item.trim())
        .filter(Boolean),
    [toolsDraft],
  );
  const currentBuildState = React.useMemo<StudioGAgentBuildState>(
    () => ({
      agentKind: selectedAgentKindValue,
      displayName: displayName.trim(),
      initialPrompt: initialPrompt.trim(),
      persistenceMode,
      role: role.trim(),
      tools: toolTags,
    }),
    [
      displayName,
      initialPrompt,
      persistenceMode,
      role,
      selectedAgentKindValue,
      toolTags,
    ],
  );

  React.useEffect(() => {
    if (!selectedAgentKind && selectedAgentKindValue) {
      onSelectAgentKind(selectedAgentKindValue);
    }
  }, [onSelectAgentKind, selectedAgentKind, selectedAgentKindValue]);

  React.useEffect(() => {
    setDisplayName(
      (current) => current || currentMemberLabel || 'Member GAgent',
    );
  }, [currentMemberLabel]);

  React.useEffect(() => {
    onBuildStateChange?.(currentBuildState);
  }, [currentBuildState, onBuildStateChange]);

  React.useEffect(
    () => () => {
      abortControllerRef.current?.abort();
    },
    [],
  );

  const handleRun = React.useCallback(async () => {
    if (!scopeId || !selectedAgentKindValue.trim() || !runPrompt.trim()) {
      setRunState({
        ...IDLE_DRAFT_RUN_STATE,
        error:
          'Workspace, GAgent kind, and prompt are required before running.',
        status: 'error',
      });
      return;
    }

    abortControllerRef.current?.abort();
    const controller = new AbortController();
    abortControllerRef.current = controller;
    const timeoutId = window.setTimeout(() => {
      if (!controller.signal.aborted) {
        controller.abort(createGAgentDraftRunTimeoutError());
      }
    }, GAGENT_DRAFT_RUN_CLIENT_TIMEOUT_MS);
    setRunState({
      ...IDLE_DRAFT_RUN_STATE,
      status: 'running',
    });

    try {
      const response = await runtimeGAgentApi.streamDraftRun(
        scopeId,
        {
          agentKind: selectedAgentKindValue,
          prompt: runPrompt,
          timeoutMs: GAGENT_DRAFT_RUN_TIMEOUT_MS,
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
        const reason = controller.signal.reason;
        if (reason instanceof Error) {
          setRunState({
            ...IDLE_DRAFT_RUN_STATE,
            error: reason.message,
            status: 'error',
          });
        }
        return;
      }

      setRunState({
        ...IDLE_DRAFT_RUN_STATE,
        error: describeError(error),
        status: 'error',
      });
    } finally {
      window.clearTimeout(timeoutId);
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
      }
    }
  }, [runPrompt, scopeId, selectedAgentKindValue]);

  return (
    <div
      data-testid="studio-gagent-build-panel"
      style={buildWorkbenchGridStyle}
    >
      <div style={{ display: 'grid', gap: 16, minWidth: 0 }}>
        <section style={buildSurfaceCardStyle}>
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>
              {t(
                'pages.studio.studiobuildpanels.gagent.definition.2',
                'GAgent Definition',
              )}
            </div>
            <div style={sectionDescriptionStyle}>
              {t(
                'pages.studio.studiobuildpanels.gagent.mode.build.member',
                "GAgent mode defines the current member's Agent kind, display name, role, initial Prompt, tools, and state persistence semantics in Build.",
              )}
            </div>
          </div>
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 8,
              justifyContent: 'space-between',
            }}
          >
            <Space wrap size={[8, 8]}>
              <Tag color="green">
                {t(
                  'pages.studio.studiobuildpanels.template.seeded.2',
                  'template · seeded',
                )}
              </Tag>
              {selectedKindDescriptor ? (
                <Tag>{buildRuntimeGAgentKindLabel(selectedKindDescriptor)}</Tag>
              ) : null}
            </Space>
          </div>
          {gAgentKindsError ? (
            <Alert
              message={describeError(gAgentKindsError)}
              showIcon
              type="error"
            />
          ) : null}
          <div
            style={{
              display: 'grid',
              gap: 16,
              gridTemplateColumns: '160px minmax(0, 1fr)',
            }}
          >
            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>
              {t('pages.studio.studiobuildpanels.gagent.kind.2', 'GAgent kind')}
            </div>
            <Select
              aria-label={t(
                'pages.studio.studiobuildpanels.gagent.type.2',
                'GAgent kind',
              )}
              loading={gAgentKindsLoading}
              value={selectedAgentKindValue || undefined}
              onChange={onSelectAgentKind}
              options={gAgentKinds.map((descriptor) => ({
                label: buildRuntimeGAgentKindLabel(descriptor),
                value: buildRuntimeGAgentKindValue(descriptor),
              }))}
              placeholder={t(
                'pages.studio.studiobuildpanels.select.typed.gagent.2',
                'Select a GAgent kind',
              )}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>
              {t(
                'pages.studio.studiobuildpanels.display.name.2',
                'Display name',
              )}
            </div>
            <Input
              aria-label={t(
                'pages.studio.studiobuildpanels.gagent.display.name.2',
                'GAgent display name',
              )}
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>
              {t('pages.studio.studiobuildpanels.role.2', 'Role')}
            </div>
            <Input
              aria-label={t(
                'pages.studio.studiobuildpanels.gagent.role.2',
                'GAgent role',
              )}
              value={role}
              onChange={(event) => setRole(event.target.value)}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>
              {t(
                'pages.studio.studiobuildpanels.initial.prompt.2',
                'Initial prompt',
              )}
            </div>
            <Input.TextArea
              aria-label={t(
                'pages.studio.studiobuildpanels.gagent.initial.prompt.2',
                'GAgent initial prompt',
              )}
              autoSize={{ minRows: 4, maxRows: 8 }}
              value={initialPrompt}
              onChange={(event) => setInitialPrompt(event.target.value)}
            />

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>
              {t('pages.studio.studiobuildpanels.tools.2', 'Tools')}
            </div>
            <div style={{ display: 'grid', gap: 10 }}>
              <Input
                aria-label={t(
                  'pages.studio.studiobuildpanels.gagent.tools.2',
                  'GAgent tools',
                )}
                value={toolsDraft}
                onChange={(event) => setToolsDraft(event.target.value)}
                placeholder={t(
                  'pages.studio.studiobuildpanels.classify.intent.detect.language.2',
                  'classify_intent, detect_language',
                )}
              />
              <Space wrap size={[8, 8]}>
                {toolTags.length > 0 ? (
                  toolTags.map((tool) => (
                    <Tag key={tool} color="blue">
                      {tool}
                    </Tag>
                  ))
                ) : (
                  <Tag>
                    {t(
                      'pages.studio.studiobuildpanels.add.tool.2',
                      '+ add tool',
                    )}
                  </Tag>
                )}
              </Space>
            </div>

            <div style={{ ...sectionEyebrowStyle, paddingTop: 10 }}>
              {t(
                'pages.studio.studiobuildpanels.state.persistence.2',
                'State persistence',
              )}
            </div>
            <Radio.Group
              value={persistenceMode}
              onChange={(event) => setPersistenceMode(event.target.value)}
            >
              <Space direction="vertical">
                <Radio value="grain">
                  {t(
                    'pages.studio.studiobuildpanels.orleans.grain.2',
                    'Orleans grain',
                  )}
                </Radio>
                <Radio value="ephemeral">
                  {t('pages.studio.studiobuildpanels.ephemeral.2', 'Ephemeral')}
                </Radio>
              </Space>
            </Radio.Group>
          </div>
        </section>

        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Typography.Text type="secondary">
            {t(
              'pages.studio.studiobuildpanels.gagent.build.actor.service',
              'GAgent Build only defines Actor semantics. Publish the Service and Endpoint in Bind.',
            )}
          </Typography.Text>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            disabled={!selectedAgentKindValue}
            type="primary"
            onClick={() => onContinueToBind(currentBuildState)}
          >
            {t(
              'pages.studio.studiobuildpanels.continue.to.bind.6',
              'Continue to Bind',
            )}
          </Button>
        </div>
      </div>

      <aside style={dryRunAsideStyle}>
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 8,
            justifyContent: 'space-between',
          }}
        >
          <div style={{ display: 'grid', gap: 4 }}>
            <div style={sectionEyebrowStyle}>
              {t('pages.studio.studiobuildpanels.dry.run.6', 'Dry-run')}
            </div>
            <Typography.Text strong>
              {t(
                'pages.studio.studiobuildpanels.gagent.draft.run.2',
                'GAgent draft run',
              )}
            </Typography.Text>
          </div>
          <span
            style={{
              ...statusTagStyle,
              background: '#f6ffed',
              color: '#237804',
            }}
          >
            {t('pages.studio.studiobuildpanels.draft.input.3', 'Draft input')}
          </span>
        </div>
        <div style={sectionDescriptionStyle}>
          {t(
            'pages.studio.studiobuildpanels.gagent.prompt.transcript',
            'Run the currently selected GAgent kind as a draft to verify that the prompt and transcript match expectations.',
          )}
        </div>
        <Input.TextArea
          aria-label={t(
            'pages.studio.studiobuildpanels.gagent.dry.run.input.2',
            'GAgent dry run input',
          )}
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
            {t('pages.studio.studiobuildpanels.run.6', 'Run')}
          </Button>
          <Button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={() =>
              setRunPrompt(
                'Classify this support ticket, keep the member state, and decide whether to escalate.',
              )
            }
          >
            {t(
              'pages.studio.studiobuildpanels.load.sample.input.3',
              'Load sample input',
            )}
          </Button>
        </Space>
        <div>
          <div style={sectionEyebrowStyle}>
            {t('pages.studio.studiobuildpanels.output.6', 'Output')}
          </div>
          <pre style={dryRunOutputStyle}>{renderRunOutput(runState)}</pre>
        </div>
        {getGAgentDraftRunRecoveryText(runState) ? (
          <Alert
            showIcon
            message={
              runState.status === 'error'
                ? t(
                    'pages.studio.studiobuildpanels.build.dry.run.needs.attention',
                    'Build dry-run needs attention',
                  )
                : runState.status === 'success'
                  ? t(
                      'pages.studio.studiobuildpanels.build.dry.run.ready',
                      'Build dry-run is ready',
                    )
                  : t(
                      'pages.studio.studiobuildpanels.build.dry.run.running',
                      'Build dry-run is running',
                    )
            }
            description={getGAgentDraftRunRecoveryText(runState)}
            type={runState.status === 'error' ? 'warning' : 'info'}
          />
        ) : null}
        {renderRunSummary(runState) ? (
          <details style={dryRunDebugDetailsStyle}>
            <summary style={dryRunDebugSummaryStyle}>
              {t(
                'pages.studio.studiobuildpanels.debug.details.4',
                'Debug details',
              )}
            </summary>
            <pre style={{ ...dryRunSummaryStyle, marginTop: 10 }}>
              {renderRunSummary(runState)}
            </pre>
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

export function getDefaultBuildModeCards(
  scriptsEnabled: boolean,
): readonly StudioBuildModeCard[] {
  return [
    {
      key: 'workflow',
      label: 'Workflow',
      description: t(
        'pages.studio.studiobuildpanels.compose.steps.as.dag.best.2',
        'Compose steps as a DAG. Best when the flow is known and parallel fan-out matters.',
      ),
      hint: 'When · Multiple agents hand off predictably',
    },
    {
      key: 'script',
      label: 'Script',
      description: t(
        'pages.studio.studiobuildpanels.write.typed.script.that.handles.2',
        'Write a typed script that handles deterministic business logic and code-level branches.',
      ),
      hint: scriptsEnabled
        ? 'When · You need code-level control'
        : t(
            'pages.studio.studiobuildpanels.copy.3',
            'Script capability is not enabled in the current environment.',
          ),
      disabled: !scriptsEnabled,
    },
    {
      key: 'gagent',
      label: 'GAgent',
      description: t(
        'pages.studio.studiobuildpanels.wire.typed.gagent.actor.with.2',
        'Wire a GAgent kind actor with long-lived state. Best when one member owns durable behavior.',
      ),
      hint: 'When · State lives with one agent',
    },
  ];
}
