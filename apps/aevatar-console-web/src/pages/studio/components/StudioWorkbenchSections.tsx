import {
  ApiOutlined,
  ApartmentOutlined,
  AppstoreOutlined,
  BarsOutlined,
  CaretRightFilled,
  CheckOutlined,
  CloseOutlined,
  CloseCircleFilled,
  CodeOutlined,
  CopyOutlined,
  DatabaseOutlined,
  DeleteOutlined,
  DownOutlined,
  ExpandOutlined,
  ExportOutlined,
  FileTextOutlined,
  FolderAddOutlined,
  LoadingOutlined,
  PlayCircleFilled,
  PlusOutlined,
  QuestionCircleOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  SearchOutlined,
  SaveOutlined,
  UploadOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
  ProCard,
} from '@ant-design/pro-components';
import {
  Alert,
  Button,
  Col,
  Divider,
  Drawer,
  Empty,
  Input,
  Modal,
  Row,
  Select,
  Space,
  Tag,
  Tabs,
  Typography,
  message,
} from 'antd';
import React from 'react';
import type { Edge, Node } from '@xyflow/react';
import ReactDOM from 'react-dom';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import { history } from '@/shared/navigation/history';
import { buildRuntimeGAgentsHref } from '@/shared/navigation/runtimeRoutes';
import type { PlaygroundPromptHistoryEntry } from '@/shared/playground/promptHistory';
import {
  buildRuntimeGAgentAssemblyQualifiedName,
  buildRuntimeGAgentTypeLabel,
  matchesRuntimeGAgentTypeDescriptor,
  type RuntimeGAgentTypeDescriptor,
} from '@/shared/models/runtime/gagents';
import type {
  StudioConnectorCatalog,
  StudioConnectorDefinition,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioProviderSettings,
  StudioProviderType,
  StudioRoleDefinition,
  StudioScopeBindingStatus,
  StudioRuntimeTestResult,
  StudioWorkflowFile,
  StudioWorkflowSummary,
  StudioWorkspaceSettings,
} from '@/shared/studio/models';
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  formatStudioScopeBindingImplementationKind,
  getStudioScopeBindingCurrentRevision,
} from '@/shared/studio/models';
import type {
  StudioGraphEdgeData,
  StudioGraphRole,
  StudioGraphStep,
  StudioGraphNodeData,
} from '@/shared/studio/graph';
import {
  STUDIO_GRAPH_CATEGORIES,
} from '@/shared/studio/graph';
import {
  buildExecutionTrace,
  decorateEdgesForExecution,
  decorateNodesForExecution,
  findExecutionLogIndexForStep,
  formatDurationBetween,
  formatExecutionLogClipboard,
  formatExecutionLogsClipboard,
  type ExecutionInteractionState,
} from '@/shared/studio/execution';
import { formatDateTime } from '@/shared/datetime/dateTime';
import {
  cardListActionStyle,
  cardListHeaderStyle,
  cardListItemStyle,
  cardListMainStyle,
  cardListStyle,
  cardStackStyle,
  codeBlockStyle,
  drawerBodyStyle,
  drawerScrollStyle,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import { describeError } from '@/shared/ui/errorText';

type QueryState<T> = {
  readonly isLoading: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly data: T | undefined;
};

type DraftMode = '' | 'new';
type LegacySource = '' | 'playground';
type StudioInspectorTab = 'node' | 'roles' | 'yaml';
type StudioToolDrawerMode = 'palette' | 'ask-ai';
type StudioGAgentBindingEndpointDraft = {
  readonly endpointId: string;
  readonly displayName: string;
  readonly kind: 'command' | 'chat';
  readonly requestTypeUrl: string;
  readonly responseTypeUrl: string;
  readonly description: string;
};
type StudioGAgentBindingDraft = {
  readonly displayName: string;
  readonly actorTypeName: string;
  readonly endpoints: readonly StudioGAgentBindingEndpointDraft[];
  readonly openRunsEndpointId: string;
  readonly prompt: string;
  readonly payloadBase64: string;
};

type StudioSelectedGraphEdge = {
  readonly edgeId: string;
  readonly sourceStepId: string;
  readonly targetStepId: string;
  readonly branchLabel: string | null;
  readonly kind: 'next' | 'branch';
  readonly implicit: boolean;
};

function readWorkflowSortTimestamp(value: string): number {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function compareWorkflowSummaryPriority(
  left: StudioWorkflowSummary,
  right: StudioWorkflowSummary,
): number {
  const updatedDelta =
    readWorkflowSortTimestamp(right.updatedAtUtc) -
    readWorkflowSortTimestamp(left.updatedAtUtc);
  if (updatedDelta !== 0) {
    return updatedDelta;
  }

  if (left.stepCount !== right.stepCount) {
    return right.stepCount - left.stepCount;
  }

  const leftDescriptionLength = left.description.trim().length;
  const rightDescriptionLength = right.description.trim().length;
  if (leftDescriptionLength !== rightDescriptionLength) {
    return rightDescriptionLength - leftDescriptionLength;
  }

  return left.workflowId.localeCompare(right.workflowId);
}

export function dedupeStudioWorkflowSummaries(
  workflows: readonly StudioWorkflowSummary[],
): StudioWorkflowSummary[] {
  const dedupedWorkflows = new Map<string, StudioWorkflowSummary>();

  for (const workflow of workflows) {
    const key =
      workflow.name.trim().toLowerCase() ||
      workflow.workflowId.trim().toLowerCase();
    const current = dedupedWorkflows.get(key);
    if (!current) {
      dedupedWorkflows.set(key, workflow);
      continue;
    }

    if (compareWorkflowSummaryPriority(workflow, current) < 0) {
      dedupedWorkflows.set(key, workflow);
    }
  }

  return Array.from(dedupedWorkflows.values()).sort((left, right) =>
    compareWorkflowSummaryPriority(left, right),
  );
}

const DEFAULT_GAGENT_REQUEST_TYPE_URL =
  'type.googleapis.com/google.protobuf.StringValue';

function createStudioGAgentBindingPresetEndpointDraft(
  preset: 'command' | 'chat',
  overrides?: Partial<StudioGAgentBindingEndpointDraft>,
): StudioGAgentBindingEndpointDraft {
  if (preset === 'chat') {
    return {
      endpointId: overrides?.endpointId ?? 'chat',
      displayName: overrides?.displayName ?? 'Chat',
      kind: 'chat',
      requestTypeUrl: overrides?.requestTypeUrl ?? '',
      responseTypeUrl: overrides?.responseTypeUrl ?? '',
      description: overrides?.description ?? 'Open the chat endpoint.',
    };
  }

  return {
    endpointId: overrides?.endpointId ?? 'run',
    displayName: overrides?.displayName ?? 'Run',
    kind: 'command',
    requestTypeUrl:
      overrides?.requestTypeUrl ?? DEFAULT_GAGENT_REQUEST_TYPE_URL,
    responseTypeUrl: overrides?.responseTypeUrl ?? '',
    description: overrides?.description ?? 'Run the bound GAgent.',
  };
}

function createStudioGAgentBindingEndpointDraft(
  overrides?: Partial<StudioGAgentBindingEndpointDraft>,
): StudioGAgentBindingEndpointDraft {
  return createStudioGAgentBindingPresetEndpointDraft(
    overrides?.kind ?? 'command',
    overrides,
  );
}

function createStudioGAgentBindingDraft(
  displayName: string,
): StudioGAgentBindingDraft {
  const defaultEndpoint = createStudioGAgentBindingEndpointDraft();
  return {
    displayName,
    actorTypeName: '',
    endpoints: [defaultEndpoint],
    openRunsEndpointId: defaultEndpoint.endpointId,
    prompt: '',
    payloadBase64: '',
  };
}

function inferStudioGAgentEndpointPreset(
  actorTypeName: string,
  descriptor?: RuntimeGAgentTypeDescriptor | null,
): 'command' | 'chat' {
  const candidates = [
    actorTypeName,
    descriptor?.typeName,
    descriptor?.fullName,
  ]
    .map((value) => value?.trim().toLowerCase() ?? '')
    .filter((value) => value.length > 0);

  return candidates.some((value) => value.includes('chat')) ? 'chat' : 'command';
}

function isDefaultStudioGAgentEndpointValue(
  value: string,
  defaults: readonly string[],
): boolean {
  const normalized = value.trim().toLowerCase();
  return normalized.length === 0 || defaults.includes(normalized);
}

function applyStudioGAgentEndpointPreset(
  endpoint: StudioGAgentBindingEndpointDraft,
  preset: 'command' | 'chat',
): StudioGAgentBindingEndpointDraft {
  const previousPreset = endpoint.kind === 'chat' ? 'chat' : 'command';
  const previousDefaults = previousPreset === 'chat'
    ? {
        endpointIds: ['chat'],
        displayNames: ['chat'],
        requestTypeUrls: [''],
        descriptions: ['open the chat endpoint.'],
      }
    : {
        endpointIds: ['run'],
        displayNames: ['run'],
        requestTypeUrls: [
          '',
          DEFAULT_GAGENT_REQUEST_TYPE_URL.toLowerCase(),
        ],
        descriptions: ['run the bound gagent.'],
      };
  const nextDefaults = createStudioGAgentBindingPresetEndpointDraft(preset);

  return {
    ...endpoint,
    kind: nextDefaults.kind,
    endpointId: isDefaultStudioGAgentEndpointValue(
      endpoint.endpointId,
      previousDefaults.endpointIds,
    )
      ? nextDefaults.endpointId
      : endpoint.endpointId,
    displayName: isDefaultStudioGAgentEndpointValue(
      endpoint.displayName,
      previousDefaults.displayNames,
    )
      ? nextDefaults.displayName
      : endpoint.displayName,
    requestTypeUrl: isDefaultStudioGAgentEndpointValue(
      endpoint.requestTypeUrl,
      previousDefaults.requestTypeUrls,
    )
      ? nextDefaults.requestTypeUrl
      : endpoint.requestTypeUrl,
    responseTypeUrl:
      preset === 'chat' && endpoint.responseTypeUrl.trim().length === 0
        ? ''
        : endpoint.responseTypeUrl,
    description: isDefaultStudioGAgentEndpointValue(
      endpoint.description,
      previousDefaults.descriptions,
    )
      ? nextDefaults.description
      : endpoint.description,
  };
}

type StudioNoticeLike = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type StudioSettingsDraftLike = {
  readonly runtimeBaseUrl: string;
  readonly defaultProviderName: string;
  readonly providerTypes: StudioProviderType[];
  readonly providers: StudioProviderSettings[];
};

function isScopeDirectoryPath(path: string): boolean {
  return path.trim().startsWith('scope://');
}

const workflowWorkspaceRowStyle: React.CSSProperties = {
  flex: '1 1 0',
  minHeight: 0,
  height: '100%',
  overflow: 'hidden',
};

const workflowSidebarStackStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
};

const workflowColumnStretchStyle: React.CSSProperties = {
  ...stretchColumnStyle,
  flex: '1 1 0',
  flexDirection: 'column',
  height: '100%',
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
};

const workflowSectionShellStyle: React.CSSProperties = {
  border: '1px solid #E6E3DE',
  borderRadius: 32,
  background: 'rgba(255, 255, 255, 0.94)',
  padding: 20,
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  boxShadow: '0 22px 52px rgba(17, 24, 39, 0.06)',
};

const workflowSectionFillStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
};

const workflowSectionHeaderStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  gap: 12,
};

const workflowSectionHeadingStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 600,
  letterSpacing: '0.14em',
  textTransform: 'uppercase',
  color: '#6B7280',
};

const workflowSurfaceStyle: React.CSSProperties = {
  border: '1px solid #EEEAE4',
  borderRadius: 20,
  background: '#FAF8F4',
  padding: 14,
};

const workflowSurfaceActiveStyle: React.CSSProperties = {
  borderColor: '#B8D2FF',
  background: '#EEF5FF',
  boxShadow: '0 12px 28px rgba(47, 111, 236, 0.12)',
};

const workflowToolbarSurfaceStyle: React.CSSProperties = {
  borderBottom: '1px solid #E6E3DE',
  background: 'rgba(255, 255, 255, 0.72)',
  backdropFilter: 'blur(10px)',
  padding: 16,
};

const workflowToolbarStackStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const workflowBrowserStyle: React.CSSProperties = {
  border: '1px solid #E6E3DE',
  borderRadius: 38,
  background: '#F2F1EE',
  boxShadow: '0 26px 64px rgba(17, 24, 39, 0.08)',
  overflow: 'hidden',
  width: '100%',
  flex: 1,
  minWidth: 0,
  minHeight: 0,
  height: '100%',
  display: 'flex',
  flexDirection: 'column',
};

const workflowSectionCopyStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: 1.6,
  color: 'var(--ant-color-text-tertiary)',
};

const workflowPanelIconButtonStyle: React.CSSProperties = {
  width: 36,
  height: 36,
  borderRadius: 999,
  border: '1px solid #E5E1DA',
  background: '#FFFFFF',
  color: 'var(--ant-color-text-tertiary)',
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowDirectorySelectButtonStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  flex: 1,
  height: 'auto',
  justifyContent: 'flex-start',
  minWidth: 0,
  padding: 0,
  textAlign: 'left',
  whiteSpace: 'normal',
};

const workflowDirectoryTextStackStyle: React.CSSProperties = {
  ...cardStackStyle,
  minWidth: 0,
};

const workflowDirectoryLabelStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 13,
  fontWeight: 600,
  color: '#1F2937',
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const workflowDirectoryPathStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 11,
  lineHeight: 1.6,
  color: 'var(--ant-color-text-tertiary)',
  marginTop: 4,
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const workflowToolbarLayoutStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 12,
};

const workflowSearchFieldStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 10,
  flex: '1 1 260px',
  minWidth: 260,
  maxWidth: 420,
  padding: '0 14px',
  height: 44,
  border: '1px solid #E5E1DA',
  borderRadius: 18,
  background: '#FFFFFF',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowSearchInputStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
  border: 0,
  outline: 'none',
  background: 'transparent',
  fontSize: 14,
  lineHeight: 1.5,
  color: '#1F2937',
};

const workflowToolbarActionsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: 10,
  justifyContent: 'flex-end',
};

const workflowSummaryStripStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
};

const workflowSummaryCardStyle: React.CSSProperties = {
  ...workflowSurfaceStyle,
  flex: '1 1 340px',
  minWidth: 0,
  padding: '14px 16px',
  borderRadius: 18,
};

const workflowSummaryCardRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 16,
  flexWrap: 'wrap',
  minWidth: 0,
};

const workflowSummaryCardBodyStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  minWidth: 0,
  flex: '1 1 220px',
};

const workflowSummaryHintStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--ant-color-text-tertiary)',
};

const workflowSummaryActionsStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  flexWrap: 'wrap',
};

const workflowGhostActionStyle: React.CSSProperties = {
  borderRadius: 999,
  border: '1px solid #E5E1DA',
  background: '#FFFFFF',
  color: '#374151',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowSolidActionStyle: React.CSSProperties = {
  borderRadius: 999,
  border: '1px solid #2F6FEC',
  background: '#2F6FEC',
  color: '#FFFFFF',
  boxShadow: '0 14px 32px rgba(47, 111, 236, 0.24)',
};

const workflowResultsBodyStyle: React.CSSProperties = {
  flex: '1 1 0',
  height: 0,
  minHeight: 0,
  display: 'flex',
  flexDirection: 'column',
  overflowX: 'hidden',
  overflowY: 'auto',
  padding: 24,
};

const workflowCardButtonBaseStyle: React.CSSProperties = {
  width: '100%',
  border: '1px solid #EEEAE4',
  borderRadius: 24,
  cursor: 'pointer',
  background: '#FFFFFF',
  textAlign: 'left',
  padding: 20,
  display: 'block',
  transition:
    'background-color 0.2s ease, border-color 0.2s ease, box-shadow 0.2s ease',
  boxShadow: 'none',
};

const workflowCardButtonListStyle: React.CSSProperties = {
  ...workflowCardButtonBaseStyle,
  padding: '16px 20px',
};

const workflowCardButtonActiveStyle: React.CSSProperties = {
  borderColor: '#B8D2FF',
  background: '#EEF5FF',
  boxShadow: '0 14px 32px rgba(47, 111, 236, 0.14)',
};

const workflowCardRowStyle: React.CSSProperties = {
  display: 'flex',
  gap: 16,
  width: '100%',
};

const workflowCardListRowStyle: React.CSSProperties = {
  ...workflowCardRowStyle,
  alignItems: 'center',
};

const workflowCardIconStyle: React.CSSProperties = {
  width: 48,
  height: 48,
  borderRadius: 16,
  background: '#F3F0EA',
  color: 'var(--ant-color-text-tertiary)',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  flexShrink: 0,
};

const workflowCardIconActiveStyle: React.CSSProperties = {
  background: '#EAF2FF',
  color: '#2F6FEC',
};

const workflowCardTitleStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 15,
  fontWeight: 600,
  color: '#1F2937',
};

const workflowCardMetaLineStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '4px 12px',
  marginTop: 4,
  fontSize: 12,
  lineHeight: 1.6,
  color: 'var(--ant-color-text-tertiary)',
};

const workflowCardDescriptionStyle: React.CSSProperties = {
  margin: '12px 0 0',
  fontSize: 12,
  lineHeight: 1.6,
  color: '#6B7280',
  display: '-webkit-box',
  WebkitBoxOrient: 'vertical',
  WebkitLineClamp: 2,
  overflow: 'hidden',
};

const workflowCardDescriptionCompactStyle: React.CSSProperties = {
  margin: '8px 0 0',
  fontSize: 12,
  lineHeight: 1.6,
  color: '#6B7280',
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
};

const workflowOpenHintStyle: React.CSSProperties = {
  display: 'none',
  flexShrink: 0,
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: '0.16em',
  textTransform: 'uppercase',
  color: '#D1D5DB',
};

const workflowEmptyStateStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
  width: '100%',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
};

const workflowEmptyStateInnerStyle: React.CSSProperties = {
  maxWidth: 420,
  width: '100%',
};

const workflowDirectoryContentStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
};

const workflowDirectoryListStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
  overflowY: 'auto',
};

const workflowResultsViewportStyle: React.CSSProperties = {
  width: '100%',
};

const studioTitleBarStyle: React.CSSProperties = {
  minWidth: 0,
  flex: '1 1 420px',
  maxWidth: '100%',
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  minHeight: 40,
  padding: '0 6px 0 12px',
  border: '1px solid #E8E4DD',
  borderRadius: 12,
  background: '#FFFFFF',
  boxShadow: 'none',
};

const studioTitleGroupStyle: React.CSSProperties = {
  minWidth: 0,
  flex: '1 1 auto',
  display: 'flex',
  alignItems: 'center',
  gap: 10,
};

const studioTitleInputStyle: React.CSSProperties = {
  minWidth: 0,
  width: '100%',
  background: 'transparent',
  border: 0,
  outline: 'none',
  fontSize: 15,
  fontWeight: 700,
  letterSpacing: '-0.02em',
  lineHeight: 1.2,
  color: '#1F2937',
};

const studioInfoPopoverStyle: React.CSSProperties = {
  position: 'relative',
  display: 'inline-flex',
  zIndex: 40,
};

const studioInfoPopoverButtonStyle: React.CSSProperties = {
  width: 20,
  height: 20,
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  borderRadius: 999,
  border: 0,
  background: 'transparent',
  color: 'var(--ant-color-text-tertiary)',
  cursor: 'pointer',
  transition: 'background 0.18s ease, color 0.18s ease',
};

const studioInfoPopoverCardStyle: React.CSSProperties = {
  position: 'fixed',
  zIndex: 300,
  width: 320,
  border: '1px solid #E8E4DD',
  borderRadius: 16,
  background: 'rgba(255,255,255,0.98)',
  boxShadow: '0 18px 40px rgba(17, 24, 39, 0.08)',
  backdropFilter: 'blur(12px)',
  padding: 14,
};

const studioDescriptionEditorStyle: React.CSSProperties = {
  width: '100%',
  minHeight: 168,
  resize: 'vertical',
  border: '1px solid #E7E3DC',
  borderRadius: 12,
  background: '#FBFAF8',
  color: '#24211F',
  outline: 'none',
  padding: '12px 14px',
  fontSize: 15,
  lineHeight: 1.65,
  fontFamily:
    "'Inter', system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif",
};

const studioInfoPopoverActionsStyle: React.CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
  gap: 8,
  marginTop: 12,
};

const studioNoticeStripStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
};

const studioNoticeCardStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const studioNoticeCardHeaderStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  gap: 10,
};

const studioNoticeCardHeadingStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: '1 1 240px',
  minWidth: 0,
};

const studioNoticeCardActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
};

const studioNoticeCardInlineActionsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-start',
};

const studioNoticeCardCollapsedCopyStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-tertiary)',
  display: '-webkit-box',
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  overflow: 'hidden',
  WebkitBoxOrient: 'vertical',
  WebkitLineClamp: 1,
};

const studioSurfaceStyle: React.CSSProperties = {
  background: 'var(--ant-color-bg-container)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 12,
  boxShadow: '0 6px 18px rgba(15, 23, 42, 0.06)',
  display: 'flex',
  flexDirection: 'column',
  flex: 1,
  height: '100%',
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
  width: '100%',
};

const studioSurfaceHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  borderBottom: '1px solid var(--ant-color-border-secondary)',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 12,
  justifyContent: 'space-between',
  minWidth: 0,
  padding: '14px 16px',
};

const studioSurfaceBodyStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: 1,
  gap: 16,
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
  padding: 16,
};

const studioSettingsTabsStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  height: '100%',
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
  width: '100%',
};

const studioSettingsTabsClassName = 'studio-settings-tabs';
const studioSettingsTabContentClassName = 'studio-settings-tab-content';
const studioSettingsTabsCss = `
.${studioSettingsTabsClassName} {
  align-items: stretch;
  display: flex;
  flex: 1;
  height: 100%;
  min-height: 0;
  min-width: 0;
  overflow: hidden;
}

.${studioSettingsTabsClassName} .ant-tabs-content-holder {
  display: flex;
  flex: 1;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.${studioSettingsTabsClassName} .ant-tabs-content {
  display: flex;
  flex: 1;
  height: 100%;
  min-height: 0;
}

.${studioSettingsTabsClassName} .ant-tabs-tabpane-hidden {
  display: none !important;
}

.${studioSettingsTabsClassName} .ant-tabs-tabpane-active {
  display: flex !important;
  flex: 1;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.${studioSettingsTabContentClassName} {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
  overflow-x: hidden;
  overflow-y: auto;
  padding-right: 4px;
}
`;

const studioSettingsTabLabelStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 8,
  minWidth: 0,
};

const studioSettingsTabContentStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
  minWidth: 0,
};

const studioStatusStripStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
};

const studioStatusPillStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: 'rgba(255, 255, 255, 0.92)',
  border: '1px solid #E8E2D9',
  borderRadius: 18,
  boxShadow: '0 10px 24px rgba(17, 24, 39, 0.08)',
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minWidth: 0,
  padding: '10px 12px',
};

const studioCanvasViewportStyle: React.CSSProperties = {
  background: '#F2F1EE',
  borderRadius: 24,
  display: 'flex',
  flex: '1 1 0',
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
  position: 'relative',
};

const studioEditorPageRootStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: '1 1 0',
  minHeight: 0,
};

const studioEditorShellStyle: React.CSSProperties = {
  background: '#F2F1EE',
  border: '1px solid #E6E3DE',
  borderRadius: 36,
  boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
  display: 'flex',
  flex: '1 1 0',
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
};

const studioDefinitionListScrollStyle: React.CSSProperties = {
  display: 'flex',
  flex: '1 1 0',
  flexDirection: 'column',
  height: 0,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};

const studioEditorHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255,255,255,0.96)',
  borderBottom: '1px solid #E8E2D9',
  display: 'flex',
  gap: 16,
  justifyContent: 'space-between',
  padding: '12px 16px',
};

const studioEditorToolbarStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
  minWidth: 0,
};

const studioCanvasEmptyStateStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#6B7280',
  display: 'flex',
  inset: 12,
  justifyContent: 'center',
  pointerEvents: 'none',
  position: 'absolute',
  textAlign: 'center',
  zIndex: 1,
};

const studioCanvasEmptyCardStyle: React.CSSProperties = {
  alignItems: 'center',
  backdropFilter: 'blur(10px)',
  background: 'rgba(255,255,255,0.94)',
  border: '1px solid #E8E2D9',
  borderRadius: 24,
  boxShadow: '0 18px 38px rgba(17, 24, 39, 0.08)',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  maxWidth: 380,
  padding: '24px 24px 20px',
  pointerEvents: 'auto',
};

const studioCanvasEmptyActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'center',
};

const studioInspectorEmptyStateStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  justifyContent: 'center',
  minHeight: 0,
  padding: 20,
  textAlign: 'center',
};

const studioInspectorQuickActionsStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  marginTop: 16,
};

const studioToolDrawerSectionStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

function getStudioNoticeAccent(
  type: StudioNoticeLike['type'] | 'default',
): { border: string; background: string; label: string } {
  switch (type) {
    case 'success':
      return {
        border: 'rgba(82, 196, 26, 0.28)',
        background: 'rgba(246, 255, 237, 0.96)',
        label: '成功',
      };
    case 'warning':
      return {
        border: 'rgba(250, 173, 20, 0.28)',
        background: 'rgba(255, 251, 230, 0.96)',
        label: '注意',
      };
    case 'error':
      return {
        border: 'rgba(255, 77, 79, 0.28)',
        background: 'rgba(255, 241, 240, 0.96)',
        label: '错误',
      };
    case 'info':
      return {
        border: 'rgba(22, 119, 255, 0.24)',
        background: 'rgba(240, 245, 255, 0.96)',
        label: '提示',
      };
    default:
      return {
        border: 'var(--ant-color-border-secondary)',
        background: 'var(--ant-color-fill-quaternary)',
        label: '状态',
      };
  }
}

type StudioNoticeCardProps = {
  readonly type?: StudioNoticeLike['type'] | 'default';
  readonly title: React.ReactNode;
  readonly description?: React.ReactNode;
  readonly action?: React.ReactNode;
  readonly compact?: boolean;
  readonly defaultExpanded?: boolean;
  readonly expandLabel?: string;
  readonly collapseLabel?: string;
};

const StudioNoticeCard: React.FC<StudioNoticeCardProps> = ({
  type = 'default',
  title,
  description,
  action,
  compact = false,
  defaultExpanded,
  expandLabel = '查看详情',
  collapseLabel = '收起详情',
}) => {
  const accent = getStudioNoticeAccent(type);
  const canToggleDescription =
    compact && typeof description === 'string' && description.trim().length > 0;
  const [expanded, setExpanded] = React.useState(
    defaultExpanded ?? type === 'error',
  );
  const showExpandedDescription = !compact || !canToggleDescription || expanded;

  return (
    <div
      style={{
        ...studioNoticeCardStyle,
        background: accent.background,
        borderColor: accent.border,
      }}
    >
      {compact ? (
        <>
          <div style={studioNoticeCardHeaderStyle}>
            <div style={studioNoticeCardHeadingStyle}>
              <Space wrap size={[8, 8]}>
                <Tag color={type === 'default' ? 'default' : type}>{accent.label}</Tag>
                <Typography.Text strong>{title}</Typography.Text>
              </Space>
              {canToggleDescription && !expanded ? (
                <Typography.Text style={studioNoticeCardCollapsedCopyStyle}>
                  {description}
                </Typography.Text>
              ) : null}
            </div>
            {action || canToggleDescription ? (
              <div style={studioNoticeCardActionsStyle}>
                {action}
                {canToggleDescription ? (
                  <Button
                    type="text"
                    size="small"
                    onClick={() => setExpanded((current) => !current)}
                    aria-expanded={expanded}
                    icon={
                      <DownOutlined
                        style={{
                          transform: expanded ? 'rotate(180deg)' : 'rotate(0deg)',
                          transition: 'transform 0.2s ease',
                        }}
                      />
                    }
                  >
                    {expanded ? collapseLabel : expandLabel}
                  </Button>
                ) : null}
              </div>
            ) : null}
          </div>
          {description && showExpandedDescription ? (
            typeof description === 'string' ? (
              <Typography.Paragraph style={{ margin: 0 }} type="secondary">
                {description}
              </Typography.Paragraph>
            ) : (
              description
            )
          ) : null}
        </>
      ) : (
        <>
          <Space wrap size={[8, 8]}>
            <Tag color={type === 'default' ? 'default' : type}>{accent.label}</Tag>
            <Typography.Text strong>{title}</Typography.Text>
          </Space>
          {description ? (
            typeof description === 'string' ? (
              <Typography.Paragraph style={{ margin: 0 }} type="secondary">
                {description}
              </Typography.Paragraph>
            ) : (
              description
            )
          ) : null}
          {action ? (
            <div style={studioNoticeCardInlineActionsStyle}>{action}</div>
          ) : null}
        </>
      )}
    </div>
  );
};

type StudioSummaryFieldProps = {
  readonly copyable?: boolean;
  readonly label: string;
  readonly value: React.ReactNode;
};

type StudioSummaryMetricProps = {
  readonly label: string;
  readonly tone?: 'default' | 'info' | 'success' | 'warning' | 'error';
  readonly value: React.ReactNode;
};

const studioSummaryMetricToneMap: Record<
  NonNullable<StudioSummaryMetricProps['tone']>,
  { color: string }
> = {
  default: { color: 'var(--ant-color-text)' },
  error: { color: 'var(--ant-color-error)' },
  info: { color: 'var(--ant-color-primary)' },
  success: { color: 'var(--ant-color-success)' },
  warning: { color: 'var(--ant-color-warning)' },
};

function renderStudioSummaryValue(
  value: React.ReactNode,
  copyable?: boolean,
): React.ReactNode {
  if (typeof value === 'string') {
    if (!value) {
      return <Typography.Text type="secondary">n/a</Typography.Text>;
    }

    return copyable ? (
      <Typography.Text copyable>{value}</Typography.Text>
    ) : (
      <Typography.Text>{value}</Typography.Text>
    );
  }

  if (typeof value === 'number') {
    return <Typography.Text>{value}</Typography.Text>;
  }

  return value;
}

const StudioSummaryField: React.FC<StudioSummaryFieldProps> = ({
  copyable,
  label,
  value,
}) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {renderStudioSummaryValue(value, copyable)}
  </div>
);

const StudioSummaryMetric: React.FC<StudioSummaryMetricProps> = ({
  label,
  tone = 'default',
  value,
}) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        color: studioSummaryMetricToneMap[tone].color,
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

function getScopeBindingRevisionTone(
  revision: StudioScopeBindingStatus['revisions'][number],
): 'default' | 'success' | 'warning' | 'error' | 'info' {
  const normalizedStatus = revision.status.trim().toLowerCase();
  if (normalizedStatus === 'retired' || normalizedStatus.includes('failed')) {
    return 'error';
  }

  if (revision.isDefaultServing || revision.isActiveServing) {
    return 'success';
  }

  if (revision.isServingTarget) {
    return 'info';
  }

  if (normalizedStatus === 'prepared') {
    return 'warning';
  }

  return 'default';
}

function canActivateScopeBindingRevision(
  binding: StudioScopeBindingStatus | undefined,
  revision: StudioScopeBindingStatus['revisions'][number],
): boolean {
  if (!binding?.available) {
    return false;
  }

  if (revision.revisionId === binding.defaultServingRevisionId) {
    return false;
  }

  const normalizedStatus = revision.status.trim().toLowerCase();
  return normalizedStatus === 'published' || normalizedStatus === 'prepared';
}

function canRetireScopeBindingRevision(
  binding: StudioScopeBindingStatus | undefined,
  revision: StudioScopeBindingStatus['revisions'][number],
): boolean {
  if (!binding?.available || revision.retiredAt) {
    return false;
  }

  return revision.revisionId !== binding.defaultServingRevisionId;
}

function formatScopeBindingRevisionTimestamp(
  revision: StudioScopeBindingStatus['revisions'][number],
): string {
  return (
    formatDateTime(revision.publishedAt) ||
    formatDateTime(revision.preparedAt) ||
    formatDateTime(revision.createdAt) ||
    'n/a'
  );
}

type StudioScopeBindingPanelProps = {
  readonly scopeId?: string;
  readonly binding?: StudioScopeBindingStatus;
  readonly loading: boolean;
  readonly error: unknown;
  readonly pendingRevisionId: string;
  readonly pendingRetirementRevisionId: string;
  readonly onOpenBinding?: () => void;
  readonly onActivateRevision: (revisionId: string) => void;
  readonly onRetireRevision: (revisionId: string) => void;
};

const StudioScopeBindingPanel: React.FC<StudioScopeBindingPanelProps> = ({
  scopeId,
  binding,
  loading,
  error,
  pendingRevisionId,
  pendingRetirementRevisionId,
  onOpenBinding,
  onActivateRevision,
  onRetireRevision,
}) => {
  if (!scopeId) {
    return null;
  }

  const revisions = binding?.revisions ?? [];
  const currentRevision = getStudioScopeBindingCurrentRevision(binding);
  const currentTarget = describeStudioScopeBindingRevisionTarget(currentRevision);
  const currentContext = describeStudioScopeBindingRevisionContext(currentRevision);
  const currentActor =
    currentRevision?.primaryActorId ||
    binding?.primaryActorId ||
    '';
  const [detailsOpen, setDetailsOpen] = React.useState(false);
  const bindingStateColor = loading
    ? 'processing'
    : error
      ? 'error'
      : binding?.available
        ? 'success'
        : 'default';
  const bindingStateLabel = loading
    ? '加载中'
    : error
      ? '不可用'
      : binding?.available
        ? '已发布'
        : '未发布';
  const bindingSummary = loading
    ? '正在读取当前团队入口状态。'
    : error
      ? '暂时无法读取当前团队入口。'
      : binding?.available
        ? `默认入口指向 ${currentTarget || binding.displayName || binding.serviceId}。`
        : '当前还没有发布默认入口。';
  const canShowBindingDetails = Boolean(binding?.available);
  const canInspectPublishedMembers = Boolean(
    currentRevision?.primaryActorId || currentRevision?.staticActorTypeName,
  );
  const detailsContent = loading ? (
    <StudioNoticeCard
      title="正在加载团队入口"
      description="正在读取当前发布版本和服务状态。"
      compact
    />
  ) : error ? (
    <StudioNoticeCard
      type="error"
      title="读取团队入口失败"
      description={describeError(error)}
      compact
      defaultExpanded
    />
  ) : !binding?.available ? (
    <StudioNoticeCard
      type="info"
      title="尚未发布默认入口"
      description="发布当前行为定义或脚本后，这里会显示团队默认入口。"
      compact
    />
  ) : (
    <div
      style={{
        display: 'grid',
        gap: 16,
        gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
      }}
    >
      <div
        style={{
          ...cardStackStyle,
          minWidth: 0,
        }}
      >
        <div
          style={{
            border: '1px solid #E6E3DE',
            borderRadius: 24,
            background: '#FFFFFF',
            display: 'flex',
            flexDirection: 'column',
            gap: 16,
            padding: 18,
          }}
        >
          <Space direction="vertical" size={2}>
            <Typography.Text strong>当前入口</Typography.Text>
            <Typography.Text type="secondary">
              当前发布版本、目标对象和服务状态。
            </Typography.Text>
          </Space>

          <div style={summaryMetricGridStyle}>
            <StudioSummaryMetric label="版本数" value={binding.revisions.length} />
            <StudioSummaryMetric
              label="入口类型"
              value={formatStudioScopeBindingImplementationKind(
                currentRevision?.implementationKind,
              )}
            />
            <StudioSummaryMetric label="当前目标" value={currentTarget} />
            <StudioSummaryMetric
              label="默认版本"
              tone="success"
              value={binding.defaultServingRevisionId || 'n/a'}
            />
            <StudioSummaryMetric
              label="生效版本"
              tone="info"
              value={binding.activeServingRevisionId || 'n/a'}
            />
            <StudioSummaryMetric
              label="部署状态"
              value={binding.deploymentStatus || 'n/a'}
            />
          </div>

          <div style={summaryFieldGridStyle}>
            <StudioSummaryField label="服务键" copyable value={binding.serviceKey} />
            <StudioSummaryField label="目标" value={currentTarget} />
            <StudioSummaryField
              label="目标说明"
              copyable
              value={currentContext || 'n/a'}
            />
            <StudioSummaryField
              label="主 Actor"
              copyable
              value={currentActor || 'n/a'}
            />
            <StudioSummaryField label="部署 ID" value={binding.deploymentId} />
            <StudioSummaryField
              label="更新时间"
              value={formatDateTime(binding.updatedAt)}
            />
          </div>
        </div>
      </div>

      <div
        style={{
          ...cardStackStyle,
          minWidth: 0,
        }}
      >
        <div
          style={{
            border: '1px solid #E6E3DE',
            borderRadius: 24,
            background: '#FFFFFF',
            display: 'flex',
            flexDirection: 'column',
            gap: 16,
            padding: 18,
          }}
        >
          <Space direction="vertical" size={2}>
            <Typography.Text strong>版本发布</Typography.Text>
            <Typography.Text type="secondary">
              查看已发布版本、切换默认版本，或下线旧版本。
            </Typography.Text>
          </Space>

          <div
            style={{
              ...cardStackStyle,
              maxHeight: 420,
              overflowY: 'auto',
            }}
          >
            {revisions.map((revision) => {
              const canActivate = canActivateScopeBindingRevision(
                binding,
                revision,
              );
              const canRetire = canRetireScopeBindingRevision(binding, revision);
              const revisionTarget = describeStudioScopeBindingRevisionTarget(revision);
              const revisionContext = describeStudioScopeBindingRevisionContext(revision);
              return (
                <div
                  key={revision.revisionId}
                  style={{
                    alignItems: 'flex-start',
                    border: '1px solid #E6E3DE',
                    borderRadius: 20,
                    background: '#FFFFFF',
                    display: 'flex',
                    gap: 16,
                    justifyContent: 'space-between',
                    padding: 16,
                  }}
                >
                  <div
                    style={{
                      ...cardStackStyle,
                      flex: 1,
                      minWidth: 0,
                    }}
                  >
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong copyable>
                        {revision.revisionId}
                      </Typography.Text>
                      <Tag
                        color={
                          getScopeBindingRevisionTone(revision) === 'default'
                            ? 'default'
                            : getScopeBindingRevisionTone(revision)
                        }
                      >
                        {revision.status || 'unknown'}
                      </Tag>
                      {revision.isDefaultServing ? (
                        <Tag color="success">默认</Tag>
                      ) : null}
                      {revision.isActiveServing ? (
                        <Tag color="processing">生效</Tag>
                      ) : null}
                      {revision.isServingTarget ? (
                        <Tag color="blue">
                          {revision.allocationWeight}% {revision.servingState || 'serving'}
                        </Tag>
                      ) : null}
                    </Space>
                    <Typography.Text type="secondary">
                      {formatStudioScopeBindingImplementationKind(
                        revision.implementationKind,
                      )}{' '}
                      · {revisionTarget} · updated{' '}
                      {formatScopeBindingRevisionTimestamp(revision)}
                    </Typography.Text>
                    {revisionContext ? (
                      <Typography.Text type="secondary">
                        {revisionContext}
                      </Typography.Text>
                    ) : null}
                    {revision.failureReason ? (
                      <Typography.Text type="danger">
                        {revision.failureReason}
                      </Typography.Text>
                    ) : null}
                  </div>

                  <Space direction="vertical" size={8} align="end">
                    <Typography.Text type="secondary">
                      {revision.deploymentId || 'draft only'}
                    </Typography.Text>
                    <Button
                      type={canActivate ? 'primary' : 'default'}
                      disabled={!canActivate}
                      loading={pendingRevisionId === revision.revisionId}
                      onClick={() => onActivateRevision(revision.revisionId)}
                    >
                      {revision.isDefaultServing ? '当前默认' : '设为默认'}
                    </Button>
                    <Button
                      danger
                      disabled={!canRetire}
                      loading={
                        pendingRetirementRevisionId === revision.revisionId
                      }
                      onClick={() => onRetireRevision(revision.revisionId)}
                    >
                      下线
                    </Button>
                  </Space>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );

  return (
    <div
      style={{
        background: 'rgba(255, 255, 255, 0.84)',
        border: '1px solid #E6E3DE',
        borderRadius: 20,
        boxShadow: '0 10px 24px rgba(17, 24, 39, 0.04)',
        display: 'flex',
        flexDirection: 'column',
        gap: detailsOpen ? 14 : 8,
        padding: detailsOpen ? 14 : 12,
      }}
    >
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 12,
          justifyContent: 'space-between',
        }}
      >
        <div
          style={{
            ...cardStackStyle,
            flex: '1 1 320px',
            gap: 6,
            minWidth: 0,
          }}
        >
          <Space wrap size={[8, 8]}>
            <Typography.Text style={workflowSectionHeadingStyle}>
              发布到团队
            </Typography.Text>
            <Typography.Text strong style={{ fontSize: 16, color: '#1F2937' }}>
              {binding?.available
                ? binding.displayName || binding.serviceId
                : '未发布默认入口'}
            </Typography.Text>
            <Tag color={bindingStateColor}>{bindingStateLabel}</Tag>
            {binding?.available ? (
              <Tag color="success">
                {binding.defaultServingRevisionId || 'default pending'}
              </Tag>
            ) : null}
            <code
              style={{
                color: '#8C8C8C',
                fontFamily: '"SF Mono", "JetBrains Mono", monospace',
                fontSize: 11,
              }}
            >
              {scopeId}
            </code>
          </Space>
          <Typography.Text
            type="secondary"
            style={{ display: 'block', fontSize: 12 }}
            ellipsis={{ tooltip: bindingSummary }}
          >
            {bindingSummary}
          </Typography.Text>
        </div>
        <Space wrap size={[8, 8]}>
          {onOpenBinding ? (
            <Button
              size="small"
              type="default"
              icon={<SafetyCertificateOutlined />}
              onClick={onOpenBinding}
            >
              {binding?.available ? '更新团队入口' : '绑定团队入口'}
            </Button>
          ) : null}
          {canShowBindingDetails ? (
            <Button
              type="text"
              size="small"
              aria-expanded={detailsOpen}
              onClick={() => setDetailsOpen((current) => !current)}
              icon={
                <DownOutlined
                  style={{
                    transform: detailsOpen ? 'rotate(180deg)' : 'rotate(0deg)',
                    transition: 'transform 0.2s ease',
                  }}
                />
              }
            >
              {detailsOpen ? '收起详情' : '查看详情'}
            </Button>
          ) : null}
        </Space>
      </div>
      {canShowBindingDetails && detailsOpen ? (
        <div
          style={{
            borderTop: '1px solid #F1EEE8',
            display: 'grid',
            gap: 12,
            paddingTop: 12,
          }}
        >
          {canInspectPublishedMembers ? (
            <div
              style={{
                display: 'flex',
                justifyContent: 'flex-end',
              }}
            >
              <Button
                size="small"
                onClick={() =>
                  history.push(
                    buildRuntimeGAgentsHref({
                      scopeId,
                      actorId: currentRevision?.primaryActorId || undefined,
                      actorTypeName: currentRevision?.staticActorTypeName || undefined,
                    }),
                  )
                }
              >
                查看成员
              </Button>
            </div>
          ) : null}
          {detailsContent}
        </div>
      ) : null}
    </div>
  );
};

const studioPaletteCategoryIcons: Record<
  string,
  React.ComponentType<{ style?: React.CSSProperties }>
> = {
  ai: RobotOutlined,
  composition: AppstoreOutlined,
  control: ApartmentOutlined,
  data: DatabaseOutlined,
  human: UserOutlined,
  integration: ApiOutlined,
  validation: SafetyCertificateOutlined,
  custom: CodeOutlined,
};

type StudioInfoPopoverProps = {
  readonly open: boolean;
  readonly ariaLabel: string;
  readonly onOpenChange: (open: boolean) => void;
  readonly children: React.ReactNode;
};

const StudioInfoPopover: React.FC<StudioInfoPopoverProps> = ({
  open,
  ariaLabel,
  onOpenChange,
  children,
}) => {
  const popoverRef = React.useRef<HTMLDivElement | null>(null);
  const buttonRef = React.useRef<HTMLButtonElement | null>(null);
  const [popoverPosition, setPopoverPosition] = React.useState<{
    top: number;
    left: number;
    placement: 'top' | 'bottom';
  } | null>(null);

  React.useEffect(() => {
    if (!open) {
      return undefined;
    }

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (!(target instanceof globalThis.Node)) {
        return;
      }

      if (
        !popoverRef.current?.contains(target) &&
        !buttonRef.current?.contains(target)
      ) {
        onOpenChange(false);
      }
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onOpenChange(false);
      }
    };

    document.addEventListener('pointerdown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);

    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [onOpenChange, open]);

  React.useEffect(() => {
    if (!open) {
      setPopoverPosition(null);
      return undefined;
    }

    const updatePosition = () => {
      const rect = buttonRef.current?.getBoundingClientRect();
      if (!rect) {
        return;
      }

      const cardWidth = 320;
      const estimatedCardHeight = 280;
      const viewportWidth = window.innerWidth;
      const viewportHeight = window.innerHeight;
      const margin = 12;
      const left = Math.min(
        Math.max(margin, rect.right - cardWidth),
        viewportWidth - cardWidth - margin,
      );
      const openAbove =
        rect.bottom + 10 + estimatedCardHeight > viewportHeight - margin &&
        rect.top - 10 - estimatedCardHeight > margin;

      setPopoverPosition({
        top: openAbove ? rect.top - 10 : rect.bottom + 10,
        left,
        placement: openAbove ? 'top' : 'bottom',
      });
    };

    updatePosition();

    window.addEventListener('resize', updatePosition);
    window.addEventListener('scroll', updatePosition, true);

    return () => {
      window.removeEventListener('resize', updatePosition);
      window.removeEventListener('scroll', updatePosition, true);
    };
  }, [open]);

  return (
    <div style={studioInfoPopoverStyle}>
      <button
        ref={buttonRef}
        type="button"
        aria-label={ariaLabel}
        title={ariaLabel}
        onClick={(event) => {
          event.stopPropagation();
          onOpenChange(!open);
        }}
        style={{
          ...studioInfoPopoverButtonStyle,
          ...(open
            ? {
                background: 'rgba(148, 163, 184, 0.1)',
                color: '#6B7280',
              }
            : null),
        }}
      >
        <QuestionCircleOutlined />
      </button>
      {open && popoverPosition
        ? ReactDOM.createPortal(
            <div
              ref={popoverRef}
              style={{
                ...studioInfoPopoverCardStyle,
                top: popoverPosition.top,
                left: popoverPosition.left,
                transform:
                  popoverPosition.placement === 'top'
                    ? 'translateY(-100%)'
                    : undefined,
              }}
              onClick={(event) => event.stopPropagation()}
            >
              {children}
            </div>,
            document.body,
          )
        : null}
    </div>
  );
};

function parseListText(value: string): string[] {
  return value
    .split(/[\n,]/)
    .map((item) => item.trim())
    .filter(Boolean);
}

export type StudioWorkspaceAlertsProps = {
  readonly authSession: {
    readonly enabled: boolean;
    readonly authenticated: boolean;
    readonly errorMessage?: string;
    readonly loginUrl?: string;
  } | null | undefined;
  readonly templateWorkflow: string;
  readonly draftMode: DraftMode;
  readonly legacySource: LegacySource;
};

export const StudioWorkspaceAlerts: React.FC<StudioWorkspaceAlertsProps> = ({
  authSession,
  draftMode: _draftMode,
  legacySource: _legacySource,
}) => {
  const notices: React.ReactNode[] = [];

  if (authSession?.enabled && !authSession.authenticated) {
    notices.push(
      <StudioNoticeCard
        key="auth"
        type="warning"
        title="需要登录"
        description={
          <Typography.Paragraph style={{ margin: 0 }} type="secondary">
            {authSession.errorMessage || "登录后可继续访问团队构建器。"}
          </Typography.Paragraph>
        }
        action={
          authSession.loginUrl ? (
            <Button
              type="link"
              href={authSession.loginUrl}
              style={{ paddingInline: 0, alignSelf: 'flex-start' }}
            >
              去登录
            </Button>
          ) : undefined
        }
      />,
    );
  }

  if (notices.length === 0) {
    return null;
  }

  return <div style={studioNoticeStripStyle}>{notices}</div>;
};

export type StudioWorkflowsPageProps = {
  readonly workflows: QueryState<StudioWorkflowSummary[]>;
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly workflowStorageMode: string;
  readonly selectedWorkflowId: string;
  readonly selectedDirectoryId: string;
  readonly templateWorkflow: string;
  readonly draftMode: DraftMode;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeWorkflowSourceKey: string;
  readonly workflowSearch: string;
  readonly showDirectoryForm: boolean;
  readonly directoryPath: string;
  readonly directoryLabel: string;
  readonly workflowImportPending: boolean;
  readonly workflowImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly onOpenWorkflow: (workflowId: string) => void;
  readonly onStartBlankDraft: () => void;
  readonly onOpenCurrentDraft: () => void;
  readonly onSelectDirectoryId: (directoryId: string) => void;
  readonly onSetWorkflowSearch: (value: string) => void;
  readonly onToggleDirectoryForm: () => void;
  readonly onSetDirectoryPath: (value: string) => void;
  readonly onSetDirectoryLabel: (value: string) => void;
  readonly onAddDirectory: () => void;
  readonly onRemoveDirectory: (directoryId: string) => void;
  readonly onWorkflowImportClick: () => void;
  readonly onWorkflowImportChange: React.ChangeEventHandler<HTMLInputElement>;
};

export const StudioWorkflowsPage: React.FC<StudioWorkflowsPageProps> = ({
  workflows,
  workspaceSettings,
  workflowStorageMode,
  selectedWorkflowId,
  selectedDirectoryId,
  templateWorkflow,
  draftMode,
  activeWorkflowName,
  activeWorkflowDescription,
  activeWorkflowSourceKey,
  workflowSearch,
  showDirectoryForm,
  directoryPath,
  directoryLabel,
  workflowImportPending,
  workflowImportInputRef,
  onOpenWorkflow,
  onStartBlankDraft,
  onOpenCurrentDraft,
  onSelectDirectoryId,
  onSetWorkflowSearch,
  onToggleDirectoryForm,
  onSetDirectoryPath,
  onSetDirectoryLabel,
  onAddDirectory,
  onRemoveDirectory,
  onWorkflowImportClick,
  onWorkflowImportChange,
}) => {
  const directories = workspaceSettings.data?.directories ?? [];
  const isScopeMode = workflowStorageMode === 'scope';
  const visibleWorkflows = React.useMemo(
    () => dedupeStudioWorkflowSummaries(workflows.data ?? []),
    [workflows.data],
  );
  const activeDirectory =
    directories.find((directory) => directory.directoryId === selectedDirectoryId) ||
    directories[0] ||
    null;
  const filteredWorkflows = visibleWorkflows.filter((workflow) => {
    const keyword = workflowSearch.trim().toLowerCase();
    if (!keyword) {
      return true;
    }

    return [
      workflow.name,
      workflow.description,
      workflow.directoryLabel,
      workflow.fileName,
    ].some((value) => value.toLowerCase().includes(keyword));
  });
  const workflowViewportMaxWidth = isScopeMode ? undefined : 1080;
  const resolvedWorkspaceRowStyle: React.CSSProperties = isScopeMode
    ? {
        ...workflowWorkspaceRowStyle,
        width: '100%',
      }
    : workflowWorkspaceRowStyle;
  const resolvedWorkflowBrowserStyle: React.CSSProperties = workflowBrowserStyle;
  const resolvedWorkflowResultsBodyStyle: React.CSSProperties = workflowResultsBodyStyle;

  const scopeSummaryDescription = workspaceSettings.isLoading
    ? '正在解析团队上下文。'
    : workspaceSettings.isError
      ? describeError(workspaceSettings.error)
      : '';
  const draftSummaryDescription = activeWorkflowDescription || '';

  return (
    <Row gutter={[16, 16]} align="stretch" style={resolvedWorkspaceRowStyle}>
      {!isScopeMode ? (
        <Col xs={24} xl={8} xxl={7} style={workflowColumnStretchStyle}>
          <div style={workflowSidebarStackStyle}>
            <section
              style={{
                ...workflowSectionShellStyle,
                ...workflowSectionFillStyle,
              }}
            >
              <div style={workflowSectionHeaderStyle}>
                <div style={cardStackStyle}>
                  <Typography.Text style={workflowSectionHeadingStyle}>
                    工作区目录
                  </Typography.Text>
                </div>
                <Button
                  type="text"
                  icon={<FolderAddOutlined />}
                  onClick={onToggleDirectoryForm}
                  aria-label="切换目录表单"
                  style={workflowPanelIconButtonStyle}
                />
              </div>

              {workspaceSettings.isLoading ? (
                <Typography.Text type="secondary">
                  正在加载目录...
                </Typography.Text>
              ) : workspaceSettings.isError ? (
                <StudioNoticeCard
                  type="error"
                  title="读取工作区设置失败"
                  description={describeError(workspaceSettings.error)}
                />
              ) : (
                <div style={workflowDirectoryContentStyle}>
                  {showDirectoryForm ? (
                    <div style={workflowSurfaceStyle}>
                      <div style={cardStackStyle}>
                        <Input
                          aria-label="Studio directory path"
                          placeholder="/absolute/path/to/workflows"
                          value={directoryPath}
                          onChange={(event) => onSetDirectoryPath(event.target.value)}
                        />
                        <Input
                          aria-label="Studio directory label"
                          placeholder="目录名称"
                          value={directoryLabel}
                          onChange={(event) => onSetDirectoryLabel(event.target.value)}
                        />
                        <Button type="primary" onClick={onAddDirectory}>
                          添加目录
                        </Button>
                      </div>
                    </div>
                  ) : null}

                  {directories.length > 0 ? (
                    <div style={workflowDirectoryListStyle}>
                      {directories.map((directory) => {
                        const active = selectedDirectoryId === directory.directoryId;
                        const showDirectoryPath = !isScopeDirectoryPath(directory.path);

                        return (
                          <div
                            key={directory.directoryId}
                            style={{
                              ...workflowSurfaceStyle,
                              ...(active ? workflowSurfaceActiveStyle : {}),
                            }}
                          >
                            <div
                              style={{
                                display: 'flex',
                                alignItems: 'flex-start',
                                gap: 12,
                                justifyContent: 'space-between',
                              }}
                            >
                              <Button
                                type="text"
                                style={workflowDirectorySelectButtonStyle}
                                onClick={() => onSelectDirectoryId(directory.directoryId)}
                              >
                                <div style={workflowDirectoryTextStackStyle}>
                                  <Typography.Text
                                    style={workflowDirectoryLabelStyle}
                                    ellipsis={{ tooltip: directory.label }}
                                  >
                                    {directory.label}
                                  </Typography.Text>
                                  {showDirectoryPath ? (
                                    <Typography.Text
                                      style={workflowDirectoryPathStyle}
                                      ellipsis={{ tooltip: directory.path }}
                                    >
                                      {directory.path}
                                    </Typography.Text>
                                  ) : null}
                                </div>
                              </Button>
                              {!directory.isBuiltIn ? (
                                <Button
                                  type="text"
                                  icon={<DeleteOutlined />}
                                  aria-label={`Remove workflow directory ${directory.label}`}
                                  onClick={() => onRemoveDirectory(directory.directoryId)}
                                  style={{
                                    ...workflowPanelIconButtonStyle,
                                    width: 32,
                                    height: 32,
                                    color: 'var(--ant-color-text-tertiary)',
                                  }}
                                >
                                  <span style={{ display: 'none' }}>Remove</span>
                                </Button>
                              ) : null}
                            </div>
                          </div>
                        );
                      })}
                    </div>
                  ) : (
                    <div style={workflowEmptyStateStyle}>
                      <div style={workflowEmptyStateInnerStyle}>
                        <Empty
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                          description="还没有可用目录。"
                        />
                      </div>
                    </div>
                  )}
                </div>
              )}
            </section>

            <section style={workflowSectionShellStyle}>
              <div style={workflowSectionHeaderStyle}>
                <div style={cardStackStyle}>
                  <Typography.Text style={workflowSectionHeadingStyle}>
                    当前定义
                  </Typography.Text>
                </div>
              </div>

              <div style={workflowSurfaceStyle}>
                <div style={cardStackStyle}>
                  <Space wrap size={[8, 8]}>
                    {selectedWorkflowId ? (
                      <Tag color="processing">工作区定义</Tag>
                    ) : null}
                    {templateWorkflow ? (
                      <Tag color="success">模板定义</Tag>
                    ) : null}
                    {draftMode === 'new' ? <Tag color="gold">新建草稿</Tag> : null}
                  </Space>
                  <Typography.Text strong>
                    {activeWorkflowName || '尚未选择定义'}
                  </Typography.Text>
                  {activeWorkflowDescription ? (
                    <Typography.Text style={workflowSectionCopyStyle}>
                      {activeWorkflowDescription}
                    </Typography.Text>
                  ) : null}
                  <Space wrap size={[8, 8]}>
                    <Button
                      type="primary"
                      disabled={!activeWorkflowSourceKey}
                      onClick={onOpenCurrentDraft}
                      style={activeWorkflowSourceKey ? workflowSolidActionStyle : undefined}
                    >
                      进入编辑
                    </Button>
                    <Button onClick={onStartBlankDraft} style={workflowGhostActionStyle}>
                      新建定义
                    </Button>
                  </Space>
                </div>
              </div>
            </section>
          </div>
        </Col>
      ) : null}

      <Col
        xs={24}
        xl={isScopeMode ? 24 : 16}
        xxl={isScopeMode ? 24 : 17}
        style={workflowColumnStretchStyle}
      >
        <section style={resolvedWorkflowBrowserStyle}>
          <input
            ref={workflowImportInputRef}
            hidden
            accept=".yaml,.yml,text/yaml,text/x-yaml"
            type="file"
            onChange={onWorkflowImportChange}
          />

          <div style={workflowToolbarSurfaceStyle}>
            <div style={isScopeMode ? workflowToolbarStackStyle : undefined}>
              {isScopeMode ? (
                <div style={workflowSummaryStripStyle}>
                  <div
                    style={{
                      ...workflowSummaryCardStyle,
                      ...(activeDirectory ? workflowSurfaceActiveStyle : {}),
                    }}
                  >
                    <div style={workflowSummaryCardRowStyle}>
                      <div style={workflowSummaryCardBodyStyle}>
                        <Typography.Text style={workflowSectionHeadingStyle}>
                          当前团队
                        </Typography.Text>
                        <Typography.Text strong style={workflowCardTitleStyle}>
                          {activeDirectory?.label || '当前团队'}
                        </Typography.Text>
                        {scopeSummaryDescription ? (
                          <Typography.Text style={workflowSummaryHintStyle}>
                            {scopeSummaryDescription}
                          </Typography.Text>
                        ) : null}
                      </div>
                    </div>
                  </div>

                  <div style={workflowSummaryCardStyle}>
                    <div style={workflowSummaryCardRowStyle}>
                      <div style={workflowSummaryCardBodyStyle}>
                        <Space wrap size={[8, 8]}>
                          {selectedWorkflowId ? (
                            <Tag color="processing">工作区定义</Tag>
                          ) : null}
                          {templateWorkflow ? (
                            <Tag color="success">模板定义</Tag>
                          ) : null}
                          {draftMode === 'new' ? (
                            <Tag color="gold">新建草稿</Tag>
                          ) : null}
                        </Space>
                        <Typography.Text style={workflowSectionHeadingStyle}>
                          当前定义
                        </Typography.Text>
                        <Typography.Text strong style={workflowCardTitleStyle}>
                          {activeWorkflowName || '尚未选择定义'}
                        </Typography.Text>
                        {draftSummaryDescription ? (
                          <Typography.Text style={workflowSummaryHintStyle}>
                            {draftSummaryDescription}
                          </Typography.Text>
                        ) : null}
                      </div>
                      <div style={workflowSummaryActionsStyle}>
                        <Button
                          type="primary"
                          size="small"
                          disabled={!activeWorkflowSourceKey}
                          onClick={onOpenCurrentDraft}
                          style={
                            activeWorkflowSourceKey ? workflowSolidActionStyle : undefined
                          }
                        >
                          进入编辑
                        </Button>
                        <Button
                          size="small"
                          onClick={onStartBlankDraft}
                          style={workflowGhostActionStyle}
                        >
                          新建定义
                        </Button>
                      </div>
                    </div>
                  </div>
                </div>
              ) : null}

              <div style={workflowToolbarLayoutStyle}>
                <div style={workflowSearchFieldStyle}>
                  <SearchOutlined
                    style={{ color: 'var(--ant-color-text-tertiary)' }}
                  />
                  <input
                    aria-label="搜索定义"
                    placeholder="搜索定义"
                    style={workflowSearchInputStyle}
                    value={workflowSearch}
                    onChange={(event) => onSetWorkflowSearch(event.target.value)}
                  />
                </div>

                <div style={workflowToolbarActionsStyle}>
                  <Button
                    icon={<UploadOutlined />}
                    loading={workflowImportPending}
                    onClick={onWorkflowImportClick}
                    style={workflowGhostActionStyle}
                  >
                    导入
                  </Button>
                  <Button
                    type="primary"
                    icon={<PlusOutlined />}
                    onClick={onStartBlankDraft}
                    style={workflowSolidActionStyle}
                  >
                    新建定义
                  </Button>
                </div>
              </div>
            </div>
          </div>

          <div
            data-testid="studio-workflows-results"
            style={resolvedWorkflowResultsBodyStyle}
          >
            {workflows.isLoading ? (
              <Typography.Text type="secondary">
                正在加载定义...
              </Typography.Text>
            ) : workflows.isError ? (
              <StudioNoticeCard
                type="error"
                title="读取行为定义失败"
                description={describeError(workflows.error)}
              />
            ) : filteredWorkflows.length > 0 ? (
              <div
                style={{
                  ...cardStackStyle,
                  ...workflowResultsViewportStyle,
                  ...(workflowViewportMaxWidth
                    ? { maxWidth: workflowViewportMaxWidth }
                    : null),
                }}
              >
                {filteredWorkflows.map((workflow) => {
                  const active = workflow.workflowId === selectedWorkflowId;
                  return (
                    <button
                      key={workflow.workflowId}
                      type="button"
                      onClick={() => onOpenWorkflow(workflow.workflowId)}
                      style={{
                        ...workflowCardButtonListStyle,
                        ...(active ? workflowCardButtonActiveStyle : {}),
                      }}
                    >
                      <div
                        style={{
                          ...workflowCardListRowStyle,
                          justifyContent: 'space-between',
                        }}
                      >
                        <div
                          style={{
                            ...workflowCardListRowStyle,
                            minWidth: 0,
                          }}
                        >
                          <div
                            style={{
                              ...workflowCardIconStyle,
                              ...(active ? workflowCardIconActiveStyle : {}),
                              width: 44,
                              height: 44,
                            }}
                          >
                            <FileTextOutlined />
                          </div>
                          <div style={{ minWidth: 0 }}>
                            <Typography.Text style={workflowCardTitleStyle}>
                              {workflow.name}
                            </Typography.Text>
                            <div style={workflowCardMetaLineStyle}>
                              <span>{workflow.directoryLabel}</span>
                              <span>{workflow.stepCount} 步骤</span>
                              <span>{formatDateTime(workflow.updatedAtUtc)}</span>
                            </div>
                            <Typography.Paragraph
                              style={workflowCardDescriptionCompactStyle}
                            >
                              {workflow.description || '暂未填写说明'}
                            </Typography.Paragraph>
                          </div>
                        </div>
                        <span
                          style={{
                            ...workflowOpenHintStyle,
                            display: 'block',
                          }}
                        >
                          打开
                        </span>
                      </div>
                    </button>
                  );
                })}
              </div>
            ) : (
              <div style={workflowEmptyStateStyle}>
                <div style={workflowEmptyStateInnerStyle}>
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description={
                      workflowSearch.trim()
                        ? '没有匹配的定义。'
                        : '还没有定义'
                    }
                  />
                </div>
              </div>
            )}
          </div>
        </section>
      </Col>
    </Row>
  );
};

export type StudioExecutionPageProps = {
  readonly executions: QueryState<StudioExecutionSummary[]>;
  readonly selectedExecution: QueryState<StudioExecutionDetail>;
  readonly workflowGraph: {
    readonly roles: StudioGraphRole[];
    readonly steps: StudioGraphStep[];
    readonly nodes: Node<StudioGraphNodeData>[];
    readonly edges: Edge<StudioGraphEdgeData>[];
  };
  readonly draftWorkflowName: string;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeDirectoryLabel: string;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly runPending: boolean;
  readonly canOpenRunWorkflow: boolean;
  readonly canRunWorkflow: boolean;
  readonly executionCanStop: boolean;
  readonly executionStopPending: boolean;
  readonly runPrompt: string;
  readonly executionNotice: StudioNoticeLike | null;
  readonly logsPopoutMode?: boolean;
  readonly logsDetached?: boolean;
  readonly onSwitchStudioView: (view: 'editor' | 'execution') => void;
  readonly onOpenExecution: (executionId: string) => void;
  readonly onSaveDraft: () => void;
  readonly onExportDraft: () => void;
  readonly onSetDraftWorkflowName: (value: string) => void;
  readonly onSetWorkflowDescription: (value: string) => void;
  readonly onRunPromptChange: (value: string) => void;
  readonly onStartExecution: () => void;
  readonly onResumeExecution: (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject',
    userInput: string,
  ) => Promise<void>;
  readonly onStopExecution: () => void;
  readonly onPopOutLogs?: () => void;
};

export const StudioExecutionPage: React.FC<StudioExecutionPageProps> = ({
  executions,
  selectedExecution,
  workflowGraph,
  draftWorkflowName,
  activeWorkflowName,
  runPending,
  canRunWorkflow,
  executionCanStop,
  executionStopPending,
  runPrompt,
  executionNotice,
  logsPopoutMode = false,
  logsDetached = false,
  onOpenExecution,
  onRunPromptChange,
  onStartExecution,
  onResumeExecution,
  onStopExecution,
  onPopOutLogs,
}) => {
  const clampExecutionLogsHeight = React.useCallback((value: number) => {
    const minHeight = 220;
    const maxHeight =
      typeof window === 'undefined'
        ? 520
        : Math.max(minHeight, Math.min(520, window.innerHeight - 240));
    return Math.min(Math.max(value, minHeight), maxHeight);
  }, []);
  const [logsCollapsed, setLogsCollapsed] = React.useState(false);
  const [logsHeight, setLogsHeight] = React.useState(264);
  const [logsResizing, setLogsResizing] = React.useState(false);
  const [activeExecutionLogIndex, setActiveExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedExecutionLogIndex, setCopiedExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedAllExecutionLogs, setCopiedAllExecutionLogs] = React.useState(false);
  const [copiedExecutionActorId, setCopiedExecutionActorId] =
    React.useState<string | null>(null);
  const [executionActionInput, setExecutionActionInput] = React.useState('');
  const [executionActionPendingKey, setExecutionActionPendingKey] =
    React.useState('');
  const [runModalOpen, setRunModalOpen] = React.useState(false);
  const logsResizeSessionRef = React.useRef<{
    readonly clientY: number;
    readonly height: number;
  } | null>(null);

  const selectedExecutionDetail = selectedExecution.data;
  const executionTrace = React.useMemo(
    () => buildExecutionTrace(selectedExecutionDetail),
    [selectedExecutionDetail],
  );

  React.useEffect(() => {
    setActiveExecutionLogIndex(executionTrace?.defaultLogIndex ?? null);
    setExecutionActionInput('');
    setExecutionActionPendingKey('');
  }, [executionTrace, selectedExecutionDetail?.executionId]);

  React.useEffect(() => {
    if (!logsResizing || typeof window === 'undefined') {
      return undefined;
    }

    const handleMouseMove = (event: MouseEvent) => {
      const session = logsResizeSessionRef.current;
      if (!session) {
        return;
      }

      const delta = session.clientY - event.clientY;
      setLogsHeight(clampExecutionLogsHeight(session.height + delta));
    };

    const handleMouseUp = () => {
      logsResizeSessionRef.current = null;
      setLogsResizing(false);
    };

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
    document.body.style.cursor = 'ns-resize';
    document.body.style.userSelect = 'none';

    return () => {
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
  }, [clampExecutionLogsHeight, logsResizing]);

  const currentWorkflowExecutions = React.useMemo(() => {
    const workflowName = activeWorkflowName.trim().toLowerCase();
    const items = executions.data ?? [];
    if (!workflowName) {
      return items;
    }

    return items.filter(
      (item) => item.workflowName.trim().toLowerCase() === workflowName,
    );
  }, [activeWorkflowName, executions.data]);

  const executionSummaryLabel = selectedExecutionDetail
    ? `${formatDateTime(selectedExecutionDetail.startedAtUtc)} · ${selectedExecutionDetail.status}`
    : currentWorkflowExecutions.length > 0
      ? `${currentWorkflowExecutions.length} 次运行`
      : '暂无运行';
  const selectedExecutionActorId = selectedExecutionDetail?.actorId || null;
  const activeExecutionLog =
    executionTrace && Number.isInteger(activeExecutionLogIndex)
      ? executionTrace.logs[activeExecutionLogIndex as number] || null
      : null;
  const activeExecutionInteraction =
    activeExecutionLog?.interaction &&
    activeExecutionLog.stepId &&
    executionTrace?.stepStates.get(activeExecutionLog.stepId)?.status === 'waiting'
      ? activeExecutionLog.interaction
      : null;
  const executionActionKeyBase =
    selectedExecutionDetail?.executionId && activeExecutionInteraction
      ? `${selectedExecutionDetail.executionId}:${activeExecutionInteraction.stepId}`
      : '';
  const decoratedExecutionNodes = React.useMemo(
    () =>
      decorateNodesForExecution(
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.nodes],
  );
  const decoratedExecutionEdges = React.useMemo(
    () =>
      decorateEdgesForExecution(
        workflowGraph.edges,
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.edges, workflowGraph.nodes],
  );
  const executionLogCount = executionTrace?.logs.length ?? 0;
  const executionExecutedSteps = React.useMemo(
    () =>
      new Set(
        (executionTrace?.logs ?? [])
          .map((log) => log.stepId || '')
          .filter(Boolean),
      ).size,
    [executionTrace],
  );
  const executionTotalSteps =
    workflowGraph.steps.length || workflowGraph.nodes.length;
  const executionStatusKey = String(selectedExecutionDetail?.status || '')
    .trim()
    .toLowerCase();
  const executionStatusLabel =
    executionStatusKey === 'running'
      ? '运行中'
      : executionStatusKey === 'completed'
        ? '已完成'
        : executionStatusKey === 'failed'
          ? '执行失败'
          : selectedExecutionDetail
            ? '等待执行'
            : '未开始';
  const executionAccentColor =
    executionStatusKey === 'running'
      ? '#1890ff'
      : executionStatusKey === 'completed'
        ? '#52c41a'
        : executionStatusKey === 'failed'
          ? '#ff4d4f'
          : '#8c8c8c';
  const executionBarStyle: React.CSSProperties =
    executionStatusKey === 'running'
      ? {
          background: '#e6f7ff',
          borderBottom: '1px solid #91d5ff',
        }
      : executionStatusKey === 'completed'
        ? {
            background: '#f6ffed',
            borderBottom: '1px solid #b7eb8f',
          }
        : executionStatusKey === 'failed'
          ? {
              background: '#fff2f0',
              borderBottom: '1px solid #ffccc7',
            }
          : {
              background: '#fafafa',
              borderBottom: '1px solid #f0f0f0',
            };
  const executionPromptPreview = (selectedExecutionDetail?.prompt || runPrompt).trim();
  const executionDurationLabel = selectedExecutionDetail
    ? formatDurationBetween(
        selectedExecutionDetail.startedAtUtc,
        selectedExecutionDetail.completedAtUtc,
      )
    : '';

  const copyText = async (value: string): Promise<boolean> => {
    if (!value || typeof navigator === 'undefined' || !navigator.clipboard) {
      return false;
    }

    await navigator.clipboard.writeText(value);
    return true;
  };

  const showExecutionLogCopyFeedback = (mode: 'single' | 'all', index?: number) => {
    setCopiedExecutionLogIndex(mode === 'single' ? index ?? null : null);
    setCopiedAllExecutionLogs(mode === 'all');
    window.setTimeout(() => {
      setCopiedExecutionLogIndex(null);
      setCopiedAllExecutionLogs(false);
    }, 1600);
  };

  const handleExecutionLogClick = async (
    log: NonNullable<typeof executionTrace>['logs'][number],
    index: number,
  ) => {
    setActiveExecutionLogIndex(index);
    const copied = await copyText(formatExecutionLogClipboard(log));
    if (copied) {
      showExecutionLogCopyFeedback('single', index);
    }
  };

  const handleCopyAllExecutionLogs = async () => {
    const copied = await copyText(formatExecutionLogsClipboard(executionTrace));
    if (copied) {
      showExecutionLogCopyFeedback('all');
    }
  };

  const handleCopyExecutionActorId = async (actorId: string) => {
    const copied = await copyText(actorId);
    if (!copied) {
      return;
    }

    setCopiedExecutionActorId(actorId);
    window.setTimeout(() => {
      setCopiedExecutionActorId((current) =>
        current === actorId ? null : current,
      );
    }, 1600);
  };

  const handleExecutionInteraction = async (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject',
  ) => {
    const trimmedInput = executionActionInput.trim();
    if (interaction.kind === 'human_input' && !trimmedInput) {
      return;
    }

    const pendingKey = `${executionActionKeyBase}:${action}`;
    setExecutionActionPendingKey(pendingKey);

    try {
      await onResumeExecution(interaction, action, trimmedInput);
      setExecutionActionInput('');
      setLogsCollapsed(false);
    } finally {
      setExecutionActionPendingKey('');
    }
  };

  const renderExecutionLogsSection = ({
    fullscreen = false,
    overlay = false,
  }: {
    readonly fullscreen?: boolean;
    readonly overlay?: boolean;
  } = {}) => {
    const sectionCollapsed = !fullscreen && logsCollapsed;
    const popoutButtonVisible =
      !fullscreen && Boolean(selectedExecutionDetail?.executionId);
    const sectionClassName = [
      'execution-logs',
      sectionCollapsed ? 'collapsed' : '',
      fullscreen ? 'execution-logs-fullscreen' : '',
      overlay ? 'execution-logs-overlay' : '',
    ]
      .filter(Boolean)
      .join(' ');
    const executionLogsToggleLabel = logsDetached
      ? 'Viewing in new window'
      : executionSummaryLabel;
    const overlayHeight = sectionCollapsed ? 56 : logsHeight;

    return (
      <section
        className={sectionClassName}
        style={overlay && !fullscreen ? { height: overlayHeight } : undefined}
      >
        {overlay && !fullscreen && !sectionCollapsed ? (
          <button
            type="button"
            className="execution-logs-resize-handle"
            aria-label="Resize execution logs"
            title="Drag to resize logs"
            onMouseDown={(event) => {
              event.preventDefault();
              logsResizeSessionRef.current = {
                clientY: event.clientY,
                height: logsHeight,
              };
              setLogsResizing(true);
            }}
          />
        ) : null}
        <div className="execution-logs-header">
          <div>
            <div
              style={{
                color: 'var(--ant-color-text-tertiary)',
                fontSize: 11,
                letterSpacing: '0.16em',
                textTransform: 'uppercase',
              }}
            >
              Execution
            </div>
            <div style={{ color: '#1F2937', fontSize: 14, fontWeight: 600 }}>
              {fullscreen ? 'Execution logs' : 'Logs'}
            </div>
          </div>
          <div className="execution-logs-header-actions">
            {executionTrace?.logs?.length ? (
              <button
                type="button"
                className={`panel-icon-button execution-logs-copy-action ${copiedAllExecutionLogs ? 'active' : ''}`}
                title="Copy all execution logs."
                aria-label="Copy all execution logs."
                onClick={() => void handleCopyAllExecutionLogs()}
              >
                {copiedAllExecutionLogs ? <CheckOutlined /> : <CopyOutlined />}
              </button>
            ) : null}
            {popoutButtonVisible ? (
              <button
                type="button"
                className={`panel-icon-button execution-logs-window-action ${logsDetached ? 'active' : ''}`}
                title={logsDetached ? 'Focus logs window.' : 'Pop out'}
                aria-label="Pop out execution logs."
                onClick={onPopOutLogs}
              >
                <ExpandOutlined />
              </button>
            ) : null}
            {fullscreen ? (
              <button
                type="button"
                className="panel-icon-button execution-logs-window-action"
                title="Close window."
                aria-label="Close logs window."
                onClick={() => {
                  if (typeof window !== 'undefined') {
                    window.close();
                  }
                }}
              >
                <CloseOutlined />
              </button>
            ) : (
              <button
                type="button"
                onClick={() => {
                  if (logsDetached) {
                    onPopOutLogs?.();
                    return;
                  }

                  setLogsCollapsed((current) => !current);
                }}
                className="execution-logs-collapse-action"
                aria-expanded={!sectionCollapsed}
              >
                <span style={{ color: '#6B7280', fontSize: 12 }}>
                  {executionLogsToggleLabel}
                </span>
                {logsDetached ? (
                  <ExpandOutlined />
                ) : (
                  <DownOutlined
                    className={`execution-logs-collapse-icon ${sectionCollapsed ? 'collapsed' : ''}`}
                  />
                )}
              </button>
            )}
          </div>
        </div>

        {!sectionCollapsed ? (
          <div
            className="execution-logs-body"
            style={
              activeExecutionInteraction
                ? { gridTemplateColumns: '280px minmax(0, 1fr) 340px' }
                : undefined
            }
          >
            <div className="execution-runs-list">
              {currentWorkflowExecutions.length === 0 ? (
                <StudioCatalogEmptyPanel
                  icon={<CaretRightFilled style={{ color: '#CBD5E1' }} />}
                  title="No runs"
                  copy="Run the current workflow to inspect execution."
                />
              ) : (
                currentWorkflowExecutions.map((execution) => (
                  <button
                    key={execution.executionId}
                    type="button"
                    onClick={() => onOpenExecution(execution.executionId)}
                    className={`execution-run-card ${selectedExecutionDetail?.executionId === execution.executionId ? 'active' : ''}`}
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 8,
                        justifyContent: 'space-between',
                      }}
                    >
                      <div style={{ color: '#1F2937', fontSize: 13, fontWeight: 600 }}>
                        {formatDateTime(execution.startedAtUtc)}
                      </div>
                      <span
                        style={{
                          color: 'var(--ant-color-text-tertiary)',
                          fontSize: 10,
                          letterSpacing: '0.08em',
                          textTransform: 'uppercase',
                        }}
                      >
                        {execution.status}
                      </span>
                    </div>
                    <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 11, marginTop: 4 }}>
                      {formatDurationBetween(
                        execution.startedAtUtc,
                        execution.completedAtUtc,
                      )}
                    </div>
                    {execution.actorId ? (
                      <div className="execution-run-card-actor">
                        <div className="execution-run-card-actor-label">Actor ID</div>
                        <code
                          className="execution-run-card-actor-value"
                          title={execution.actorId}
                        >
                          {execution.actorId}
                        </code>
                      </div>
                    ) : null}
                  </button>
                ))
              )}
            </div>

            <div className="execution-log-stream">
              <div className="execution-log-list">
                {selectedExecutionActorId ? (
                  <div className="execution-run-identity">
                    <div className="execution-run-identity-head">
                      <div className="execution-run-identity-label">Actor ID</div>
                      <button
                        type="button"
                        className={`panel-icon-button execution-logs-copy-action ${copiedExecutionActorId === selectedExecutionActorId ? 'active' : ''}`}
                        title="Copy Actor ID."
                        aria-label="Copy Actor ID."
                        onClick={() =>
                          void handleCopyExecutionActorId(
                            selectedExecutionActorId,
                          )
                        }
                      >
                        {copiedExecutionActorId === selectedExecutionActorId ? (
                          <CheckOutlined />
                        ) : (
                          <CopyOutlined />
                        )}
                      </button>
                    </div>
                    <code
                      className="execution-run-identity-value"
                      title={selectedExecutionActorId}
                    >
                      {selectedExecutionActorId}
                    </code>
                  </div>
                ) : null}
                {executionTrace?.logs?.length ? (
                  executionTrace.logs.map((log, index) => (
                    <button
                      key={`${log.timestamp}-${log.stepId || 'run'}-${log.title}`}
                      type="button"
                      onClick={() => void handleExecutionLogClick(log, index)}
                      className={`execution-log-card tone-${log.tone} ${activeExecutionLogIndex === index ? 'active' : ''}`}
                      title="Click to copy this log."
                    >
                      <div className="execution-log-card-head">
                        <div style={{ color: '#1F2937', fontSize: 12, fontWeight: 600 }}>
                          {log.title}
                        </div>
                        <div className="execution-log-card-meta">
                          {copiedExecutionLogIndex === index ? (
                            <span className="execution-log-card-copied">
                              <CheckOutlined /> Copied
                            </span>
                          ) : null}
                          <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 11 }}>
                            {formatDateTime(log.timestamp)}
                          </div>
                        </div>
                      </div>
                      {log.meta ? (
                        <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 11, marginTop: 4 }}>
                          {log.meta}
                        </div>
                      ) : null}
                      {log.previewText ? (
                        <div className="execution-log-card-preview">
                          {log.previewText}
                        </div>
                      ) : null}
                    </button>
                  ))
                ) : (
                  <StudioCatalogEmptyPanel
                    icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
                    title="No logs yet"
                    copy="Pick a run to inspect frames and step transitions."
                  />
                )}
              </div>

              {activeExecutionInteraction ? (
                <div className="execution-action-panel">
                  <div className="execution-action-intro">
                    <div
                      style={{
                        alignItems: 'flex-start',
                        display: 'flex',
                        gap: 12,
                        justifyContent: 'space-between',
                      }}
                    >
                      <div>
                        <div
                          style={{
                            color: 'var(--ant-color-text-tertiary)',
                            fontSize: 11,
                            letterSpacing: '0.14em',
                            textTransform: 'uppercase',
                          }}
                        >
                          Action required
                        </div>
                        <div
                          style={{
                            color: '#1F2937',
                            fontSize: 15,
                            fontWeight: 600,
                            marginTop: 4,
                          }}
                        >
                          {activeExecutionInteraction.kind === 'human_approval'
                            ? 'Human approval'
                            : 'Human input'}
                        </div>
                        <div className="execution-action-subtitle">
                          {activeExecutionInteraction.kind === 'human_approval'
                            ? 'Review the pending gate and approve or reject the run.'
                            : 'Provide the missing value to resume this workflow step.'}
                        </div>
                      </div>
                      <span className="execution-action-badge">
                        {activeExecutionInteraction.stepId}
                      </span>
                    </div>

                    <div className="execution-action-meta">
                      <span className="execution-action-chip">
                        <UserOutlined /> Human required
                      </span>
                      {activeExecutionInteraction.variableName ? (
                        <span className="execution-action-chip">
                          stores as {activeExecutionInteraction.variableName}
                        </span>
                      ) : null}
                      {activeExecutionInteraction.timeoutSeconds ? (
                        <span className="execution-action-chip">
                          timeout {activeExecutionInteraction.timeoutSeconds}s
                        </span>
                      ) : null}
                    </div>
                  </div>

                  {activeExecutionInteraction.prompt ? (
                    <div className="execution-action-block">
                      <div className="execution-action-block-label">Prompt</div>
                      <div className="execution-action-prompt">
                        {activeExecutionInteraction.prompt}
                      </div>
                    </div>
                  ) : null}

                  <div className="execution-action-block">
                    <div className="execution-action-field-head">
                      <label
                        className="field-label"
                        htmlFor="studio-execution-action-input"
                      >
                        {activeExecutionInteraction.kind === 'human_approval'
                          ? 'Feedback'
                          : activeExecutionInteraction.variableName || 'Input'}
                      </label>
                      <span
                        className={`execution-action-requirement ${activeExecutionInteraction.kind === 'human_approval' ? 'optional' : 'required'}`}
                      >
                        {activeExecutionInteraction.kind === 'human_approval'
                          ? 'Optional note'
                          : 'Required'}
                      </span>
                    </div>
                    <div className="execution-action-helper">
                      {activeExecutionInteraction.kind === 'human_approval'
                        ? 'Add context for the operator if needed, then approve or reject this gate.'
                        : activeExecutionInteraction.variableName
                          ? `The submitted value will resume the run and be available as ${activeExecutionInteraction.variableName}.`
                          : 'This response resumes the workflow immediately.'}
                    </div>
                    <textarea
                      id="studio-execution-action-input"
                      value={executionActionInput}
                      onChange={(event) => setExecutionActionInput(event.target.value)}
                      className="panel-textarea execution-action-textarea"
                      placeholder={
                        activeExecutionInteraction.kind === 'human_approval'
                          ? 'Optional feedback'
                          : 'Enter the value to continue this step'
                      }
                    />
                  </div>

                  <div className="execution-action-footer">
                    {activeExecutionInteraction.kind === 'human_approval' ? (
                      <>
                        <button
                          type="button"
                          className="ghost-action execution-danger-action"
                          disabled={
                            executionActionPendingKey ===
                            `${executionActionKeyBase}:reject`
                          }
                          onClick={() =>
                            void handleExecutionInteraction(
                              activeExecutionInteraction,
                              'reject',
                            )
                          }
                        >
                          {executionActionPendingKey ===
                          `${executionActionKeyBase}:reject`
                            ? 'Rejecting...'
                            : 'Reject'}
                        </button>
                        <button
                          type="button"
                          className="solid-action"
                          disabled={
                            executionActionPendingKey ===
                            `${executionActionKeyBase}:approve`
                          }
                          onClick={() =>
                            void handleExecutionInteraction(
                              activeExecutionInteraction,
                              'approve',
                            )
                          }
                        >
                          {executionActionPendingKey ===
                          `${executionActionKeyBase}:approve`
                            ? 'Approving...'
                            : 'Approve'}
                        </button>
                      </>
                    ) : (
                      <button
                        type="button"
                        className="solid-action"
                        disabled={
                          executionActionPendingKey ===
                          `${executionActionKeyBase}:submit`
                        }
                        onClick={() =>
                          void handleExecutionInteraction(
                            activeExecutionInteraction,
                            'submit',
                          )
                        }
                      >
                        {executionActionPendingKey ===
                        `${executionActionKeyBase}:submit`
                          ? 'Submitting...'
                          : 'Submit input'}
                      </button>
                    )}
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        ) : null}
      </section>
    );
  };

  if (logsPopoutMode) {
    return (
      <div style={{ ...fillCardStyle, height: '100%' }}>
        {selectedExecution.isError ? (
          <StudioNoticeCard
            type="error"
            title="读取执行详情失败"
            description={describeError(selectedExecution.error)}
          />
        ) : selectedExecution.data ? (
          renderExecutionLogsSection({ fullscreen: true })
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="选择一条测试运行后，这里会显示执行日志。"
          />
        )}
      </div>
    );
  }

  return (
    <div style={cardStackStyle}>
      {executions.isError ? (
        <StudioNoticeCard
          type="error"
          title="读取测试运行列表失败"
          description={describeError(executions.error)}
        />
      ) : null}
      {selectedExecution.isError ? (
        <StudioNoticeCard
          type="error"
          title="读取执行详情失败"
          description={describeError(selectedExecution.error)}
        />
      ) : null}
      {executionNotice ? (
        <StudioNoticeCard
          type={executionNotice.type}
          title={
            executionNotice.type === 'error'
              ? '执行操作失败'
              : executionNotice.type === 'info'
                ? '已请求停止运行'
                : '执行状态已更新'
          }
          description={executionNotice.message}
        />
      ) : null}
      {selectedExecutionDetail?.error ? (
        <StudioNoticeCard
          type="error"
          title="执行异常"
          description={selectedExecutionDetail.error}
        />
      ) : null}

      <div
        style={{
          background: '#FFFFFF',
          border: '1px solid #E6E3DE',
          borderRadius: 28,
          boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
          display: 'flex',
          flexDirection: 'column',
          minHeight: 'calc(100vh - 176px)',
          overflow: 'hidden',
        }}
      >
        <div
          style={{
            ...executionBarStyle,
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            minHeight: 36,
            padding: '0 16px',
          }}
        >
          <span
            style={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              background: executionAccentColor,
            }}
          />
          <Typography.Text
            strong
            style={{ color: executionAccentColor, margin: 0 }}
          >
            {executionStatusLabel}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ margin: 0 }}>
            {(activeWorkflowName || draftWorkflowName || '当前流程').trim() || '当前流程'} ·
            已执行 {executionExecutedSteps}/{executionTotalSteps || 0} 步骤
            {executionDurationLabel ? ` · 耗时 ${executionDurationLabel}` : ''}
          </Typography.Text>
          {executionPromptPreview ? (
            <Typography.Text
              type="secondary"
              style={{
                marginLeft: 'auto',
                maxWidth: 380,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              输入: "{executionPromptPreview}"
            </Typography.Text>
          ) : null}
          <Button
            type="primary"
            size="small"
            loading={runPending}
            disabled={!canRunWorkflow || runPending}
            onClick={() => setRunModalOpen(true)}
          >
            重新运行
          </Button>
          {executionCanStop ? (
            <Button
              danger
              size="small"
              loading={executionStopPending}
              disabled={executionStopPending}
              onClick={onStopExecution}
            >
              停止
            </Button>
          ) : null}
        </div>

        <div
          style={{
            background: '#FAFAFA',
            display: 'flex',
            flex: 1,
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          <div
            style={{
              flex: 1,
              minHeight: 320,
              overflow: 'hidden',
              position: 'relative',
            }}
          >
            <GraphCanvas
              height="100%"
              bottomInset={0}
              variant="studio"
              nodes={decoratedExecutionNodes}
              edges={decoratedExecutionEdges}
              selectedNodeId={
                activeExecutionLog?.stepId
                  ? decoratedExecutionNodes.find(
                      (node) => node.data.stepId === activeExecutionLog.stepId,
                    )?.id
                  : undefined
              }
              onNodeSelect={(nodeId) => {
                const stepId =
                  decoratedExecutionNodes.find((node) => node.id === nodeId)?.data.stepId ||
                  '';
                const logIndex = findExecutionLogIndexForStep(executionTrace, stepId);
                if (logIndex !== null) {
                  setActiveExecutionLogIndex(logIndex);
                }
              }}
            />
          </div>

          <section
            style={{
              background: '#FFFFFF',
              borderTop: '1px solid #F0F0F0',
              display: 'flex',
              flexDirection: 'column',
              maxHeight: 320,
              minHeight: activeExecutionInteraction ? 280 : 220,
            }}
          >
            <div
              style={{
                alignItems: 'center',
                background: '#FAFAFA',
                borderBottom: '1px solid #F0F0F0',
                display: 'flex',
                gap: 12,
                minHeight: 40,
                padding: '0 12px',
              }}
            >
              <Typography.Text strong style={{ fontSize: 12, margin: 0 }}>
                执行日志
              </Typography.Text>
              <Typography.Text type="secondary" style={{ fontSize: 11, margin: 0 }}>
                {executionLogCount} 个事件
              </Typography.Text>
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  gap: 8,
                  marginLeft: 'auto',
                }}
              >
                {currentWorkflowExecutions.length > 0 ? (
                  <select
                    aria-label="选择测试运行"
                    value={selectedExecutionDetail?.executionId || ''}
                    onChange={(event) => {
                      if (event.target.value) {
                        onOpenExecution(event.target.value);
                      }
                    }}
                    style={{
                      border: '1px solid #D9D9D9',
                      borderRadius: 4,
                      fontSize: 12,
                      height: 28,
                      minWidth: 220,
                      padding: '0 8px',
                    }}
                  >
                    <option value="">{executionSummaryLabel}</option>
                    {currentWorkflowExecutions.map((execution) => (
                      <option key={execution.executionId} value={execution.executionId}>
                        {formatDateTime(execution.startedAtUtc)} · {execution.status}
                      </option>
                    ))}
                  </select>
                ) : null}
                {selectedExecutionActorId ? (
                  <>
                    <Typography.Text type="secondary" style={{ fontSize: 11, margin: 0 }}>
                      Actor ID
                    </Typography.Text>
                    <code
                      title={selectedExecutionActorId}
                      style={{
                        color: '#595959',
                        fontSize: 11,
                        maxWidth: 220,
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                      }}
                    >
                      {selectedExecutionActorId}
                    </code>
                    <button
                      type="button"
                      className={`panel-icon-button execution-logs-copy-action ${copiedExecutionActorId === selectedExecutionActorId ? 'active' : ''}`}
                      title="复制 Actor ID"
                      aria-label="Copy Actor ID."
                      onClick={() =>
                        void handleCopyExecutionActorId(selectedExecutionActorId)
                      }
                    >
                      {copiedExecutionActorId === selectedExecutionActorId ? (
                        <CheckOutlined />
                      ) : (
                        <CopyOutlined />
                      )}
                    </button>
                  </>
                ) : null}
                {selectedExecutionDetail?.executionId ? (
                  <button
                    type="button"
                    className={`panel-icon-button execution-logs-window-action ${logsDetached ? 'active' : ''}`}
                    title={logsDetached ? '聚焦日志窗口' : '弹出日志窗口'}
                    aria-label="Pop out execution logs."
                    onClick={onPopOutLogs}
                  >
                    <ExpandOutlined />
                  </button>
                ) : null}
              </div>
            </div>

            {activeExecutionInteraction ? (
              <div
                style={{
                  borderBottom: '1px solid #F5F5F5',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 12,
                  padding: 12,
                }}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 12,
                    justifyContent: 'space-between',
                  }}
                >
                  <div>
                    <Typography.Text strong>
                      {activeExecutionInteraction.kind === 'human_approval'
                        ? '等待人工审批'
                        : '等待人工输入'}
                    </Typography.Text>
                    <div className="execution-action-subtitle">
                      {activeExecutionInteraction.kind === 'human_approval'
                        ? '查看当前关卡并决定通过或驳回。'
                        : '补充缺失信息后，当前步骤会继续执行。'}
                    </div>
                  </div>
                  <span className="execution-action-badge">
                    {activeExecutionInteraction.stepId}
                  </span>
                </div>
                {activeExecutionInteraction.prompt ? (
                  <div className="execution-action-prompt">
                    {activeExecutionInteraction.prompt}
                  </div>
                ) : null}
                <textarea
                  aria-label="执行交互输入"
                  value={executionActionInput}
                  onChange={(event) => setExecutionActionInput(event.target.value)}
                  className="panel-textarea execution-action-textarea"
                  placeholder={
                    activeExecutionInteraction.kind === 'human_approval'
                      ? '可选补充说明'
                      : '输入继续执行所需的内容'
                  }
                />
                <div className="execution-action-footer">
                  {activeExecutionInteraction.kind === 'human_approval' ? (
                    <>
                      <button
                        type="button"
                        className="ghost-action execution-danger-action"
                        disabled={
                          executionActionPendingKey ===
                          `${executionActionKeyBase}:reject`
                        }
                        onClick={() =>
                          void handleExecutionInteraction(
                            activeExecutionInteraction,
                            'reject',
                          )
                        }
                      >
                        {executionActionPendingKey ===
                        `${executionActionKeyBase}:reject`
                          ? '驳回中...'
                          : '驳回'}
                      </button>
                      <button
                        type="button"
                        className="solid-action"
                        disabled={
                          executionActionPendingKey ===
                          `${executionActionKeyBase}:approve`
                        }
                        onClick={() =>
                          void handleExecutionInteraction(
                            activeExecutionInteraction,
                            'approve',
                          )
                        }
                      >
                        {executionActionPendingKey ===
                        `${executionActionKeyBase}:approve`
                          ? '通过中...'
                          : '通过'}
                      </button>
                    </>
                  ) : (
                    <button
                      type="button"
                      className="solid-action"
                      disabled={
                        executionActionPendingKey ===
                        `${executionActionKeyBase}:submit`
                      }
                      onClick={() =>
                        void handleExecutionInteraction(
                          activeExecutionInteraction,
                          'submit',
                        )
                      }
                    >
                      {executionActionPendingKey ===
                      `${executionActionKeyBase}:submit`
                        ? '提交中...'
                        : '提交'}
                    </button>
                  )}
                </div>
              </div>
            ) : null}

            <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
              {currentWorkflowExecutions.length === 0 ? (
                <StudioCatalogEmptyPanel
                  icon={<CaretRightFilled style={{ color: '#CBD5E1' }} />}
                  title="暂无运行记录"
                  copy="开始一次测试运行后，这里会显示执行日志。"
                />
              ) : executionTrace?.logs?.length ? (
                executionTrace.logs.map((log, index) => (
                  <button
                    key={`${log.timestamp}-${log.stepId || 'run'}-${log.title}`}
                    type="button"
                    onClick={() => void handleExecutionLogClick(log, index)}
                    style={{
                      alignItems: 'flex-start',
                      background:
                        activeExecutionLogIndex === index ? '#F5F7FF' : '#FFFFFF',
                      border: 'none',
                      borderBottom: '1px solid #FAFAFA',
                      cursor: 'pointer',
                      display: 'flex',
                      gap: 8,
                      padding: '6px 12px',
                      textAlign: 'left',
                      width: '100%',
                    }}
                    title="点击复制这条日志"
                  >
                    <span
                      style={{
                        color: '#BFBFBF',
                        fontFamily: 'SF Mono, Menlo, Monaco, Consolas, monospace',
                        fontSize: 11,
                        minWidth: 64,
                      }}
                    >
                      {formatDateTime(log.timestamp)}
                    </span>
                    <span
                      style={{
                        color:
                          log.tone === 'failed'
                            ? '#ff4d4f'
                            : log.tone === 'completed'
                              ? '#52c41a'
                              : log.tone === 'pending'
                                ? '#faad14'
                                : '#1890ff',
                        fontSize: 11,
                        fontWeight: 600,
                        minWidth: 64,
                        textTransform: 'uppercase',
                      }}
                    >
                      {log.tone === 'failed'
                        ? '失败'
                        : log.tone === 'completed'
                          ? '完成'
                          : log.tone === 'pending'
                            ? '等待'
                            : log.tone === 'run'
                              ? '运行'
                              : '开始'}
                    </span>
                    <span style={{ color: '#434343', flex: 1, fontSize: 12 }}>
                      {log.previewText || log.meta || log.title}
                    </span>
                  </button>
                ))
              ) : (
                <StudioCatalogEmptyPanel
                  icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
                  title="还没有日志"
                  copy="选择一条测试运行后，这里会显示步骤执行和状态变化。"
                />
              )}
            </div>
          </section>
        </div>
      </div>

      <Modal
        open={runModalOpen}
        title="测试运行"
        onCancel={() => setRunModalOpen(false)}
        onOk={() => {
          void onStartExecution();
          setRunModalOpen(false);
        }}
        okText="开始运行"
        okButtonProps={{
          disabled: !canRunWorkflow,
          loading: runPending,
          icon: <CaretRightFilled />,
        }}
      >
        <div style={cardStackStyle}>
          <Typography.Text type="secondary">
            这段输入会作为 <Typography.Text code>$input</Typography.Text> 传入当前行为定义。
          </Typography.Text>
          <Input.TextArea
            aria-label="Studio execution prompt"
            autoSize={{ minRows: 6, maxRows: 10 }}
            className="run-prompt-textarea"
            placeholder="这次运行要做什么？"
            value={runPrompt}
            onChange={(event) => onRunPromptChange(event.target.value)}
          />
        </div>
      </Modal>
    </div>
  );
};

export type StudioEditorPageProps = {
  readonly workflows: QueryState<StudioWorkflowSummary[]>;
  readonly selectedWorkflow: QueryState<unknown>;
  readonly templateWorkflow: QueryState<unknown>;
  readonly connectors: QueryState<StudioConnectorCatalog>;
  readonly draftYaml: string;
  readonly draftWorkflowName: string;
  readonly draftDirectoryId: string;
  readonly draftFileName: string;
  readonly draftMode: DraftMode;
  readonly selectedWorkflowId: string;
  readonly templateWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeWorkflowFile: StudioWorkflowFile | null | undefined;
  readonly isDraftDirty: boolean;
  readonly workflowGraph: {
    readonly roles: StudioGraphRole[];
    readonly steps: StudioGraphStep[];
    readonly nodes: Parameters<typeof GraphCanvas>[0]['nodes'];
    readonly edges: Parameters<typeof GraphCanvas>[0]['edges'];
  };
  readonly parseYaml: QueryState<{
    readonly document?: { readonly name?: string } | null;
    readonly findings: unknown[];
  }>;
  readonly selectedGraphNodeId: string;
  readonly selectedGraphEdge: StudioSelectedGraphEdge | null;
  readonly workflowRoleIds: string[];
  readonly workflowStepIds: string[];
  readonly inspectorTab: StudioInspectorTab;
  readonly inspectorContent: React.ReactNode;
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly saveNotice: StudioNoticeLike | null;
  readonly workflowImportPending: boolean;
  readonly workflowImportNotice: StudioNoticeLike | null;
  readonly workflowImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly askAiPrompt: string;
  readonly askAiPending: boolean;
  readonly askAiNotice: StudioNoticeLike | null;
  readonly askAiReasoning: string;
  readonly askAiAnswer: string;
  readonly canAskAiGenerate: boolean;
  readonly askAiUnavailableMessage: string;
  readonly runPrompt: string;
  readonly recentPromptHistory: readonly PlaygroundPromptHistoryEntry[];
  readonly promptHistoryCount: number;
  readonly runPending: boolean;
  readonly canOpenRunWorkflow: boolean;
  readonly canRunWorkflow: boolean;
  readonly runNotice: StudioNoticeLike | null;
  readonly resolvedScopeId?: string;
  readonly publishPending: boolean;
  readonly canPublishWorkflow: boolean;
  readonly publishNotice: StudioNoticeLike | null;
  readonly scopeBinding?: StudioScopeBindingStatus;
  readonly scopeBindingLoading: boolean;
  readonly scopeBindingError: unknown;
  readonly gAgentTypes: readonly RuntimeGAgentTypeDescriptor[];
  readonly gAgentTypesLoading: boolean;
  readonly gAgentTypesError: unknown;
  readonly bindingActivationRevisionId: string;
  readonly bindingRetirementRevisionId: string;
  readonly onSwitchStudioView: (view: 'editor' | 'execution') => void;
  readonly onExportDraft: () => void;
  readonly onSelectGraphNode: (nodeId: string) => void;
  readonly onSelectGraphEdge: (edgeId: string) => void;
  readonly onClearGraphSelection: () => void;
  readonly onAddGraphNode: (
    stepType: string,
    connectorName?: string,
    preferredPosition?: { x: number; y: number } | null,
  ) => void;
  readonly onConnectGraphNodes: (sourceId: string, targetId: string) => void;
  readonly onUpdateGraphLayout: (
    nodes: Parameters<typeof GraphCanvas>[0]['nodes'],
  ) => void;
  readonly onDeleteSelectedGraphEdge: () => void;
  readonly onSetWorkflowDescription: (value: string) => void;
  readonly onSetDraftYaml: (value: string) => void;
  readonly onSetDraftWorkflowName: (value: string) => void;
  readonly onSetDraftDirectoryId: (value: string) => void;
  readonly onSetDraftFileName: (value: string) => void;
  readonly onSetInspectorTab: (tab: StudioInspectorTab) => void;
  readonly onValidateDraft: () => void;
  readonly onWorkflowImportClick: () => void;
  readonly onWorkflowImportChange: React.ChangeEventHandler<HTMLInputElement>;
  readonly onResetDraft: () => void;
  readonly onSaveDraft: () => void;
  readonly onPublishWorkflow: () => void;
  readonly onOpenProjectOverview: () => void;
  readonly onOpenProjectInvoke: () => void;
  readonly onOpenWorkflow: (workflowId: string) => void;
  readonly onStartBlankDraft: () => void;
  readonly onBindGAgent: (input: {
    displayName?: string;
    actorTypeName: string;
    endpoints?: Array<{
      endpointId: string;
      displayName?: string;
      kind?: 'command' | 'chat';
      requestTypeUrl?: string;
      responseTypeUrl?: string;
      description?: string;
    }>;
    openRunsEndpointId?: string;
    endpointId?: string;
    endpointDisplayName?: string;
    requestTypeUrl?: string;
    responseTypeUrl?: string;
    description?: string;
    prompt?: string;
    payloadBase64?: string;
  }, options?: {
    openRuns?: boolean;
  }) => Promise<void>;
  readonly onActivateBindingRevision: (revisionId: string) => void;
  readonly onRetireBindingRevision: (revisionId: string) => void;
  readonly onInspectPublishedWorkflow: () => void;
  readonly onRunInConsole: () => void;
  readonly onAskAiPromptChange: (value: string) => void;
  readonly onAskAiGenerate: () => void;
  readonly onRunPromptChange: (value: string) => void;
  readonly onClearPromptHistory: () => void;
  readonly onReusePrompt: (prompt: string) => void;
  readonly onOpenWorkflowFromHistory: (workflowName: string, prompt: string) => void;
  readonly onStartExecution: () => void;
  readonly onOpenExecutions: () => void;
};

export const StudioEditorPage: React.FC<StudioEditorPageProps> = ({
  workflows,
  selectedWorkflow,
  templateWorkflow,
  connectors,
  draftYaml,
  draftWorkflowName,
  draftDirectoryId,
  draftFileName,
  draftMode,
  selectedWorkflowId,
  templateWorkflowName,
  activeWorkflowDescription,
  activeWorkflowFile,
  isDraftDirty,
  workflowGraph,
  selectedGraphNodeId,
  selectedGraphEdge,
  workflowRoleIds,
  workflowStepIds,
  inspectorTab,
  inspectorContent,
  workspaceSettings,
  savePending,
  canSaveWorkflow,
  saveNotice,
  workflowImportNotice,
  workflowImportInputRef,
  askAiPrompt,
  askAiPending,
  askAiNotice,
  askAiReasoning,
  askAiAnswer,
  canAskAiGenerate,
  askAiUnavailableMessage,
  runPrompt,
  runPending,
  canOpenRunWorkflow,
  canRunWorkflow,
  runNotice,
  resolvedScopeId,
  publishPending,
  canPublishWorkflow,
  publishNotice,
  scopeBinding,
  scopeBindingLoading,
  scopeBindingError,
  gAgentTypes,
  gAgentTypesLoading,
  gAgentTypesError,
  bindingActivationRevisionId,
  bindingRetirementRevisionId,
  onSwitchStudioView,
  onExportDraft,
  onSelectGraphNode,
  onSelectGraphEdge,
  onClearGraphSelection,
  onAddGraphNode,
  onConnectGraphNodes,
  onUpdateGraphLayout,
  onDeleteSelectedGraphEdge,
  onSetWorkflowDescription,
  onSetDraftWorkflowName,
  onSetInspectorTab,
  onWorkflowImportChange,
  onSaveDraft,
  onPublishWorkflow,
  onOpenProjectOverview,
  onOpenProjectInvoke,
  onOpenWorkflow,
  onStartBlankDraft,
  onBindGAgent,
  onActivateBindingRevision,
  onRetireBindingRevision,
  onRunInConsole,
  onAskAiPromptChange,
  onAskAiGenerate,
  onRunPromptChange,
  onStartExecution,
}) => {
  const hasSelectedGraphNode = Boolean(selectedGraphNodeId);
  const [toolDrawerMode, setToolDrawerMode] =
    React.useState<StudioToolDrawerMode | null>(null);
  const [nodePaletteSearch, setNodePaletteSearch] = React.useState('');
  const [nodePaletteSection, setNodePaletteSection] = React.useState('AI');
  const [runModalOpen, setRunModalOpen] = React.useState(false);
  const [gAgentModalOpen, setGAgentModalOpen] = React.useState(false);
  const [gAgentBindingPending, setGAgentBindingPending] = React.useState(false);
  const [gAgentAdvancedOpen, setGAgentAdvancedOpen] = React.useState(false);
  const [gAgentDraft, setGAgentDraft] = React.useState<StudioGAgentBindingDraft>(
    () => createStudioGAgentBindingDraft(draftWorkflowName || templateWorkflowName || ''),
  );
  const [descriptionEditorOpen, setDescriptionEditorOpen] = React.useState(false);
  const [descriptionDraft, setDescriptionDraft] = React.useState(activeWorkflowDescription);
  const [pendingAddPosition, setPendingAddPosition] = React.useState({
    x: 420,
    y: 220,
  });
  const hasResolvedProject = Boolean(resolvedScopeId);
  const hasNamedDraft = Boolean(draftWorkflowName.trim() && draftYaml.trim());
  const selectedDiscoveredGAgentType = React.useMemo(
    () =>
      gAgentTypes.find((descriptor) =>
        matchesRuntimeGAgentTypeDescriptor(gAgentDraft.actorTypeName, descriptor),
      ) || null,
    [gAgentDraft.actorTypeName, gAgentTypes],
  );
  const launchableGAgentEndpoints = React.useMemo(
    () =>
      gAgentDraft.endpoints.filter((endpoint) => endpoint.endpointId.trim().length > 0),
    [gAgentDraft.endpoints],
  );
  const selectedOpenRunsEndpoint = React.useMemo(
    () =>
      launchableGAgentEndpoints.find(
        (endpoint) =>
          endpoint.endpointId.trim() === gAgentDraft.openRunsEndpointId.trim(),
      ) ||
      launchableGAgentEndpoints[0] ||
      null,
    [gAgentDraft.openRunsEndpointId, launchableGAgentEndpoints],
  );
  const primaryGAgentEndpoint = gAgentDraft.endpoints[0] ?? null;
  const inferredPrimaryGAgentPreset = React.useMemo(
    () =>
      inferStudioGAgentEndpointPreset(
        gAgentDraft.actorTypeName,
        selectedDiscoveredGAgentType,
      ),
    [gAgentDraft.actorTypeName, selectedDiscoveredGAgentType],
  );

  const updateGAgentEndpointDraft = React.useCallback(
    (
      index: number,
      patch: Partial<StudioGAgentBindingEndpointDraft>,
    ) => {
      setGAgentDraft((current) => {
        const previousEndpoint = current.endpoints[index];
        if (!previousEndpoint) {
          return current;
        }

        const nextEndpoint = {
          ...previousEndpoint,
          ...patch,
        };
        const nextEndpoints = current.endpoints.map((endpoint, endpointIndex) =>
          endpointIndex === index ? nextEndpoint : endpoint,
        );
        const nextOpenRunsEndpointId =
          current.openRunsEndpointId.trim() === previousEndpoint.endpointId.trim()
            ? nextEndpoint.endpointId
            : current.openRunsEndpointId;

        return {
          ...current,
          endpoints: nextEndpoints,
          openRunsEndpointId: nextOpenRunsEndpointId,
        };
      });
    },
    [],
  );

  const addGAgentEndpointDraft = React.useCallback(() => {
    setGAgentDraft((current) => {
      const nextIndex = current.endpoints.length + 1;
      const endpoint = createStudioGAgentBindingEndpointDraft({
        endpointId: `run-${nextIndex}`,
        displayName: `Run ${nextIndex}`,
        description: 'Run the bound GAgent.',
      });

      return {
        ...current,
        endpoints: [...current.endpoints, endpoint],
      };
    });
  }, []);

  const removeGAgentEndpointDraft = React.useCallback((index: number) => {
    setGAgentDraft((current) => {
      if (current.endpoints.length <= 1) {
        return current;
      }

      const removedEndpoint = current.endpoints[index];
      if (!removedEndpoint) {
        return current;
      }

      const nextEndpoints = current.endpoints.filter(
        (_endpoint, endpointIndex) => endpointIndex !== index,
      );
      const nextOpenRunsEndpointId =
        current.openRunsEndpointId.trim() === removedEndpoint.endpointId.trim()
          ? nextEndpoints[0]?.endpointId ?? ''
          : current.openRunsEndpointId;

      return {
        ...current,
        endpoints: nextEndpoints,
        openRunsEndpointId: nextOpenRunsEndpointId,
      };
    });
  }, []);

  const filteredPrimitiveCategories = React.useMemo(() => {
    const keyword = nodePaletteSearch.trim().toLowerCase();
    if (!keyword) {
      return STUDIO_GRAPH_CATEGORIES;
    }

    return STUDIO_GRAPH_CATEGORIES.map((category) => ({
      ...category,
      items: category.items.filter((item) => item.toLowerCase().includes(keyword)),
    })).filter((category) => category.items.length > 0);
  }, [nodePaletteSearch]);

  const filteredConnectorPalette = React.useMemo(() => {
    const keyword = nodePaletteSearch.trim().toLowerCase();
    const items = connectors.data?.connectors ?? [];
    if (!keyword) {
      return items;
    }

    return items.filter((connector) =>
      [connector.name, connector.type].some((value) =>
        value.toLowerCase().includes(keyword),
      ),
    );
  }, [connectors.data?.connectors, nodePaletteSearch]);

  React.useEffect(() => {
    if (toolDrawerMode !== 'palette') {
      return;
    }

    const hasExpandedCategory = filteredPrimitiveCategories.some(
      (category) => category.label === nodePaletteSection,
    );
    if (hasExpandedCategory || nodePaletteSection === 'Configured connectors') {
      return;
    }

    if (filteredPrimitiveCategories[0]?.label) {
      setNodePaletteSection(filteredPrimitiveCategories[0].label);
      return;
    }

    if (filteredConnectorPalette.length > 0) {
      setNodePaletteSection('Configured connectors');
    }
  }, [
    filteredConnectorPalette.length,
    filteredPrimitiveCategories,
    nodePaletteSection,
    toolDrawerMode,
  ]);

  const closeToolDrawer = () => {
    setToolDrawerMode(null);
  };

  const openPaletteFromEditor = React.useCallback(() => {
    setPendingAddPosition({
      x: 420,
      y: 220,
    });
    setToolDrawerMode('palette');
  }, []);

  const openAskAiFromEditor = React.useCallback(() => {
    if (!canAskAiGenerate) {
      return;
    }

    setToolDrawerMode('ask-ai');
  }, [canAskAiGenerate]);

  const openGAgentModal = React.useCallback(() => {
    const defaultDescriptor = gAgentTypes[0] ?? null;
    const defaultActorTypeName =
      buildRuntimeGAgentAssemblyQualifiedName(defaultDescriptor ?? {
        typeName: '',
        fullName: '',
        assemblyName: '',
      }).trim() || '';
    const defaultPreset = inferStudioGAgentEndpointPreset(
      defaultActorTypeName,
      defaultDescriptor,
    );
    setGAgentDraft((current) => ({
      ...current,
      displayName: current.displayName || draftWorkflowName || templateWorkflowName || '',
      actorTypeName:
        current.actorTypeName.trim() ||
        defaultActorTypeName ||
        '',
      endpoints:
        current.endpoints.length > 0
          ? current.actorTypeName.trim()
            ? current.endpoints
            : [
                applyStudioGAgentEndpointPreset(
                  current.endpoints[0],
                  defaultPreset,
                ),
                ...current.endpoints.slice(1),
              ]
          : [createStudioGAgentBindingPresetEndpointDraft(defaultPreset)],
      openRunsEndpointId:
        current.openRunsEndpointId ||
        current.endpoints[0]?.endpointId ||
        createStudioGAgentBindingPresetEndpointDraft(defaultPreset).endpointId,
    }));
    setGAgentAdvancedOpen(false);
    setGAgentModalOpen(true);
  }, [draftWorkflowName, gAgentTypes, templateWorkflowName]);

  const updatePrimaryGAgentEndpointPreset = React.useCallback(
    (preset: 'command' | 'chat') => {
      setGAgentDraft((current) => {
        const primaryEndpoint = current.endpoints[0];
        if (!primaryEndpoint) {
          const nextEndpoint = createStudioGAgentBindingPresetEndpointDraft(preset);
          return {
            ...current,
            endpoints: [nextEndpoint],
            openRunsEndpointId: nextEndpoint.endpointId,
          };
        }

        const nextPrimaryEndpoint = applyStudioGAgentEndpointPreset(
          primaryEndpoint,
          preset,
        );
        const nextEndpoints = [
          nextPrimaryEndpoint,
          ...current.endpoints.slice(1),
        ];
        const nextOpenRunsEndpointId =
          current.openRunsEndpointId.trim() === primaryEndpoint.endpointId.trim()
            ? nextPrimaryEndpoint.endpointId
            : current.openRunsEndpointId;

        return {
          ...current,
          endpoints: nextEndpoints,
          openRunsEndpointId: nextOpenRunsEndpointId,
        };
      });
    },
    [],
  );

  const submitGAgentBinding = React.useCallback(async (openRuns: boolean) => {
    setGAgentBindingPending(true);
    try {
      await onBindGAgent(
        {
          displayName: gAgentDraft.displayName,
          actorTypeName: gAgentDraft.actorTypeName,
          endpoints: gAgentDraft.endpoints.map((endpoint) => ({
            endpointId: endpoint.endpointId,
            displayName: endpoint.displayName,
            kind: endpoint.kind,
            requestTypeUrl: endpoint.requestTypeUrl,
            responseTypeUrl: endpoint.responseTypeUrl,
            description: endpoint.description,
          })),
          openRunsEndpointId:
            selectedOpenRunsEndpoint?.endpointId ||
            gAgentDraft.openRunsEndpointId,
          endpointId: gAgentDraft.endpoints[0]?.endpointId || '',
          endpointDisplayName: gAgentDraft.endpoints[0]?.displayName || '',
          requestTypeUrl: gAgentDraft.endpoints[0]?.requestTypeUrl || '',
          responseTypeUrl: gAgentDraft.endpoints[0]?.responseTypeUrl || '',
          description: gAgentDraft.endpoints[0]?.description || '',
          prompt: gAgentDraft.prompt,
          payloadBase64: gAgentDraft.payloadBase64,
        },
        {
          openRuns,
        },
      );
      if (!openRuns) {
        void message.success('团队入口已绑定成功。');
      }
      setGAgentModalOpen(false);
    } catch {
      // The page-level binder already surfaces the error notice. Keep the dialog
      // open so the user can correct the input or retry.
    } finally {
      setGAgentBindingPending(false);
    }
  }, [gAgentDraft, onBindGAgent, selectedOpenRunsEndpoint]);

  const activeDirectoryLabel =
    workspaceSettings.data?.directories.find(
      (item) => item.directoryId === draftDirectoryId,
    )?.label ||
    activeWorkflowFile?.directoryLabel ||
    'No directory';
  const workflowDisplayName =
    draftWorkflowName.trim() ||
    activeWorkflowFile?.name ||
    templateWorkflowName ||
    'draft';
  const visibleWorkflowDefinitions = React.useMemo(
    () => dedupeStudioWorkflowSummaries(workflows.data ?? []),
    [workflows.data],
  );

  React.useEffect(() => {
    if (askAiPending || askAiNotice || askAiAnswer || askAiReasoning) {
      setToolDrawerMode('ask-ai');
    }
  }, [askAiAnswer, askAiNotice, askAiPending, askAiReasoning]);

  React.useEffect(() => {
    if (!descriptionEditorOpen) {
      setDescriptionDraft(activeWorkflowDescription);
    }
  }, [activeWorkflowDescription, descriptionEditorOpen]);

  const handleAddNodeFromPalette = (stepType: string, connectorName?: string) => {
    onAddGraphNode(stepType, connectorName, pendingAddPosition);
    setToolDrawerMode(null);
  };

  const hasEditableDraftContext = Boolean(
    draftMode === 'new' ||
      selectedWorkflowId.trim() ||
      templateWorkflowName.trim() ||
      draftWorkflowName.trim() ||
      draftFileName.trim() ||
      activeWorkflowFile?.workflowId?.trim(),
  );

  const askAiStatusText = !canAskAiGenerate
    ? askAiUnavailableMessage || '当前环境暂不支持 AI 辅助。'
    : askAiPending
      ? '正在生成并校验 YAML...'
      : askAiAnswer.trim()
        ? '校验通过的 YAML 已写回当前草稿。'
        : '返回格式仅限 workflow YAML。';
  const toolDrawerVisible = hasEditableDraftContext && toolDrawerMode !== null;
  const toolDrawerTitle =
    toolDrawerMode === 'palette' ? '添加步骤' : 'AI 辅助';
  const nodePaletteDrawerContent = (
    <div style={cardStackStyle}>
      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>步骤库</Typography.Text>
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          搜索基础步骤或已配置集成，然后把新步骤插入到当前画布位置。
        </Typography.Paragraph>
        <Input
          allowClear
          prefix={<SearchOutlined />}
          placeholder="搜索步骤或集成"
          value={nodePaletteSearch}
          onChange={(event) => setNodePaletteSearch(event.target.value)}
        />
      </div>

      {filteredPrimitiveCategories.map((category) => {
        const Icon =
          studioPaletteCategoryIcons[category.key] ??
          studioPaletteCategoryIcons.custom;
        const expanded = nodePaletteSection === category.label;

        return (
          <div key={category.key} style={studioToolDrawerSectionStyle}>
            <Button
              type="text"
              block
              onClick={() =>
                setNodePaletteSection(expanded ? '' : category.label)
              }
              style={{
                alignItems: 'center',
                display: 'flex',
                gap: 12,
                height: 'auto',
                justifyContent: 'flex-start',
                padding: 0,
                textAlign: 'left',
              }}
            >
              <div
                style={{
                  alignItems: 'center',
                  background: `${category.color}18`,
                  borderRadius: 12,
                  color: category.color,
                  display: 'flex',
                  height: 32,
                  justifyContent: 'center',
                  width: 32,
                }}
              >
                <Icon />
              </div>
              <Typography.Text strong style={{ flex: 1 }}>
                {category.label}
              </Typography.Text>
              <Tag>{category.items.length}</Tag>
            </Button>
            {expanded ? (
              <div style={cardListStyle}>
                {category.items.map((item) => (
                  <div key={item} style={cardListItemStyle}>
                    <div style={cardListHeaderStyle}>
                      <div style={cardListMainStyle}>
                        <Typography.Text strong>{item}</Typography.Text>
                        <Typography.Text type="secondary">
                          {category.label}
                        </Typography.Text>
                      </div>
                      <div style={cardListActionStyle}>
                        <Button
                          size="small"
                          onClick={() => handleAddNodeFromPalette(item)}
                        >
                          插入
                        </Button>
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        );
      })}

      {filteredConnectorPalette.length > 0 ? (
        <div style={studioToolDrawerSectionStyle}>
          <Button
            type="text"
            block
            onClick={() =>
              setNodePaletteSection(
                nodePaletteSection === 'Configured connectors'
                  ? ''
                  : 'Configured connectors',
              )
            }
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 12,
              height: 'auto',
              justifyContent: 'flex-start',
              padding: 0,
              textAlign: 'left',
            }}
          >
            <div
              style={{
                alignItems: 'center',
                background: '#64748B18',
                borderRadius: 12,
                color: '#64748B',
                display: 'flex',
                height: 32,
                justifyContent: 'center',
                width: 32,
              }}
            >
              <ApiOutlined />
            </div>
            <Typography.Text strong style={{ flex: 1 }}>
              已配置集成
            </Typography.Text>
            <Tag>{filteredConnectorPalette.length}</Tag>
          </Button>
          {nodePaletteSection === 'Configured connectors' ? (
            <div style={cardListStyle}>
              {filteredConnectorPalette.map((connector) => (
                <div key={connector.name} style={cardListItemStyle}>
                  <div style={cardListHeaderStyle}>
                    <div style={cardListMainStyle}>
                      <Typography.Text strong>{connector.name}</Typography.Text>
                      <Space wrap size={[6, 6]}>
                        <Tag>{connector.type}</Tag>
                        {connector.enabled ? (
                          <Tag color="success">已启用</Tag>
                        ) : (
                          <Tag color="default">已停用</Tag>
                        )}
                      </Space>
                    </div>
                    <div style={cardListActionStyle}>
                      <Button
                        size="small"
                        onClick={() =>
                          handleAddNodeFromPalette(
                            'connector_call',
                            connector.name,
                          )
                        }
                      >
                        插入
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
  const askAiDrawerContent = !canAskAiGenerate ? (
    <StudioNoticeCard
      type="error"
      title="AI 辅助暂不可用"
      description={askAiUnavailableMessage || '当前环境暂不支持 AI 辅助。'}
    />
  ) : (
    <div style={cardStackStyle}>
      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>行为描述</Typography.Text>
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          描述你想要的行为结果。Studio 会把校验通过的 YAML 自动写回当前草稿。
        </Typography.Paragraph>
        <Input.TextArea
          aria-label="Studio AI workflow prompt"
          autoSize={{ minRows: 5, maxRows: 10 }}
          placeholder="构建一个能分诊事件、把高风险情况交给人工审批，并把结果发到 Slack 的流程。"
          value={askAiPrompt}
          onChange={(event) => onAskAiPromptChange(event.target.value)}
        />
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {askAiStatusText}
          </Typography.Text>
          <Button
            type="primary"
            loading={askAiPending}
            onClick={onAskAiGenerate}
          >
            {askAiPending ? '生成中' : '生成'}
          </Button>
        </div>
      </div>

      {askAiNotice ? (
        <StudioNoticeCard
          type={askAiNotice.type}
          title={
            askAiNotice.type === 'error'
              ? 'AI 辅助生成失败'
              : 'AI 辅助已更新当前草稿'
          }
          description={askAiNotice.message}
        />
      ) : null}

      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>推理过程</Typography.Text>
        <pre style={{ ...codeBlockStyle, margin: 0, maxHeight: 160 }}>
          {askAiReasoning || '模型推理会显示在这里。'}
        </pre>
      </div>

      <div style={studioToolDrawerSectionStyle}>
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Typography.Text strong>校验后的 YAML</Typography.Text>
          <Tag color={askAiAnswer.trim() ? 'success' : 'default'}>
            {askAiAnswer.trim() ? '已写回草稿' : '等待有效 YAML'}
          </Tag>
        </div>
        <pre style={{ ...codeBlockStyle, margin: 0, maxHeight: 260 }}>
          {askAiAnswer || '校验通过的 workflow YAML 会显示在这里。'}
        </pre>
      </div>
    </div>
  );

  const editorStatusItems: React.ReactNode[] = [];

  if (saveNotice && saveNotice.type === 'error') {
    editorStatusItems.push(
      <StudioNoticeCard
        key="save-notice"
        type={saveNotice.type}
        title="定义保存失败"
        description={saveNotice.message}
        compact
      />,
    );
  }

  if (workflowImportNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="workflow-import-notice"
        type={workflowImportNotice.type}
        title={
          workflowImportNotice.type === 'error'
            ? '定义导入失败'
            : '定义已导入'
        }
        description={workflowImportNotice.message}
        compact
      />,
    );
  }

  if (runNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="run-notice"
        type={runNotice.type}
        title={
          runNotice.type === 'success'
            ? '测试运行已启动'
            : '测试运行失败'
        }
        description={runNotice.message}
        compact
      />,
    );
  }

  if (publishNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="publish-notice"
        type={publishNotice.type}
        title={
          publishNotice.type === 'error'
            ? '团队入口发布失败'
            : '团队入口已更新'
        }
        description={publishNotice.message}
        compact
      />,
    );
  }

  const editorFatalNotice = selectedWorkflow.isError ? (
    <StudioNoticeCard
      key="selected-workflow-error"
      type="error"
      title="读取行为定义失败"
      description={describeError(selectedWorkflow.error)}
      compact
    />
  ) : templateWorkflow.isError ? (
    <StudioNoticeCard
      key="template-workflow-error"
      type="error"
      title="读取模板定义失败"
      description={describeError(templateWorkflow.error)}
      compact
    />
  ) : null;
  const inspectorPanelBody = (
    <div
      data-testid="studio-inspector-scroll"
      style={{
        display: 'flex',
        flex: 1,
        flexDirection: 'column',
        gap: 12,
        minHeight: 0,
        overflowY: 'auto',
        padding: 12,
      }}
    >
      <datalist id="studio-workflow-role-options">
        {workflowRoleIds.map((roleId) => (
          <option key={roleId} value={roleId} />
        ))}
      </datalist>
      <datalist id="studio-workflow-step-options">
        {workflowStepIds.map((stepId) => (
          <option key={stepId} value={stepId} />
        ))}
      </datalist>
      {inspectorContent}
    </div>
  );
  const canvasIsEmpty = workflowGraph.nodes.length === 0;
  const inspectorShowsNodeEmptyState =
    inspectorTab === 'node' && !hasSelectedGraphNode;
  const inspectorPanelWidth = inspectorShowsNodeEmptyState ? 272 : 320;
  const inspectorPanelSurface = inspectorShowsNodeEmptyState ? (
    <div
      data-testid="studio-inspector-empty-state"
      style={studioInspectorEmptyStateStyle}
    >
      <Typography.Text strong>先选一个步骤</Typography.Text>
      <Typography.Text type="secondary">
        当前先把画布搭起来。你也可以直接切去角色或 YAML 继续完善草稿。
      </Typography.Text>
      <div style={studioInspectorQuickActionsStyle}>
        <Button icon={<PlusOutlined />} type="primary" onClick={openPaletteFromEditor}>
          打开步骤库
        </Button>
        <Button onClick={() => onSetInspectorTab('roles')}>管理角色</Button>
        <Button icon={<CodeOutlined />} onClick={() => onSetInspectorTab('yaml')}>
          查看 YAML 草稿
        </Button>
      </div>
    </div>
  ) : (
    inspectorPanelBody
  );

  return (
    <div style={studioEditorPageRootStyle}>
      {editorFatalNotice ? (
        <div style={studioNoticeStripStyle}>{editorFatalNotice}</div>
      ) : hasEditableDraftContext ? (
        <>
          <input
            ref={workflowImportInputRef}
            hidden
            accept=".yaml,.yml,text/yaml,text/x-yaml"
            type="file"
            onChange={onWorkflowImportChange}
          />

          <div
            data-testid="studio-editor-shell"
            style={studioEditorShellStyle}
          >
            <div style={studioEditorHeaderStyle}>
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  flex: 1,
                  gap: 10,
                  minWidth: 0,
                }}
              >
                <div style={studioTitleBarStyle}>
                  <div style={studioTitleGroupStyle}>
                    <input
                      aria-label="Studio workflow title"
                      placeholder={workflowDisplayName}
                      value={draftWorkflowName}
                      onChange={(event) => onSetDraftWorkflowName(event.target.value)}
                      style={studioTitleInputStyle}
                    />
                    <StudioInfoPopover
                      open={descriptionEditorOpen}
                      ariaLabel="编辑定义说明"
                      onOpenChange={(open) => {
                        setDescriptionEditorOpen(open);
                        if (open) {
                          setDescriptionDraft(activeWorkflowDescription);
                        }
                      }}
                    >
                      <textarea
                        aria-label="Studio workflow description"
                        placeholder="定义说明"
                        value={descriptionDraft}
                        onChange={(event) =>
                          setDescriptionDraft(event.target.value)
                        }
                        style={studioDescriptionEditorStyle}
                      />
                      <div style={studioInfoPopoverActionsStyle}>
                        <Button
                          size="small"
                          style={{ minWidth: 72, borderRadius: 10 }}
                          onClick={() => {
                            setDescriptionDraft(activeWorkflowDescription);
                            setDescriptionEditorOpen(false);
                          }}
                        >
                          取消
                        </Button>
                        <Button
                          type="primary"
                          size="small"
                          style={{ minWidth: 72, borderRadius: 10 }}
                          onClick={() => {
                            onSetWorkflowDescription(descriptionDraft);
                            setDescriptionEditorOpen(false);
                          }}
                        >
                          保存
                        </Button>
                      </div>
                    </StudioInfoPopover>
                  </div>
                </div>
              </div>

              <div
                style={studioEditorToolbarStyle}
              >
                <Button
                  type="primary"
                  icon={<PlusOutlined />}
                  onClick={openPaletteFromEditor}
                >
                  添加步骤
                </Button>
                <Button
                  icon={<RobotOutlined />}
                  type={toolDrawerMode === 'ask-ai' ? 'primary' : 'default'}
                  disabled={!canAskAiGenerate}
                  onClick={openAskAiFromEditor}
                  title={!canAskAiGenerate ? askAiUnavailableMessage : undefined}
                >
                  AI 辅助
                </Button>
              </div>
            </div>

            <div
              style={{
                display: 'flex',
                flex: 1,
                minHeight: 0,
                padding: 12,
                gap: 12,
              }}
            >
              <div
                style={{
                  background: '#FAFAFA',
                  border: '1px solid #E8E2D9',
                  borderRadius: 24,
                  display: 'flex',
                  flexDirection: 'column',
                  flexShrink: 0,
                  minHeight: 0,
                  overflow: 'hidden',
                  width: 220,
                }}
              >
                <div
                  style={{
                    alignItems: 'center',
                    borderBottom: '1px solid #E8E2D9',
                    display: 'flex',
                    justifyContent: 'space-between',
                    padding: '12px 14px',
                  }}
                >
                  <Typography.Text strong>行为定义</Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    {visibleWorkflowDefinitions.length}
                  </Typography.Text>
                </div>
                <div
                  data-testid="studio-editor-definition-list"
                  style={studioDefinitionListScrollStyle}
                >
                  {workflows.isLoading ? (
                    <div style={{ padding: 14 }}>
                      <Typography.Text type="secondary">
                        正在加载行为定义...
                      </Typography.Text>
                    </div>
                  ) : workflows.isError ? (
                    <div style={{ padding: 14 }}>
                      <Typography.Text type="danger">
                        行为定义加载失败
                      </Typography.Text>
                    </div>
                  ) : visibleWorkflowDefinitions.length > 0 ? (
                    visibleWorkflowDefinitions.map((workflow) => {
                      const active = workflow.workflowId === selectedWorkflowId;
                      return (
                        <button
                          key={workflow.workflowId}
                          type="button"
                          onClick={() => onOpenWorkflow(workflow.workflowId)}
                          style={{
                            background: active ? '#E6F7FF' : 'transparent',
                            border: 'none',
                            borderLeft: active ? '3px solid #1890FF' : '3px solid transparent',
                            borderTop: '1px solid #F0F0F0',
                            cursor: 'pointer',
                            display: 'flex',
                            flexDirection: 'column',
                            gap: 4,
                            padding: '10px 14px 10px 12px',
                            textAlign: 'left',
                          }}
                        >
                          <Typography.Text strong ellipsis={{ tooltip: workflow.name }}>
                            {workflow.name}
                          </Typography.Text>
                          <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                            {workflow.stepCount} 步骤 ·{' '}
                            {formatDateTime(workflow.updatedAtUtc) || '刚刚更新'}
                          </Typography.Text>
                        </button>
                      );
                    })
                  ) : (
                    <div style={{ padding: 14 }}>
                      <Typography.Text type="secondary">
                        当前还没有可编辑的行为定义。
                      </Typography.Text>
                    </div>
                  )}
                </div>
                <div
                  style={{
                    borderTop: '1px solid #E8E2D9',
                    padding: 12,
                  }}
                >
                  <Button block onClick={onStartBlankDraft}>
                    + 新建定义
                  </Button>
                </div>
              </div>

              <div
                style={{
                  display: 'flex',
                  flex: 1,
                  gap: 12,
                  minHeight: 0,
                  minWidth: 0,
                }}
              >
                <div
                  data-testid="studio-editor-canvas-viewport"
                  style={{
                    ...studioCanvasViewportStyle,
                    minWidth: 0,
                  }}
                >
                  {selectedGraphEdge ? (
                    <div
                      style={{
                        bottom: 24,
                        left: 24,
                        position: 'absolute',
                        zIndex: 2,
                      }}
                    >
                      <div style={cardStackStyle}>
                        <div
                          style={{
                            backdropFilter: 'blur(12px)',
                            background: 'rgba(255, 255, 255, 0.96)',
                            border: '1px solid #E8E2D9',
                            borderRadius: 20,
                            boxShadow: '0 14px 32px rgba(17, 24, 39, 0.10)',
                            maxWidth: 320,
                            padding: '12px 14px',
                          }}
                        >
                          <div style={cardStackStyle}>
                            <Typography.Text strong>已选连接</Typography.Text>
                            <Typography.Text type="secondary">
                              {selectedGraphEdge.branchLabel
                                ? `${selectedGraphEdge.sourceStepId} -> ${selectedGraphEdge.targetStepId} (${selectedGraphEdge.branchLabel})`
                                : `${selectedGraphEdge.sourceStepId} -> ${selectedGraphEdge.targetStepId}`}
                            </Typography.Text>
                            <Space wrap size={[8, 8]}>
                              <Tag color={selectedGraphEdge.kind === 'branch' ? 'purple' : 'processing'}>
                                {selectedGraphEdge.kind === 'branch' ? '分支' : '下一步'}
                              </Tag>
                              {selectedGraphEdge.implicit ? <Tag>自动流转</Tag> : null}
                              <Button
                                danger
                                size="small"
                                disabled={selectedGraphEdge.implicit}
                                onClick={onDeleteSelectedGraphEdge}
                              >
                                移除连接
                              </Button>
                            </Space>
                          </div>
                        </div>
                      </div>
                    </div>
                  ) : null}

                  {canvasIsEmpty ? (
                    <div style={studioCanvasEmptyStateStyle}>
                      <div style={studioCanvasEmptyCardStyle}>
                        <Typography.Text strong style={{ display: 'block' }}>
                          当前定义还没有步骤。
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          先把第一个处理步骤放进画布，再继续补角色、连接和运行方式。
                        </Typography.Text>
                        <div style={studioCanvasEmptyActionsStyle}>
                          <Button
                            icon={<PlusOutlined />}
                            type="primary"
                            onClick={openPaletteFromEditor}
                          >
                            添加第一个步骤
                          </Button>
                          <Button
                            icon={<RobotOutlined />}
                            disabled={!canAskAiGenerate}
                            onClick={openAskAiFromEditor}
                          >
                            用 AI 生成初稿
                          </Button>
                        </div>
                      </div>
                    </div>
                  ) : null}

                  <GraphCanvas
                    nodes={workflowGraph.nodes}
                    edges={workflowGraph.edges}
                    height="100%"
                    variant="studio"
                    selectedNodeId={selectedGraphNodeId}
                    selectedEdgeId={selectedGraphEdge?.edgeId}
                    onNodeSelect={(nodeId) => {
                      onSelectGraphNode(nodeId);
                    }}
                    onEdgeSelect={(edgeId) => {
                      onSelectGraphEdge(edgeId);
                    }}
                    onCanvasSelect={() => {
                      onClearGraphSelection();
                    }}
                    onCanvasContextMenu={({ flowX, flowY }) => {
                      setPendingAddPosition({
                        x: flowX,
                        y: flowY,
                      });
                      setToolDrawerMode('palette');
                    }}
                    onConnectNodes={onConnectGraphNodes}
                    onNodeLayoutChange={onUpdateGraphLayout}
                  />
                </div>

                <div
                  data-testid="studio-inspector-shell"
                  style={{
                    background: '#FFFFFF',
                    border: '1px solid #E8E2D9',
                    borderRadius: 24,
                    display: 'flex',
                    flexDirection: 'column',
                    flexShrink: 0,
                    minHeight: 0,
                    overflow: 'hidden',
                    transition: 'width 0.18s ease',
                    width: inspectorPanelWidth,
                  }}
                >
                  <div
                    style={{
                      borderBottom: '1px solid #F0F0F0',
                      display: 'grid',
                      gridTemplateColumns: 'repeat(3, 1fr)',
                    }}
                  >
                    {[
                      { label: '步骤属性', value: 'node' as const },
                      { label: '角色', value: 'roles' as const },
                      { label: 'YAML', value: 'yaml' as const },
                    ].map((item) => {
                      const active = inspectorTab === item.value;
                      return (
                        <button
                          key={item.value}
                          type="button"
                          onClick={() => onSetInspectorTab(item.value)}
                          style={{
                            background: 'transparent',
                            border: 'none',
                            borderBottom: `2px solid ${active ? '#1890FF' : 'transparent'}`,
                            color: active ? '#1890FF' : '#8C8C8C',
                            cursor: 'pointer',
                            fontSize: 12,
                            padding: '10px 0',
                          }}
                        >
                          {item.label}
                        </button>
                      );
                    })}
                  </div>
                  <div
                    style={{
                      borderBottom: '1px solid #F5F5F5',
                      padding: '10px 12px',
                    }}
                  >
                    <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                      {inspectorTab === 'node'
                        ? hasSelectedGraphNode
                          ? `选中: ${selectedGraphNodeId}`
                          : '先选步骤，或切到角色 / YAML 继续编辑'
                        : inspectorTab === 'roles'
                          ? '管理当前定义的 Agent 角色'
                          : '查看和校验当前 YAML'}
                    </Typography.Text>
                  </div>
                  {inspectorPanelSurface}
                </div>
              </div>
            </div>
          </div>

          <Drawer
            open={toolDrawerVisible}
            title={toolDrawerTitle}
            placement="left"
            size={420}
            mask={false}
            onClose={closeToolDrawer}
            destroyOnClose={false}
            styles={{ body: drawerBodyStyle }}
          >
            <div style={drawerScrollStyle}>
              {toolDrawerMode === 'palette'
                ? nodePaletteDrawerContent
                : askAiDrawerContent}
            </div>
          </Drawer>

          <Modal
            open={gAgentModalOpen}
            title="绑定团队入口"
            onCancel={() => setGAgentModalOpen(false)}
            footer={[
              <Button
                key="cancel"
                onClick={() => setGAgentModalOpen(false)}
                disabled={gAgentBindingPending}
              >
                取消
              </Button>,
              <Button
                key="bind"
                onClick={() => void submitGAgentBinding(false)}
                loading={gAgentBindingPending}
                disabled={!resolvedScopeId}
              >
                仅绑定
              </Button>,
              <Button
                key="bind-open-runs"
                type="primary"
                onClick={() => void submitGAgentBinding(true)}
                loading={gAgentBindingPending}
                disabled={!resolvedScopeId}
              >
                绑定并打开测试运行
              </Button>,
            ]}
          >
            <div style={cardStackStyle}>
              <Alert
                type="info"
                showIcon
                message="先填 3 项就能绑定成功"
                description="团队入口名称、能力类型和默认入口用途是最常用的必填项。程序集名称、类型 URL 和载荷草稿都在高级设置里。"
              />
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>团队入口名称</Typography.Text>
                <Input
                  aria-label="入口展示名称"
                  placeholder="例如 NyxID Chat"
                  value={gAgentDraft.displayName}
                  onChange={(event) =>
                    setGAgentDraft((current) => ({
                      ...current,
                      displayName: event.target.value,
                    }))
                  }
                />
                <Typography.Text type="secondary">
                  这就是首页团队卡和详情页里默认显示的名称。
                </Typography.Text>
              </div>
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>能力类型</Typography.Text>
                <Typography.Text type="secondary">
                  先选一个可绑定的 GAgent 类型，系统会自动补一套推荐入口模板。
                </Typography.Text>
              </div>
              <Select
                aria-label="已发现入口类型"
                showSearch
                style={{ width: '100%' }}
                placeholder={
                  gAgentTypesLoading
                    ? '正在读取可绑定的入口类型'
                    : gAgentTypes.length > 0
                      ? '选择一个已发现的入口类型'
                      : '当前没有可绑定的入口类型'
                }
                value={
                  selectedDiscoveredGAgentType
                    ? buildRuntimeGAgentAssemblyQualifiedName(selectedDiscoveredGAgentType)
                    : undefined
                }
                optionFilterProp="label"
                options={gAgentTypes.map((descriptor) => ({
                  value: buildRuntimeGAgentAssemblyQualifiedName(descriptor),
                  label: buildRuntimeGAgentTypeLabel(descriptor),
                }))}
                notFoundContent={
                  gAgentTypesLoading ? '正在读取入口类型...' : '没有找到入口类型。'
                }
                onChange={(value) => {
                  const descriptor =
                    gAgentTypes.find(
                      (item) =>
                        buildRuntimeGAgentAssemblyQualifiedName(item) === value,
                    ) || null;
                  const preset = inferStudioGAgentEndpointPreset(value, descriptor);
                  setGAgentDraft((current) => ({
                    ...current,
                    actorTypeName: value,
                  }));
                  updatePrimaryGAgentEndpointPreset(preset);
                }}
              />
              {gAgentTypesError ? (
                <Typography.Text type="danger">
                  {describeError(gAgentTypesError)}
                </Typography.Text>
              ) : null}
              <Button
                type="link"
                style={{ alignSelf: 'flex-start', paddingInline: 0 }}
                onClick={() => setGAgentAdvancedOpen((current) => !current)}
              >
                {gAgentAdvancedOpen ? '收起高级设置' : '显示高级设置'}
              </Button>
              {gAgentAdvancedOpen ? (
                <>
                  <Input
                    aria-label="入口 Actor 类型"
                    placeholder="程序集限定 Actor 类型名"
                    value={gAgentDraft.actorTypeName}
                    onChange={(event) =>
                      setGAgentDraft((current) => ({
                        ...current,
                        actorTypeName: event.target.value,
                      }))
                    }
                  />
                  <Typography.Text type="secondary">
                    只有在下拉里找不到目标类型，或者你想手动指定程序集限定名时，才需要修改这一项。
                  </Typography.Text>
                </>
              ) : null}
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>默认入口用途</Typography.Text>
                <Select
                  aria-label="默认入口用途"
                  style={{ width: '100%' }}
                  value={primaryGAgentEndpoint?.kind === 'chat' ? 'chat' : 'command'}
                  options={[
                    {
                      label: '聊天对话',
                      value: 'chat',
                    },
                    {
                      label: '命令执行',
                      value: 'command',
                    },
                  ]}
                  onChange={(value) =>
                    updatePrimaryGAgentEndpointPreset(value as 'command' | 'chat')
                  }
                />
                <Typography.Text type="secondary">
                  {primaryGAgentEndpoint?.kind === 'chat'
                    ? '聊天入口会自动推荐入口 ID = chat，适合 NyxID Chat 这类对话型入口。'
                    : '命令入口会自动推荐入口 ID = run，适合执行一次命令或任务。'}
                </Typography.Text>
              </div>
              <Divider style={{ marginBlock: 8 }}>服务入口</Divider>
              <Space
                direction="vertical"
                size={12}
                style={{ width: '100%' }}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 12,
                  justifyContent: 'space-between',
                  }}
                >
                  <Button
                    icon={<PlusOutlined />}
                    onClick={() => addGAgentEndpointDraft()}
                  >
                    添加入口
                  </Button>
                </div>
                {gAgentDraft.endpoints.map((endpoint, endpointIndex) => (
                  <div
                    key={`gagent-endpoint-${endpointIndex}`}
                    style={{
                      border: '1px solid #E6E3DE',
                      borderRadius: 20,
                      display: 'grid',
                      gap: 8,
                      padding: 16,
                    }}
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 12,
                        justifyContent: 'space-between',
                      }}
                    >
                      <Typography.Text strong>
                        入口 {endpointIndex + 1}
                      </Typography.Text>
                      <Button
                        danger
                        disabled={gAgentDraft.endpoints.length <= 1}
                        icon={<DeleteOutlined />}
                        onClick={() =>
                          removeGAgentEndpointDraft(endpointIndex)
                        }
                      >
                        删除
                      </Button>
                    </div>
                    <div style={{ display: 'grid', gap: 8 }}>
                      <Typography.Text strong>入口 ID</Typography.Text>
                      <Input
                        aria-label={`入口 ID ${endpointIndex + 1}`}
                        placeholder="聊天入口推荐 chat，命令入口推荐 run"
                        value={endpoint.endpointId}
                        onChange={(event) =>
                          updateGAgentEndpointDraft(endpointIndex, {
                            endpointId: event.target.value,
                          })
                        }
                      />
                    </div>
                    <div style={{ display: 'grid', gap: 8 }}>
                      <Typography.Text strong>入口名称</Typography.Text>
                      <Input
                        aria-label={`入口展示名称 ${endpointIndex + 1}`}
                        placeholder="例如 Chat / Run"
                        value={endpoint.displayName}
                        onChange={(event) =>
                          updateGAgentEndpointDraft(endpointIndex, {
                            displayName: event.target.value,
                          })
                        }
                      />
                    </div>
                    <div style={{ display: 'grid', gap: 8 }}>
                      <Typography.Text strong>入口类型</Typography.Text>
                      <Select
                        aria-label={`入口类型 ${endpointIndex + 1}`}
                        options={[
                          {
                            label: '命令入口',
                            value: 'command',
                          },
                          {
                            label: '聊天入口',
                            value: 'chat',
                          },
                        ]}
                        value={endpoint.kind}
                        onChange={(value) =>
                          updateGAgentEndpointDraft(
                            endpointIndex,
                            applyStudioGAgentEndpointPreset(
                              endpoint,
                              value as 'command' | 'chat',
                            ),
                          )
                        }
                      />
                      <Typography.Text type="secondary">
                        {endpoint.kind === 'chat'
                          ? '聊天入口会直接打开聊天型测试运行。'
                          : '命令入口会把提示词或载荷草稿带入测试运行。'}
                      </Typography.Text>
                    </div>
                    {gAgentAdvancedOpen ? (
                      <>
                        <Input
                          aria-label={`请求类型 URL ${endpointIndex + 1}`}
                          placeholder="请求类型 URL"
                          value={endpoint.requestTypeUrl}
                          onChange={(event) =>
                            updateGAgentEndpointDraft(endpointIndex, {
                              requestTypeUrl: event.target.value,
                            })
                          }
                        />
                        <Input
                          aria-label={`响应类型 URL ${endpointIndex + 1}`}
                          placeholder="响应类型 URL（可选）"
                          value={endpoint.responseTypeUrl}
                          onChange={(event) =>
                            updateGAgentEndpointDraft(endpointIndex, {
                              responseTypeUrl: event.target.value,
                            })
                          }
                        />
                        <Input.TextArea
                          aria-label={`入口说明 ${endpointIndex + 1}`}
                          autoSize={{ minRows: 2, maxRows: 4 }}
                          placeholder="入口说明"
                          value={endpoint.description}
                          onChange={(event) =>
                            updateGAgentEndpointDraft(endpointIndex, {
                              description: event.target.value,
                            })
                          }
                        />
                      </>
                    ) : null}
                  </div>
                ))}
              </Space>
              {launchableGAgentEndpoints.length > 1 ? (
                <Select
                  aria-label="测试运行默认入口"
                  style={{ width: '100%' }}
                  placeholder="选择绑定后默认打开的测试运行入口"
                  value={selectedOpenRunsEndpoint?.endpointId || undefined}
                  options={launchableGAgentEndpoints.map((endpoint) => ({
                    value: endpoint.endpointId,
                    label: `${
                      endpoint.displayName.trim() || endpoint.endpointId.trim()
                    } (${endpoint.kind})`,
                  }))}
                  notFoundContent="先填写入口 ID，才能启用测试运行跳转。"
                  onChange={(value) =>
                    setGAgentDraft((current) => ({
                      ...current,
                      openRunsEndpointId: value,
                    }))
                  }
                />
              ) : selectedOpenRunsEndpoint ? (
                <Typography.Text type="secondary">
                  默认测试运行入口：{selectedOpenRunsEndpoint.displayName.trim() || selectedOpenRunsEndpoint.endpointId.trim()} ({selectedOpenRunsEndpoint.kind})
                </Typography.Text>
              ) : null}
              <Typography.Text type="secondary">
                {selectedOpenRunsEndpoint?.kind === 'chat'
                  ? '测试运行目前会通过特殊的 “chat” 入口 ID 直接打开聊天类入口。'
                  : '命令入口可以把一段文本提示，或者一份自定义载荷草稿带入测试运行。'}
              </Typography.Text>
              <Divider style={{ marginBlock: 8 }} />
              <Input.TextArea
                aria-label="测试运行默认提示词"
                autoSize={{ minRows: 4, maxRows: 8 }}
                placeholder="测试运行默认提示词"
                value={gAgentDraft.prompt}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    prompt: event.target.value,
                  }))
                }
              />
              <Input.TextArea
                aria-label="GAgent 载荷 Base64"
                autoSize={{ minRows: 3, maxRows: 6 }}
                placeholder="自定义请求类型的 Base64 载荷（可选）"
                value={gAgentDraft.payloadBase64}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    payloadBase64: event.target.value,
                  }))
                }
              />
            </div>
          </Modal>

          <Modal
            open={runModalOpen}
            title="测试运行"
            onCancel={() => setRunModalOpen(false)}
            onOk={() => {
              void onStartExecution();
              setRunModalOpen(false);
            }}
            okText="打开测试运行"
            okButtonProps={{
              disabled: !canRunWorkflow,
              loading: runPending,
              icon: <CaretRightFilled />,
            }}
          >
            <div style={cardStackStyle}>
              <Typography.Text type="secondary">
                Studio 会打开测试运行页面，并通过 <Typography.Text code>/api/scopes/{'{scopeId}'}/workflow/draft-run</Typography.Text> 执行当前草稿。
              </Typography.Text>
              <Input.TextArea
                aria-label="Studio 测试运行输入"
                autoSize={{ minRows: 6, maxRows: 10 }}
                placeholder="这次运行要做什么？"
                value={runPrompt}
                onChange={(event) => onRunPromptChange(event.target.value)}
              />
            </div>
          </Modal>

          {editorStatusItems.length > 0 ? (
            <div style={studioNoticeStripStyle}>{editorStatusItems}</div>
          ) : null}

          <StudioScopeBindingPanel
            scopeId={resolvedScopeId}
            binding={scopeBinding}
            loading={scopeBindingLoading}
            error={scopeBindingError}
            pendingRevisionId={bindingActivationRevisionId}
            pendingRetirementRevisionId={bindingRetirementRevisionId}
            onOpenBinding={openGAgentModal}
            onActivateRevision={onActivateBindingRevision}
            onRetireRevision={onRetireBindingRevision}
          />
        </>
      ) : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="Pick a workspace workflow, open Studio from the published workflow catalog, or start a blank draft."
        />
      )}
    </div>
  );
};

export type StudioCatalogDraftMeta = {
  readonly filePath: string;
  readonly fileExists: boolean;
  readonly updatedAtUtc: string | null;
};

export type StudioRoleCatalogItem = {
  readonly key: string;
  readonly id: string;
  readonly name: string;
  readonly systemPrompt: string;
  readonly provider: string;
  readonly model: string;
  readonly connectorsText: string;
};

export type StudioRoleDraftItem = Omit<StudioRoleCatalogItem, 'key'>;

export type StudioConnectorType = 'http' | 'cli' | 'mcp';

export type StudioConnectorCatalogItem = {
  readonly key: string;
  readonly name: string;
  readonly type: StudioConnectorType;
  readonly enabled: boolean;
  readonly timeoutMs: string;
  readonly retry: string;
  readonly http: {
    readonly baseUrl: string;
    readonly allowedMethods: string[];
    readonly allowedPaths: string[];
    readonly allowedInputKeys: string[];
    readonly defaultHeaders: Record<string, string>;
  };
  readonly cli: {
    readonly command: string;
    readonly fixedArguments: string[];
    readonly allowedOperations: string[];
    readonly allowedInputKeys: string[];
    readonly workingDirectory: string;
    readonly environment: Record<string, string>;
  };
  readonly mcp: {
    readonly serverName: string;
    readonly command: string;
    readonly arguments: string[];
    readonly environment: Record<string, string>;
    readonly defaultTool: string;
    readonly allowedTools: string[];
    readonly allowedInputKeys: string[];
  };
};

export type StudioConnectorDraftItem = Omit<StudioConnectorCatalogItem, 'key'>;

type StudioCatalogModalShellProps = {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly actions: React.ReactNode;
  readonly children: React.ReactNode;
};

const selectedCatalogSurfaceStyle: React.CSSProperties = {
  borderColor: 'var(--accent-border)',
  background: 'var(--accent-soft-end)',
};

const pageHeaderStyle: React.CSSProperties = {
  height: 88,
  flexShrink: 0,
  borderBottom: '1px solid #E6E3DE',
  background: 'rgba(255,255,255,0.92)',
  backdropFilter: 'blur(8px)',
  paddingInline: 24,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 16,
};

const sidebarMetaTextStyle: React.CSSProperties = {
  fontSize: 11,
  color: 'var(--ant-color-text-tertiary)',
  wordBreak: 'break-all',
};

const catalogListItemTitleStyle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 600,
  color: '#1f2937',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const catalogListItemMetaStyle: React.CSSProperties = {
  fontSize: 11,
  color: 'var(--ant-color-text-tertiary)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const catalogCardStackStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const catalogSectionTitleStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  fontSize: 13,
  fontWeight: 600,
  color: '#1f2937',
};

function StudioCatalogModalShell(props: StudioCatalogModalShellProps) {
  if (!props.open) {
    return null;
  }

  return (
    <div className="modal-overlay" onClick={props.onClose}>
      <div className="modal-shell" onClick={(event) => event.stopPropagation()}>
        <div className="modal-header">
          <div>
            <div className="panel-eyebrow">Catalog</div>
            <div className="panel-title">{props.title}</div>
          </div>
          <button
            type="button"
            onClick={props.onClose}
            title="Close dialog."
            className="panel-icon-button"
          >
            <CloseOutlined />
          </button>
        </div>
        <div className="modal-body">{props.children}</div>
        <div className="modal-footer">{props.actions}</div>
      </div>
    </div>
  );
}

function StudioCatalogInputField(props: {
  readonly inputId: string;
  readonly label: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly type?: 'text' | 'number';
}) {
  return (
    <div style={catalogCardStackStyle}>
      <label className="field-label" htmlFor={props.inputId}>
        {props.label}
      </label>
      <input
        id={props.inputId}
        type={props.type || 'text'}
        className="panel-input"
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
      />
    </div>
  );
}

function StudioCatalogTextAreaField(props: {
  readonly inputId: string;
  readonly label: string;
  readonly value: string;
  readonly rows?: number;
  readonly onChange: (value: string) => void;
}) {
  return (
    <div style={catalogCardStackStyle}>
      <label className="field-label" htmlFor={props.inputId}>
        {props.label}
      </label>
      <textarea
        id={props.inputId}
        rows={props.rows ?? 4}
        className="panel-textarea"
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
      />
    </div>
  );
}

function StudioCatalogEmptyPanel(props: {
  readonly icon: React.ReactNode;
  readonly title: string;
  readonly copy: string;
}) {
  return (
    <div className="studio-empty-panel">
      <div className="studio-empty-panel-icon">{props.icon}</div>
      <div className="studio-empty-panel-title">{props.title}</div>
      <div className="studio-empty-panel-copy">{props.copy}</div>
    </div>
  );
}

export type StudioRolesPageProps = {
  readonly roles: QueryState<{ readonly roles: StudioRoleDefinition[] }>;
  readonly appearanceTheme: string;
  readonly colorMode: string;
  readonly roleCatalogDraft: StudioRoleCatalogItem[];
  readonly roleCatalogMeta: {
    readonly filePath: string;
    readonly fileExists: boolean;
  };
  readonly roleCatalogIsRemote: boolean;
  readonly roleCatalogDirty: boolean;
  readonly roleCatalogPending: boolean;
  readonly roleCatalogNotice: StudioNoticeLike | null;
  readonly roleImportPending: boolean;
  readonly roleImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly roleSearch: string;
  readonly roleModalOpen: boolean;
  readonly roleDraft: StudioRoleDraftItem | null;
  readonly roleDraftMeta: StudioCatalogDraftMeta;
  readonly selectedRole: StudioRoleCatalogItem | null;
  readonly connectors: readonly { readonly name: string }[];
  readonly settingsProviders: readonly {
    readonly providerName: string;
    readonly model: string;
  }[];
  readonly onRoleSearchChange: (value: string) => void;
  readonly onOpenRoleModal: () => void;
  readonly onCloseRoleModal: () => void | Promise<void>;
  readonly onRoleDraftChange: (
    updater: (draft: StudioRoleDraftItem) => StudioRoleDraftItem,
  ) => void;
  readonly onSubmitRoleDraft: () => void | Promise<void>;
  readonly onRoleImportClick: () => void;
  readonly onRoleImportChange: React.ChangeEventHandler<HTMLInputElement>;
  readonly onSaveRoles: () => void;
  readonly onSelectRoleKey: (roleKey: string) => void;
  readonly onDeleteRole: (roleKey: string) => void;
  readonly onApplyRoleToWorkflow: (roleKey: string) => void | Promise<void>;
  readonly onUpdateRoleCatalog: (
    roleKey: string,
    updater: (role: StudioRoleCatalogItem) => StudioRoleCatalogItem,
  ) => void;
};

export const StudioRolesPage: React.FC<StudioRolesPageProps> = ({
  roles,
  appearanceTheme,
  colorMode,
  roleCatalogDraft,
  roleCatalogMeta,
  roleCatalogIsRemote,
  roleCatalogPending,
  roleCatalogNotice,
  roleImportPending,
  roleImportInputRef,
  roleSearch,
  roleModalOpen,
  roleDraft,
  roleDraftMeta,
  selectedRole,
  connectors,
  settingsProviders,
  onRoleSearchChange,
  onOpenRoleModal,
  onCloseRoleModal,
  onRoleDraftChange,
  onSubmitRoleDraft,
  onRoleImportClick,
  onRoleImportChange,
  onSaveRoles,
  onSelectRoleKey,
  onDeleteRole,
  onApplyRoleToWorkflow,
  onUpdateRoleCatalog,
}) => {
  const filteredRoleCatalog = roleCatalogDraft.filter((role) => {
    const normalizedQuery = roleSearch.trim().toLowerCase();
    if (!normalizedQuery) {
      return true;
    }

    return [
      role.id,
      role.name,
      role.systemPrompt,
      role.provider,
      role.model,
      role.connectorsText,
    ]
      .join(' ')
      .toLowerCase()
      .includes(normalizedQuery);
  });

  return (
    <div
      className="studio-shell studio-catalog-page"
      data-appearance={appearanceTheme || 'blue'}
      data-color-mode={colorMode || 'light'}
    >
      <header className="workspace-page-header" style={pageHeaderStyle}>
        <div>
          <div className="panel-eyebrow">Catalog</div>
          <div className="panel-title">Roles</div>
        </div>
      </header>

      <section className="studio-catalog-page-body">
        <aside className="studio-catalog-sidebar">
          <div style={{ ...catalogCardStackStyle, marginBottom: 16 }}>
            <div className="catalog-sidebar-actions">
              <input
                ref={roleImportInputRef}
                type="file"
                accept=".json,application/json"
                hidden
                onChange={onRoleImportChange}
              />
              <button
                type="button"
                onClick={onOpenRoleModal}
                className="solid-action"
                style={{ flex: 1 }}
              >
                <PlusOutlined />
                Add role
              </button>
              <button
                type="button"
                onClick={onSaveRoles}
                className="ghost-action catalog-save-action"
                disabled={roleCatalogPending}
              >
                Save
              </button>
              <button
                type="button"
                onClick={onRoleImportClick}
                className="ghost-action catalog-save-action"
                disabled={roleImportPending}
              >
                <UploadOutlined />
                {roleImportPending ? 'Importing...' : 'Import'}
              </button>
            </div>

            <div className="search-field">
              <SearchOutlined style={{ color: 'var(--ant-color-text-tertiary)' }} />
              <input
                className="search-input"
                placeholder="Search roles"
                value={roleSearch}
                onChange={(event) => onRoleSearchChange(event.target.value)}
              />
            </div>

            {roleCatalogMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>
                {roleCatalogIsRemote
                  ? `${roleCatalogMeta.fileExists ? 'Remote object' : 'Remote object pending'} · ${roleCatalogMeta.filePath}`
                  : `${roleCatalogMeta.fileExists ? 'File' : 'Will create'} · ${roleCatalogMeta.filePath}`}
              </div>
            ) : null}

            {roleDraftMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>Draft · {roleDraftMeta.filePath}</div>
            ) : null}
          </div>

          {roles.isError ? (
            <div className="empty-card">{describeError(roles.error)}</div>
          ) : roles.isLoading ? (
            <div className="empty-card">Loading roles...</div>
          ) : (
            <div className="studio-catalog-list">
              {filteredRoleCatalog.length === 0 ? (
                <div className="empty-card">No roles matched</div>
              ) : (
                filteredRoleCatalog.map((role) => (
                  <button
                    key={role.key}
                    type="button"
                    onClick={() => onSelectRoleKey(role.key)}
                    className={`studio-catalog-item ${selectedRole?.key === role.key ? 'active' : ''}`}
                    style={
                      selectedRole?.key === role.key
                        ? selectedCatalogSurfaceStyle
                        : undefined
                    }
                  >
                    <div
                      style={{
                        display: 'flex',
                        justifyContent: 'space-between',
                        alignItems: 'center',
                        gap: 8,
                      }}
                    >
                      <div style={catalogListItemTitleStyle}>
                        {role.name || role.id || 'Role'}
                      </div>
                      <span style={{ ...catalogListItemMetaStyle, textTransform: 'uppercase', letterSpacing: '0.08em' }}>
                        {role.id || 'role'}
                      </span>
                    </div>
                    <div style={{ ...catalogListItemMetaStyle, marginTop: 4 }}>
                      {role.provider || 'default'}
                      {role.model ? ` · ${role.model}` : ''}
                    </div>
                  </button>
                ))
              )}
            </div>
          )}
        </aside>

        <div className="studio-catalog-content">
          {selectedRole ? (
            <div className="studio-catalog-detail-card" style={catalogCardStackStyle}>
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'flex-start',
                  gap: 12,
                }}
              >
                <div>
                  <div className="studio-catalog-title">
                    {selectedRole.name || selectedRole.id || 'Role'}
                  </div>
                  <div className="studio-catalog-subtitle" style={{ marginTop: 4 }}>
                    {selectedRole.id || 'role_id'}
                  </div>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <button
                    type="button"
                    onClick={() => void onApplyRoleToWorkflow(selectedRole.key)}
                    className="ghost-action"
                  >
                    Use in workflow
                  </button>
                  <button
                    type="button"
                    onClick={() => onDeleteRole(selectedRole.key)}
                    title="Delete role."
                    className="panel-icon-button"
                    style={{ color: '#ef4444' }}
                  >
                    <DeleteOutlined />
                  </button>
                </div>
              </div>

              <div className="studio-catalog-grid-two">
                <StudioCatalogInputField
                  inputId="studio-role-id"
                  label="Role ID"
                  value={selectedRole.id}
                  onChange={(value) =>
                    onUpdateRoleCatalog(selectedRole.key, (role) => ({
                      ...role,
                      id: value,
                    }))
                  }
                />
                <StudioCatalogInputField
                  inputId="studio-role-name"
                  label="Role name"
                  value={selectedRole.name}
                  onChange={(value) =>
                    onUpdateRoleCatalog(selectedRole.key, (role) => ({
                      ...role,
                      name: value,
                    }))
                  }
                />
              </div>

              <div className="studio-catalog-grid-two">
                <div style={catalogCardStackStyle}>
                  <label className="field-label" htmlFor="studio-role-provider">
                    Provider
                  </label>
                  <select
                    id="studio-role-provider"
                    className="panel-input"
                    value={selectedRole.provider}
                    onChange={(event) => {
                      const nextProviderName = event.target.value;
                      const configuredProvider = settingsProviders.find(
                        (provider) => provider.providerName === nextProviderName,
                      );
                      onUpdateRoleCatalog(selectedRole.key, (role) => ({
                        ...role,
                        provider: nextProviderName,
                        model: configuredProvider?.model || role.model,
                      }));
                    }}
                  >
                    <option value="">Default</option>
                    {settingsProviders.map((provider) => (
                      <option
                        key={provider.providerName}
                        value={provider.providerName}
                      >
                        {provider.providerName}
                      </option>
                    ))}
                  </select>
                </div>

                <StudioCatalogInputField
                  inputId="studio-role-model"
                  label="Model"
                  value={selectedRole.model}
                  onChange={(value) =>
                    onUpdateRoleCatalog(selectedRole.key, (role) => ({
                      ...role,
                      model: value,
                    }))
                  }
                />
              </div>

              <StudioCatalogTextAreaField
                inputId="studio-role-system-prompt"
                label="System prompt"
                value={selectedRole.systemPrompt}
                rows={8}
                onChange={(value) =>
                  onUpdateRoleCatalog(selectedRole.key, (role) => ({
                    ...role,
                    systemPrompt: value,
                  }))
                }
              />

              <div style={catalogCardStackStyle}>
                <div className="field-label">Allowed connectors</div>
                <div
                  style={{
                    display: 'flex',
                    flexWrap: 'wrap',
                    gap: 8,
                  }}
                >
                  {connectors.length === 0 ? (
                    <div className="studio-catalog-muted">
                      No connectors configured
                    </div>
                  ) : (
                    connectors.map((connector) => {
                      const active = parseListText(
                        selectedRole.connectorsText,
                      ).includes(connector.name);
                      return (
                        <button
                          key={`${selectedRole.key}:${connector.name}`}
                          type="button"
                          onClick={() =>
                            onUpdateRoleCatalog(selectedRole.key, (role) => {
                              const nextValues = new Set(
                                parseListText(role.connectorsText),
                              );
                              if (nextValues.has(connector.name)) {
                                nextValues.delete(connector.name);
                              } else {
                                nextValues.add(connector.name);
                              }

                              return {
                                ...role,
                                connectorsText: Array.from(nextValues).join('\n'),
                              };
                            })
                          }
                          className={`chip-button ${active ? 'chip-button-active' : ''}`}
                        >
                          {connector.name}
                        </button>
                      );
                    })
                  )}
                </div>
              </div>
            </div>
          ) : (
            <div style={{ maxWidth: 420 }}>
              <StudioCatalogEmptyPanel
                icon={<UserOutlined />}
                title="No role selected"
                copy="Create a role or pick one from the catalog."
              />
            </div>
          )}
        </div>
      </section>

      <StudioCatalogModalShell
        open={roleModalOpen}
        title="Add Role"
        onClose={() => {
          void onCloseRoleModal();
        }}
        actions={
          <>
            <button
              type="button"
              onClick={() => {
                void onCloseRoleModal();
              }}
              className="ghost-action"
            >
              Close
            </button>
            <button
              type="button"
              onClick={() => {
                void onSubmitRoleDraft();
              }}
              className="solid-action"
            >
              <PlusOutlined />
              Add role
            </button>
          </>
        }
      >
        {roleDraft ? (
          <div style={catalogCardStackStyle}>
            <div className="studio-catalog-muted">
              The latest unfinished role draft is stored automatically when you
              close this dialog.
            </div>

            <div className="studio-catalog-grid-two">
              <StudioCatalogInputField
                inputId="studio-role-draft-id"
                label="Role ID"
                value={roleDraft.id}
                onChange={(value) =>
                  onRoleDraftChange((draft) => ({ ...draft, id: value }))
                }
              />
              <StudioCatalogInputField
                inputId="studio-role-draft-name"
                label="Role name"
                value={roleDraft.name}
                onChange={(value) =>
                  onRoleDraftChange((draft) => ({ ...draft, name: value }))
                }
              />
            </div>

            <div className="studio-catalog-grid-two">
              <div style={catalogCardStackStyle}>
                <label
                  className="field-label"
                  htmlFor="studio-role-draft-provider"
                >
                  Provider
                </label>
                <select
                  id="studio-role-draft-provider"
                  className="panel-input"
                  value={roleDraft.provider}
                  onChange={(event) => {
                    const nextProviderName = event.target.value;
                    const configuredProvider = settingsProviders.find(
                      (provider) => provider.providerName === nextProviderName,
                    );
                    onRoleDraftChange((draft) => ({
                      ...draft,
                      provider: nextProviderName,
                      model: configuredProvider?.model || draft.model,
                    }));
                  }}
                >
                  <option value="">Default</option>
                  {settingsProviders.map((provider) => (
                    <option
                      key={provider.providerName}
                      value={provider.providerName}
                    >
                      {provider.providerName}
                    </option>
                  ))}
                </select>
              </div>

              <StudioCatalogInputField
                inputId="studio-role-draft-model"
                label="Model"
                value={roleDraft.model}
                onChange={(value) =>
                  onRoleDraftChange((draft) => ({ ...draft, model: value }))
                }
              />
            </div>

            <StudioCatalogTextAreaField
              inputId="studio-role-draft-system-prompt"
              label="System prompt"
              value={roleDraft.systemPrompt}
              rows={6}
              onChange={(value) =>
                onRoleDraftChange((draft) => ({
                  ...draft,
                  systemPrompt: value,
                }))
              }
            />

            <StudioCatalogTextAreaField
              inputId="studio-role-draft-connectors"
              label="Allowed connectors"
              value={roleDraft.connectorsText}
              rows={4}
              onChange={(value) =>
                onRoleDraftChange((draft) => ({
                  ...draft,
                  connectorsText: value,
                }))
              }
            />
          </div>
        ) : null}
      </StudioCatalogModalShell>

      {roleCatalogNotice ? (
        <div className={`studio-catalog-toast ${roleCatalogNotice.type}`}>
          {roleCatalogNotice.message}
        </div>
      ) : null}
    </div>
  );
};

export type StudioConnectorsPageProps = {
  readonly connectors: QueryState<{ readonly connectors: StudioConnectorDefinition[] }>;
  readonly appearanceTheme: string;
  readonly colorMode: string;
  readonly connectorCatalogDraft: StudioConnectorCatalogItem[];
  readonly connectorCatalogMeta: {
    readonly filePath: string;
    readonly fileExists: boolean;
  };
  readonly connectorCatalogIsRemote: boolean;
  readonly connectorCatalogDirty: boolean;
  readonly connectorCatalogPending: boolean;
  readonly connectorImportPending: boolean;
  readonly connectorCatalogNotice: StudioNoticeLike | null;
  readonly connectorImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly connectorSearch: string;
  readonly connectorModalOpen: boolean;
  readonly connectorDraft: StudioConnectorDraftItem | null;
  readonly connectorDraftMeta: StudioCatalogDraftMeta;
  readonly selectedConnector: StudioConnectorCatalogItem | null;
  readonly onConnectorSearchChange: (value: string) => void;
  readonly onOpenConnectorModal: () => void;
  readonly onCloseConnectorModal: () => void | Promise<void>;
  readonly onConnectorDraftChange: (
    updater: (draft: StudioConnectorDraftItem) => StudioConnectorDraftItem,
  ) => void;
  readonly onSubmitConnectorDraft: () => void | Promise<void>;
  readonly onConnectorImportClick: () => void;
  readonly onConnectorImportChange: React.ChangeEventHandler<HTMLInputElement>;
  readonly onSaveConnectors: () => void;
  readonly onSelectConnectorKey: (connectorKey: string) => void;
  readonly onDeleteConnector: (connectorKey: string) => void;
  readonly onUpdateConnectorCatalog: (
    connectorKey: string,
    updater: (
      connector: StudioConnectorCatalogItem,
    ) => StudioConnectorCatalogItem,
  ) => void;
};

export const StudioConnectorsPage: React.FC<StudioConnectorsPageProps> = ({
  connectors,
  appearanceTheme,
  colorMode,
  connectorCatalogDraft,
  connectorCatalogMeta,
  connectorCatalogIsRemote,
  connectorCatalogPending,
  connectorImportPending,
  connectorCatalogNotice,
  connectorImportInputRef,
  connectorSearch,
  connectorModalOpen,
  connectorDraft,
  connectorDraftMeta,
  selectedConnector,
  onConnectorSearchChange,
  onOpenConnectorModal,
  onCloseConnectorModal,
  onConnectorDraftChange,
  onSubmitConnectorDraft,
  onConnectorImportClick,
  onConnectorImportChange,
  onSaveConnectors,
  onSelectConnectorKey,
  onDeleteConnector,
  onUpdateConnectorCatalog,
}) => {
  const filteredConnectorList = connectorCatalogDraft.filter((connector) => {
    const normalizedQuery = connectorSearch.trim().toLowerCase();
    if (!normalizedQuery) {
      return true;
    }

    return [
      connector.name,
      connector.type,
      connector.http.baseUrl,
      connector.cli.command,
      connector.mcp.serverName,
      connector.mcp.command,
    ]
      .join(' ')
      .toLowerCase()
      .includes(normalizedQuery);
  });

  return (
    <div
      className="studio-shell studio-catalog-page"
      data-appearance={appearanceTheme || 'blue'}
      data-color-mode={colorMode || 'light'}
    >
      <header className="workspace-page-header" style={pageHeaderStyle}>
        <div>
          <div className="panel-eyebrow">Catalog</div>
          <div className="panel-title">Connectors</div>
        </div>
      </header>

      <section className="studio-catalog-page-body">
        <aside className="studio-catalog-sidebar">
          <div style={{ ...catalogCardStackStyle, marginBottom: 16 }}>
            <div className="catalog-sidebar-actions">
              <input
                ref={connectorImportInputRef}
                type="file"
                accept=".json,application/json"
                hidden
                onChange={onConnectorImportChange}
              />
              <button
                type="button"
                onClick={onOpenConnectorModal}
                className="solid-action"
                style={{ flex: 1 }}
              >
                <PlusOutlined />
                Add connector
              </button>
              <button
                type="button"
                onClick={onSaveConnectors}
                className="ghost-action catalog-save-action"
                disabled={connectorCatalogPending}
              >
                Save
              </button>
              <button
                type="button"
                onClick={onConnectorImportClick}
                className="ghost-action catalog-save-action"
                disabled={connectorImportPending}
              >
                <UploadOutlined />
                {connectorImportPending ? 'Importing...' : 'Import'}
              </button>
            </div>

            <div className="search-field">
              <SearchOutlined style={{ color: 'var(--ant-color-text-tertiary)' }} />
              <input
                className="search-input"
                placeholder="Search connectors"
                value={connectorSearch}
                onChange={(event) => onConnectorSearchChange(event.target.value)}
              />
            </div>

            {connectorCatalogMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>
                {connectorCatalogIsRemote
                  ? `${connectorCatalogMeta.fileExists ? 'Remote object' : 'Remote object pending'} · ${connectorCatalogMeta.filePath}`
                  : `${connectorCatalogMeta.fileExists ? 'File' : 'Will create'} · ${connectorCatalogMeta.filePath}`}
              </div>
            ) : null}

            {connectorDraftMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>
                Draft · {connectorDraftMeta.filePath}
              </div>
            ) : null}
          </div>

          {connectors.isError ? (
            <div className="empty-card">{describeError(connectors.error)}</div>
          ) : connectors.isLoading ? (
            <div className="empty-card">Loading connectors...</div>
          ) : (
            <div className="studio-catalog-list">
              {filteredConnectorList.length === 0 ? (
                <div className="empty-card">No connectors matched</div>
              ) : (
                filteredConnectorList.map((connector) => (
                  <button
                    key={connector.key}
                    type="button"
                    onClick={() => onSelectConnectorKey(connector.key)}
                    className={`studio-catalog-item ${selectedConnector?.key === connector.key ? 'active' : ''}`}
                    style={
                      selectedConnector?.key === connector.key
                        ? selectedCatalogSurfaceStyle
                        : undefined
                    }
                  >
                    <div
                      style={{
                        display: 'flex',
                        justifyContent: 'space-between',
                        alignItems: 'center',
                        gap: 8,
                      }}
                    >
                      <div style={catalogListItemTitleStyle}>
                        {connector.name || 'New connector'}
                      </div>
                      <span style={{ ...catalogListItemMetaStyle, textTransform: 'uppercase', letterSpacing: '0.08em' }}>
                        {connector.type}
                      </span>
                    </div>
                    <div style={{ ...catalogListItemMetaStyle, marginTop: 4 }}>
                      {connector.type === 'http'
                        ? connector.http.baseUrl || 'HTTP connector'
                        : connector.type === 'cli'
                          ? connector.cli.command || 'CLI connector'
                          : connector.mcp.serverName || connector.mcp.command || 'MCP connector'}
                    </div>
                  </button>
                ))
              )}
            </div>
          )}
        </aside>

        <div className="studio-catalog-content">
          {selectedConnector ? (
            <div className="studio-catalog-detail-card" style={catalogCardStackStyle}>
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'flex-start',
                  gap: 12,
                }}
              >
                <div>
                  <div className="studio-catalog-title">
                    {selectedConnector.name || 'Connector'}
                  </div>
                  <div className="studio-catalog-subtitle" style={{ marginTop: 4 }}>
                    {selectedConnector.type.toUpperCase()}
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => onDeleteConnector(selectedConnector.key)}
                  title="Delete connector."
                  className="panel-icon-button"
                  style={{ color: '#ef4444' }}
                >
                  <DeleteOutlined />
                </button>
              </div>

              <StudioCatalogInputField
                inputId="studio-connector-name"
                label="Name"
                value={selectedConnector.name}
                onChange={(value) =>
                  onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                    ...connector,
                    name: value,
                  }))
                }
              />

              <div style={catalogCardStackStyle}>
                <label className="field-label" htmlFor="studio-connector-type">
                  Type
                </label>
                <select
                  id="studio-connector-type"
                  className="panel-input"
                  value={selectedConnector.type}
                  onChange={(event) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      type: event.target.value as StudioConnectorType,
                    }))
                  }
                >
                  <option value="http">HTTP</option>
                  <option value="cli">CLI</option>
                  <option value="mcp">MCP</option>
                </select>
              </div>

              <div className="studio-catalog-grid-two">
                <StudioCatalogInputField
                  inputId="studio-connector-timeout"
                  label="Timeout"
                  value={selectedConnector.timeoutMs}
                  onChange={(value) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      timeoutMs: value,
                    }))
                  }
                />
                <StudioCatalogInputField
                  inputId="studio-connector-retry"
                  label="Retry"
                  value={selectedConnector.retry}
                  onChange={(value) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      retry: value,
                    }))
                  }
                />
              </div>

              <label
                className={`studio-catalog-toggle ${selectedConnector.enabled ? '' : 'disabled'}`}
              >
                <input
                  type="checkbox"
                  checked={selectedConnector.enabled}
                  onChange={(event) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      enabled: event.target.checked,
                    }))
                  }
                />
                Enabled
              </label>

              {selectedConnector.type === 'http' ? (
                <div className="studio-catalog-section-card">
                  <div style={catalogSectionTitleStyle}>
                    <ApiOutlined />
                    HTTP
                  </div>
                  <StudioCatalogInputField
                    inputId="studio-connector-http-base-url"
                    label="Base URL"
                    value={selectedConnector.http.baseUrl}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        http: {
                          ...connector.http,
                          baseUrl: value,
                        },
                      }))
                    }
                  />
                  <StudioCatalogTextAreaField
                    inputId="studio-connector-http-methods"
                    label="Allowed methods"
                    value={selectedConnector.http.allowedMethods.join('\n')}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        http: {
                          ...connector.http,
                          allowedMethods: parseListText(value).map((item) =>
                            item.toUpperCase(),
                          ),
                        },
                      }))
                    }
                  />
                  <StudioCatalogTextAreaField
                    inputId="studio-connector-http-paths"
                    label="Allowed paths"
                    value={selectedConnector.http.allowedPaths.join('\n')}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        http: {
                          ...connector.http,
                          allowedPaths: parseListText(value),
                        },
                      }))
                    }
                  />
                </div>
              ) : null}

              {selectedConnector.type === 'cli' ? (
                <div className="studio-catalog-section-card">
                  <div style={catalogSectionTitleStyle}>
                    <CodeOutlined />
                    CLI
                  </div>
                  <StudioCatalogInputField
                    inputId="studio-connector-cli-command"
                    label="Command"
                    value={selectedConnector.cli.command}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        cli: {
                          ...connector.cli,
                          command: value,
                        },
                      }))
                    }
                  />
                  <StudioCatalogTextAreaField
                    inputId="studio-connector-cli-fixed-arguments"
                    label="Fixed arguments"
                    value={selectedConnector.cli.fixedArguments.join('\n')}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        cli: {
                          ...connector.cli,
                          fixedArguments: parseListText(value),
                        },
                      }))
                    }
                  />
                </div>
              ) : null}

              {selectedConnector.type === 'mcp' ? (
                <div className="studio-catalog-section-card">
                  <div style={catalogSectionTitleStyle}>
                    <AppstoreOutlined />
                    MCP
                  </div>
                  <StudioCatalogInputField
                    inputId="studio-connector-mcp-server-name"
                    label="Server name"
                    value={selectedConnector.mcp.serverName}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        mcp: {
                          ...connector.mcp,
                          serverName: value,
                        },
                      }))
                    }
                  />
                  <StudioCatalogInputField
                    inputId="studio-connector-mcp-command"
                    label="Command"
                    value={selectedConnector.mcp.command}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        mcp: {
                          ...connector.mcp,
                          command: value,
                        },
                      }))
                    }
                  />
                </div>
              ) : null}
            </div>
          ) : (
            <div style={{ maxWidth: 420 }}>
              <StudioCatalogEmptyPanel
                icon={<ApiOutlined />}
                title="No connector selected"
                copy="Create a connector or pick one from the catalog."
              />
            </div>
          )}
        </div>
      </section>

      <StudioCatalogModalShell
        open={connectorModalOpen}
        title="Add Connector"
        onClose={() => {
          void onCloseConnectorModal();
        }}
        actions={
          <>
            <button
              type="button"
              onClick={() => {
                void onCloseConnectorModal();
              }}
              className="ghost-action"
            >
              Close
            </button>
            <button
              type="button"
              onClick={() => {
                void onSubmitConnectorDraft();
              }}
              className="solid-action"
            >
              <PlusOutlined />
              Add connector
            </button>
          </>
        }
      >
        {connectorDraft ? (
          <div style={catalogCardStackStyle}>
            <div className="studio-catalog-muted">
              Close this dialog at any time and the latest text will be kept as
              a draft.
            </div>

            <div style={catalogCardStackStyle}>
              <label
                className="field-label"
                htmlFor="studio-connector-draft-type"
              >
                Type
              </label>
              <select
                id="studio-connector-draft-type"
                className="panel-input"
                value={connectorDraft.type}
                onChange={(event) =>
                  onConnectorDraftChange((draft) => ({
                    ...draft,
                    type: event.target.value as StudioConnectorType,
                  }))
                }
              >
                <option value="http">HTTP</option>
                <option value="cli">CLI</option>
                <option value="mcp">MCP</option>
              </select>
            </div>

            <StudioCatalogInputField
              inputId="studio-connector-draft-name"
              label="Name"
              value={connectorDraft.name}
              onChange={(value) =>
                onConnectorDraftChange((draft) => ({ ...draft, name: value }))
              }
            />

            {connectorDraft.type === 'http' ? (
              <div className="studio-catalog-section-card">
                <StudioCatalogInputField
                  inputId="studio-connector-draft-http-base-url"
                  label="Base URL"
                  value={connectorDraft.http.baseUrl}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      http: {
                        ...draft.http,
                        baseUrl: value,
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-http-methods"
                  label="Allowed methods"
                  rows={3}
                  value={connectorDraft.http.allowedMethods.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      http: {
                        ...draft.http,
                        allowedMethods: parseListText(value).map((item) =>
                          item.toUpperCase(),
                        ),
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-http-paths"
                  label="Allowed paths"
                  rows={3}
                  value={connectorDraft.http.allowedPaths.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      http: {
                        ...draft.http,
                        allowedPaths: parseListText(value),
                      },
                    }))
                  }
                />
              </div>
            ) : null}

            {connectorDraft.type === 'cli' ? (
              <div className="studio-catalog-section-card">
                <StudioCatalogInputField
                  inputId="studio-connector-draft-cli-command"
                  label="Command"
                  value={connectorDraft.cli.command}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      cli: {
                        ...draft.cli,
                        command: value,
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-cli-fixed-arguments"
                  label="Fixed arguments"
                  rows={3}
                  value={connectorDraft.cli.fixedArguments.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      cli: {
                        ...draft.cli,
                        fixedArguments: parseListText(value),
                      },
                    }))
                  }
                />
              </div>
            ) : null}

            {connectorDraft.type === 'mcp' ? (
              <div className="studio-catalog-section-card">
                <StudioCatalogInputField
                  inputId="studio-connector-draft-mcp-server-name"
                  label="Server name"
                  value={connectorDraft.mcp.serverName}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      mcp: {
                        ...draft.mcp,
                        serverName: value,
                      },
                    }))
                  }
                />
                <StudioCatalogInputField
                  inputId="studio-connector-draft-mcp-command"
                  label="Command"
                  value={connectorDraft.mcp.command}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      mcp: {
                        ...draft.mcp,
                        command: value,
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-mcp-arguments"
                  label="Arguments"
                  rows={3}
                  value={connectorDraft.mcp.arguments.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      mcp: {
                        ...draft.mcp,
                        arguments: parseListText(value),
                      },
                    }))
                  }
                />
              </div>
            ) : null}
          </div>
        ) : null}
      </StudioCatalogModalShell>

      {connectorCatalogNotice ? (
        <div className={`studio-catalog-toast ${connectorCatalogNotice.type}`}>
          {connectorCatalogNotice.message}
        </div>
      ) : null}
    </div>
  );
};

export type StudioSettingsPageProps = {
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly settings: QueryState<unknown>;
  readonly settingsDraft: StudioSettingsDraftLike | null;
  readonly selectedProvider: StudioProviderSettings | null;
  readonly hostMode: 'embedded' | 'proxy';
  readonly workflowStorageMode: 'workspace' | 'scope';
  readonly settingsDirty: boolean;
  readonly settingsPending: boolean;
  readonly runtimeTestPending: boolean;
  readonly settingsNotice: StudioNoticeLike | null;
  readonly runtimeTestResult: StudioRuntimeTestResult | null;
  readonly directoryPath: string;
  readonly directoryLabel: string;
  readonly onSaveSettings: () => void;
  readonly onTestRuntime: () => void;
  readonly onSetSettingsDraft: React.Dispatch<
    React.SetStateAction<StudioSettingsDraftLike | null>
  >;
  readonly onAddProvider: () => void;
  readonly onSelectProviderName: (providerName: string) => void;
  readonly onDeleteSelectedProvider: () => void;
  readonly onSetDefaultProvider: () => void;
  readonly onSetDirectoryPath: (value: string) => void;
  readonly onSetDirectoryLabel: (value: string) => void;
  readonly onAddDirectory: () => void;
  readonly onRemoveDirectory: (directoryId: string) => void;
};

type StudioSettingsSectionKey =
  | 'runtime'
  | 'providers'
  | 'sources'
  | 'advanced';

export const StudioSettingsPage: React.FC<StudioSettingsPageProps> = ({
  workspaceSettings,
  settings,
  settingsDraft,
  selectedProvider,
  hostMode,
  workflowStorageMode,
  settingsDirty,
  settingsPending,
  runtimeTestPending,
  settingsNotice,
  runtimeTestResult,
  directoryPath,
  directoryLabel,
  onSaveSettings,
  onTestRuntime,
  onSetSettingsDraft,
  onAddProvider,
  onSelectProviderName,
  onDeleteSelectedProvider,
  onSetDefaultProvider,
  onSetDirectoryPath,
  onSetDirectoryLabel,
  onAddDirectory,
  onRemoveDirectory,
}) => {
  const canEditRuntime = hostMode === 'proxy';
  const canManageDirectories = workflowStorageMode === 'workspace';
  const runtimeBaseUrl =
    settingsDraft?.runtimeBaseUrl ?? workspaceSettings.data?.runtimeBaseUrl ?? '';
  const runtimeFieldLabel = canEditRuntime
    ? '运行时地址'
    : '宿主管理运行时地址';
  const runtimeActionLabel = canEditRuntime
    ? '测试运行时'
    : '检测宿主运行时';
  const [activeSection, setActiveSection] =
    React.useState<StudioSettingsSectionKey>('runtime');
  const providerFormGridStyle: React.CSSProperties = {
    display: 'grid',
    gap: 12,
    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
  };
  const providerColumnStyle: React.CSSProperties = {
    minHeight: 'fit-content',
  };
  const providersGridStyle: React.CSSProperties = {
    display: 'grid',
    gap: 16,
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    alignItems: 'start',
  };
  const sectionPanelStyle: React.CSSProperties = {
    ...embeddedPanelStyle,
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
  };
  const providers = settingsDraft?.providers ?? [];
  const providerTypes = settingsDraft?.providerTypes ?? [];
  const providerTypeLabels = React.useMemo(
    () =>
      new Map(
        providerTypes.map((type) => [type.id, type.displayName] as const),
      ),
    [providerTypes],
  );
  const resolveProviderTypeLabel = (providerType: string) =>
    providerTypeLabels.get(providerType) ?? providerType;
  const selectedProviderIsDefault = Boolean(
    selectedProvider &&
      settingsDraft?.defaultProviderName === selectedProvider.providerName,
  );
  const selectedProviderTypeLabel = selectedProvider
    ? resolveProviderTypeLabel(selectedProvider.providerType)
    : '暂无';
  const selectedProviderConnectionValue = selectedProvider?.endpoint
    ? '已配置'
    : '待配置';
  const selectedProviderConnectionTone = selectedProvider?.endpoint
    ? 'success'
    : 'warning';
  const selectedProviderCredentialValue = selectedProvider
    ? selectedProvider.clearApiKeyRequested
      ? '待清除'
      : selectedProvider.apiKeyConfigured
        ? '已保存'
        : selectedProvider.apiKey
          ? '待保存'
          : '未保存'
    : '暂无';
  const selectedProviderCredentialTone = selectedProvider
    ? selectedProvider.clearApiKeyRequested
      ? 'warning'
      : selectedProvider.apiKeyConfigured
        ? 'success'
        : 'default'
    : 'default';
  const runtimeModeLabel = canEditRuntime ? '可编辑' : '宿主管理';
  const hostModeLabel = hostMode === 'proxy' ? '远程代理' : '内嵌宿主';
  const runtimeHealthValue = runtimeTestResult
    ? runtimeTestResult.reachable
      ? '可连接'
      : '需关注'
    : '未检测';
  const runtimeHealthTone = runtimeTestResult
    ? runtimeTestResult.reachable
      ? 'success'
      : 'warning'
    : 'default';
  const workflowSourcesLabel = canManageDirectories
    ? '工作区目录'
    : '当前团队绑定';
  const workflowDirectories = workspaceSettings.data?.directories ?? [];
  const openAdvancedSection = React.useCallback(() => {
    setActiveSection('advanced');
  }, []);
  const settingsTabs = React.useMemo(
    () => [
      {
        key: 'runtime' as const,
        label: (
          <span style={studioSettingsTabLabelStyle}>
            <ApiOutlined />
            <span>运行时</span>
          </span>
        ),
        children: (
          <div
            className={studioSettingsTabContentClassName}
            style={studioSettingsTabContentStyle}
          >
            <div style={sectionPanelStyle}>
              <div style={cardStackStyle}>
                <Typography.Text strong>运行时</Typography.Text>
              </div>
              {workspaceSettings.isError ? (
                <StudioNoticeCard
                  type="error"
                  title="读取工作区设置失败"
                  description={describeError(workspaceSettings.error)}
                />
              ) : settings.isError ? (
                <StudioNoticeCard
                  type="error"
                  title="读取工作台配置失败"
                  description={describeError(settings.error)}
                />
              ) : settingsDraft ? (
                <>
                  {!canEditRuntime ? (
                    <StudioNoticeCard
                      type="info"
                      title="当前运行时由宿主管理"
                      description="这里只做查看和连通性检测。"
                    />
                  ) : null}
                  <div style={summaryMetricGridStyle}>
                    <StudioSummaryMetric
                      label="连接"
                      tone={runtimeHealthTone}
                      value={runtimeHealthValue}
                    />
                    <StudioSummaryMetric label="模式" value={runtimeModeLabel} />
                  </div>
                  <div style={summaryFieldGridStyle}>
                    <StudioSummaryField
                      label="当前地址"
                      value={runtimeBaseUrl || '暂无'}
                      copyable={Boolean(runtimeBaseUrl)}
                    />
                    <StudioSummaryField
                      label="最近检测"
                      value={
                        runtimeTestResult?.checkedUrl || '尚未检测'
                      }
                    />
                  </div>
                  <div style={cardStackStyle}>
                    <Typography.Text strong>{runtimeFieldLabel}</Typography.Text>
                    <Input
                      aria-label="Studio runtime base URL"
                      value={runtimeBaseUrl}
                      disabled={!canEditRuntime}
                      onChange={(event) =>
                        onSetSettingsDraft((current) =>
                          current
                            ? { ...current, runtimeBaseUrl: event.target.value }
                            : current,
                        )
                      }
                    />
                  </div>
                  <Space wrap size={[8, 8]}>
                    <Button loading={runtimeTestPending} onClick={onTestRuntime}>
                      {runtimeActionLabel}
                    </Button>
                  </Space>
                  {runtimeTestResult ? (
                    <StudioNoticeCard
                      type={runtimeTestResult.reachable ? 'success' : 'warning'}
                      title={
                        runtimeTestResult.reachable
                          ? '运行时可连接'
                          : '运行时检测失败'
                      }
                      description={`${runtimeTestResult.message} · ${runtimeTestResult.checkedUrl}`}
                    />
                  ) : null}
                </>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="当前没有可用的运行时设置。"
                />
              )}
            </div>
          </div>
        ),
      },
      {
        key: 'providers' as const,
        label: (
          <span style={studioSettingsTabLabelStyle}>
            <RobotOutlined />
            <span>AI 提供方</span>
          </span>
        ),
        children: (
          <div
            className={studioSettingsTabContentClassName}
            style={studioSettingsTabContentStyle}
          >
            {settings.isError ? (
              <StudioNoticeCard
                type="error"
                title="读取工作台配置失败"
                description={describeError(settings.error)}
              />
            ) : settingsDraft ? (
              <>
                <div style={sectionPanelStyle}>
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 12,
                      justifyContent: 'space-between',
                    }}
                  >
                    <div style={cardStackStyle}>
                      <Typography.Text strong>AI 提供方</Typography.Text>
                      <Typography.Paragraph
                        style={{ margin: 0 }}
                        type="secondary"
                      >
                        管理默认提供方和基础信息。
                      </Typography.Paragraph>
                    </div>
                    <Button type="primary" onClick={onAddProvider}>
                      新增提供方
                    </Button>
                  </div>
                  <StudioNoticeCard
                    type="info"
                    title="连接设置在高级配置中"
                    description="端点和密钥统一在高级配置里维护。"
                    action={<Button onClick={openAdvancedSection}>打开高级配置</Button>}
                  />
                </div>
                <div style={providersGridStyle}>
                  <div style={providerColumnStyle}>
                    <div style={sectionPanelStyle}>
                      <div style={cardStackStyle}>
                        <Typography.Text strong>提供方列表</Typography.Text>
                        <Typography.Paragraph
                          style={{ margin: 0 }}
                          type="secondary"
                        >
                          选择一个提供方继续编辑。
                        </Typography.Paragraph>
                      </div>
                      {providers.length > 0 ? (
                        <div style={cardListStyle}>
                          {providers.map((provider) => {
                            const isSelected =
                              selectedProvider?.providerName ===
                              provider.providerName;

                            return (
                              <div
                                key={provider.providerName}
                                style={{
                                  ...cardListItemStyle,
                                  background: isSelected
                                    ? 'rgba(240, 245, 255, 0.96)'
                                    : cardListItemStyle.background,
                                  borderColor: isSelected
                                    ? 'rgba(22, 119, 255, 0.28)'
                                    : 'var(--ant-color-border-secondary)',
                                }}
                              >
                                <div style={cardListHeaderStyle}>
                                  <div style={cardListMainStyle}>
                                    <Typography.Text strong>
                                      {provider.providerName}
                                    </Typography.Text>
                                    <Typography.Text type="secondary">
                                      {provider.model ||
                                        provider.displayName ||
                                        resolveProviderTypeLabel(
                                          provider.providerType,
                                        )}
                                    </Typography.Text>
                                    <Space wrap size={[6, 6]}>
                                      <Tag>
                                        {resolveProviderTypeLabel(
                                          provider.providerType,
                                        )}
                                      </Tag>
                                      {settingsDraft.defaultProviderName ===
                                      provider.providerName ? (
                                        <Tag color="success">默认</Tag>
                                      ) : null}
                                      <Tag
                                        color={
                                          provider.endpoint
                                            ? 'processing'
                                            : 'default'
                                        }
                                      >
                                        {provider.endpoint
                                          ? '已配置'
                                          : '待配置'}
                                      </Tag>
                                    </Space>
                                  </div>
                                  <div style={cardListActionStyle}>
                                    <Button
                                      type={isSelected ? 'primary' : 'default'}
                                      size="small"
                                      onClick={() =>
                                        onSelectProviderName(
                                          provider.providerName,
                                        )
                                      }
                                    >
                                      {isSelected ? '当前选中' : '编辑'}
                                    </Button>
                                  </div>
                                </div>
                              </div>
                            );
                          })}
                        </div>
                      ) : (
                        <Empty
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                          description="当前没有可用的提供方。"
                        />
                      )}
                    </div>
                  </div>
                  <div style={providerColumnStyle}>
                    {selectedProvider ? (
                      <div style={cardStackStyle}>
                        <div style={sectionPanelStyle}>
                          <div
                            style={{
                              alignItems: 'center',
                              display: 'flex',
                              gap: 12,
                              justifyContent: 'space-between',
                            }}
                          >
                            <div style={cardStackStyle}>
                              <Typography.Text strong>
                                提供方详情
                              </Typography.Text>
                              <Typography.Paragraph
                                style={{ margin: 0 }}
                                type="secondary"
                              >
                                正在编辑 {selectedProvider.providerName}。
                              </Typography.Paragraph>
                            </div>
                            <div style={cardListActionStyle}>
                              <Button danger onClick={onDeleteSelectedProvider}>
                                删除提供方
                              </Button>
                              <Button
                                disabled={selectedProviderIsDefault}
                                onClick={onSetDefaultProvider}
                              >
                                {selectedProviderIsDefault
                                  ? '默认提供方'
                                  : '设为默认'}
                              </Button>
                            </div>
                          </div>
                          <div style={summaryMetricGridStyle}>
                            <StudioSummaryMetric
                              label="提供方类型"
                              tone="info"
                              value={selectedProviderTypeLabel}
                            />
                            <StudioSummaryMetric
                              label="默认"
                              tone={
                                selectedProviderIsDefault
                                  ? 'success'
                                  : 'default'
                              }
                              value={selectedProviderIsDefault ? '是' : '否'}
                            />
                            <StudioSummaryMetric
                              label="连接"
                              value={selectedProviderConnectionValue}
                              tone={selectedProviderConnectionTone}
                            />
                            <StudioSummaryMetric
                              label="凭证"
                              value={selectedProviderCredentialValue}
                              tone={selectedProviderCredentialTone}
                            />
                          </div>
                          <div style={summaryFieldGridStyle}>
                            <StudioSummaryField
                              label="显示名称"
                              value={selectedProvider.displayName || '暂无'}
                            />
                            <StudioSummaryField
                              label="模型"
                              value={selectedProvider.model || '暂无'}
                            />
                          </div>
                          {selectedProvider.description ? (
                            <div>
                              <Typography.Text style={summaryFieldLabelStyle}>
                                说明
                              </Typography.Text>
                              <Typography.Paragraph
                                style={{ margin: '8px 0 0' }}
                                type="secondary"
                              >
                                {selectedProvider.description}
                              </Typography.Paragraph>
                            </div>
                          ) : null}
                          <StudioNoticeCard
                            type="info"
                            title="连接配置"
                            description="端点、密钥和工作流来源都在高级配置里维护。"
                            action={
                              <Button onClick={openAdvancedSection}>
                                连接设置
                              </Button>
                            }
                          />
                        </div>

                        <div style={sectionPanelStyle}>
                          <Typography.Text strong>
                            基础信息
                          </Typography.Text>
                          <div style={providerFormGridStyle}>
                            <div style={cardStackStyle}>
                              <Typography.Text strong>提供方名称</Typography.Text>
                              <Input
                                aria-label="Studio provider name"
                                value={selectedProvider.providerName}
                                onChange={(event) => {
                                  const nextName = event.target.value;
                                  onSetSettingsDraft((current) =>
                                    current
                                      ? {
                                          ...current,
                                          providers: current.providers.map(
                                            (provider) =>
                                              provider.providerName ===
                                              selectedProvider.providerName
                                                ? {
                                                    ...provider,
                                                    providerName: nextName,
                                                  }
                                                : provider,
                                          ),
                                          defaultProviderName:
                                            current.defaultProviderName ===
                                            selectedProvider.providerName
                                              ? nextName
                                              : current.defaultProviderName,
                                        }
                                      : current,
                                  );
                                  onSelectProviderName(nextName);
                                }}
                              />
                            </div>
                            <div style={cardStackStyle}>
                              <Typography.Text strong>提供方类型</Typography.Text>
                              <Select
                                aria-label="Studio provider type"
                                value={selectedProvider.providerType}
                                options={providerTypes.map((type) => ({
                                  label: type.displayName,
                                  value: type.id,
                                }))}
                                onChange={(value) => {
                                  const profile =
                                    providerTypes.find(
                                      (type) => type.id === value,
                                    ) || null;
                                  onSetSettingsDraft((current) =>
                                    current
                                      ? {
                                          ...current,
                                          providers: current.providers.map(
                                            (provider) =>
                                              provider.providerName ===
                                              selectedProvider.providerName
                                                ? {
                                                    ...provider,
                                                    providerType: value,
                                                    displayName:
                                                      profile?.displayName ||
                                                      provider.displayName,
                                                    category:
                                                      profile?.category ||
                                                      provider.category,
                                                    description:
                                                      profile?.description ||
                                                      provider.description,
                                                    endpoint:
                                                      provider.endpoint ||
                                                      profile?.defaultEndpoint ||
                                                      '',
                                                    model:
                                                      provider.model ||
                                                      profile?.defaultModel ||
                                                      '',
                                                  }
                                                : provider,
                                          ),
                                        }
                                      : current,
                                  );
                                }}
                              />
                            </div>
                            <div style={cardStackStyle}>
                              <Typography.Text strong>模型</Typography.Text>
                              <Input
                                aria-label="Studio provider model"
                                value={selectedProvider.model}
                                onChange={(event) =>
                                  onSetSettingsDraft((current) =>
                                    current
                                      ? {
                                          ...current,
                                          providers: current.providers.map(
                                            (provider) =>
                                              provider.providerName ===
                                              selectedProvider.providerName
                                                ? {
                                                    ...provider,
                                                    model: event.target.value,
                                                  }
                                                : provider,
                                          ),
                                        }
                                      : current,
                                  )
                                }
                              />
                            </div>
                          </div>
                        </div>
                      </div>
                    ) : (
                      <div style={sectionPanelStyle}>
                        <Typography.Text strong>提供方详情</Typography.Text>
                        <Empty
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                          description="先选择一个提供方，再查看和编辑详情。"
                        />
                      </div>
                    )}
                  </div>
                </div>
              </>
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="当前还没有可用的提供方设置。"
              />
            )}
          </div>
        ),
      },
      {
        key: 'sources' as const,
        label: (
          <span style={studioSettingsTabLabelStyle}>
            <FolderAddOutlined />
            <span>工作流来源</span>
          </span>
        ),
        children: (
          <div
            className={studioSettingsTabContentClassName}
            style={studioSettingsTabContentStyle}
          >
            <div style={sectionPanelStyle}>
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  gap: 12,
                  justifyContent: 'space-between',
                }}
              >
                <div style={cardStackStyle}>
                  <Typography.Text strong>工作流来源</Typography.Text>
                </div>
                <Button onClick={openAdvancedSection}>打开高级配置</Button>
              </div>
              {workspaceSettings.isError ? (
                <StudioNoticeCard
                  type="error"
                  title="读取工作流来源失败"
                  description={describeError(workspaceSettings.error)}
                />
              ) : workspaceSettings.data ? (
                <>
                  {!canManageDirectories ? (
                    <StudioNoticeCard
                      type="info"
                      title="工作流来源绑定到当前团队"
                      description="这里仅展示当前可见目录。"
                    />
                  ) : (
                    <StudioNoticeCard
                      type="info"
                      title="目录管理在高级配置中"
                      description="这里只展示当前可见目录。"
                    />
                  )}
                  <div style={summaryMetricGridStyle}>
                    <StudioSummaryMetric
                      label="来源数"
                      value={workflowDirectories.length}
                    />
                    <StudioSummaryMetric
                      label="模式"
                      tone="info"
                      value={workflowSourcesLabel}
                    />
                  </div>
                  {workflowDirectories.length > 0 ? (
                    <div style={cardListStyle}>
                      {workflowDirectories.map((directory) => {
                        const showDirectoryPath =
                          workflowStorageMode !== 'scope' &&
                          !isScopeDirectoryPath(directory.path);

                        return (
                          <div key={directory.directoryId} style={cardListItemStyle}>
                            <div style={cardListHeaderStyle}>
                              <div style={cardListMainStyle}>
                                <Typography.Text strong>
                                  {directory.label}
                                </Typography.Text>
                                <Typography.Text type="secondary">
                                  {directory.isBuiltIn
                                    ? '内置目录'
                                    : '工作区目录'}
                                </Typography.Text>
                                <Space wrap size={[6, 6]}>
                                  {directory.isBuiltIn ? <Tag>内置</Tag> : null}
                                </Space>
                              </div>
                            </div>
                            {showDirectoryPath ? (
                              <Typography.Paragraph
                                copyable
                                style={{ margin: 0 }}
                                type="secondary"
                              >
                                {directory.path}
                              </Typography.Paragraph>
                            ) : null}
                          </div>
                        );
                      })}
                    </div>
                  ) : (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="当前没有配置工作流来源。"
                    />
                  )}
                </>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="当前无法读取工作流来源。"
                />
              )}
            </div>
          </div>
        ),
      },
      {
        key: 'advanced' as const,
        label: (
          <span style={studioSettingsTabLabelStyle}>
            <SafetyCertificateOutlined />
            <span>高级配置</span>
          </span>
        ),
        children: (
          <div
            className={studioSettingsTabContentClassName}
            style={studioSettingsTabContentStyle}
          >
            <div style={cardStackStyle}>
              <div style={sectionPanelStyle}>
                <div style={cardStackStyle}>
                  <Typography.Text strong>高级配置</Typography.Text>
                </div>
                <div style={summaryFieldGridStyle}>
                  <StudioSummaryField label="宿主模式" value={hostModeLabel} />
                  <StudioSummaryField
                    label="工作流模式"
                    value={workflowSourcesLabel}
                  />
                </div>
              </div>
              <div style={sectionPanelStyle}>
                <Typography.Text strong>工作流来源管理</Typography.Text>
                {workspaceSettings.isError ? (
                  <StudioNoticeCard
                    type="error"
                    title="读取工作流来源失败"
                    description={describeError(workspaceSettings.error)}
                  />
                ) : workspaceSettings.data ? (
                  <>
                    {!canManageDirectories ? (
                      <StudioNoticeCard
                        type="info"
                        title="工作流来源绑定到当前团队"
                        description="当前不允许在这里直接维护目录。"
                      />
                    ) : null}
                    <div style={summaryMetricGridStyle}>
                      <StudioSummaryMetric
                        label="来源数"
                        value={workspaceSettings.data.directories.length}
                      />
                      <StudioSummaryMetric
                        label="模式"
                        tone="info"
                        value={workflowSourcesLabel}
                      />
                    </div>
                    {workspaceSettings.data.directories.length > 0 ? (
                      <div style={cardListStyle}>
                        {workspaceSettings.data.directories.map((directory) => {
                          const showDirectoryPath =
                            workflowStorageMode !== 'scope' &&
                            !isScopeDirectoryPath(directory.path);

                          return (
                            <div
                              key={directory.directoryId}
                              style={cardListItemStyle}
                            >
                              <div style={cardListHeaderStyle}>
                                <div style={cardListMainStyle}>
                                  <Typography.Text strong>
                                    {directory.label}
                                  </Typography.Text>
                                  <Space wrap size={[6, 6]}>
                                    {directory.isBuiltIn ? (
                                      <Tag>内置</Tag>
                                    ) : null}
                                  </Space>
                                </div>
                                {canManageDirectories && !directory.isBuiltIn ? (
                                  <div style={cardListActionStyle}>
                                    <Button
                                      danger
                                      size="small"
                                      loading={settingsPending}
                                      onClick={() =>
                                        onRemoveDirectory(directory.directoryId)
                                      }
                                    >
                                      移除
                                    </Button>
                                  </div>
                                ) : null}
                              </div>
                              {showDirectoryPath ? (
                                <Typography.Paragraph
                                  copyable
                                  style={{ margin: 0 }}
                                  type="secondary"
                                >
                                  {directory.path}
                                </Typography.Paragraph>
                              ) : null}
                            </div>
                          );
                        })}
                      </div>
                    ) : (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="当前没有配置工作流来源。"
                      />
                    )}
                    {canManageDirectories ? (
                      <>
                        <Divider style={{ margin: 0 }}>
                          新增工作流目录
                        </Divider>
                        <div style={sectionPanelStyle}>
                          <Input
                            aria-label="Studio directory path"
                            placeholder="/path/to/workflows"
                            value={directoryPath}
                            onChange={(event) =>
                              onSetDirectoryPath(event.target.value)
                            }
                          />
                          <Input
                            aria-label="Studio directory label"
                            placeholder="可选标签"
                            value={directoryLabel}
                            onChange={(event) =>
                              onSetDirectoryLabel(event.target.value)
                            }
                          />
                          <Button
                            type="primary"
                            loading={settingsPending}
                            onClick={onAddDirectory}
                          >
                            新增目录
                          </Button>
                        </div>
                      </>
                    ) : null}
                  </>
                ) : (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="当前无法管理工作流来源。"
                  />
                )}
              </div>
              <div style={sectionPanelStyle}>
                <Typography.Text strong>提供方连接</Typography.Text>
                {settings.isError ? (
                  <StudioNoticeCard
                    type="error"
                    title="读取提供方配置失败"
                    description={describeError(settings.error)}
                  />
                ) : selectedProvider ? (
                  <>
                    <StudioNoticeCard
                      type="info"
                      title="连接信息仅在高级配置里维护"
                      description={`${selectedProvider.providerName} 的端点和密钥统一在这里维护。`}
                    />
                    <div style={summaryFieldGridStyle}>
                      <StudioSummaryField
                        label="提供方"
                        value={selectedProvider.providerName}
                      />
                      <StudioSummaryField
                        label="提供方类型"
                        value={selectedProviderTypeLabel}
                      />
                    </div>
                    <div style={providerFormGridStyle}>
                      <div style={cardStackStyle}>
                        <Typography.Text strong>地址</Typography.Text>
                        <Input
                          aria-label="Studio provider endpoint"
                          value={selectedProvider.endpoint}
                          onChange={(event) =>
                            onSetSettingsDraft((current) =>
                              current
                                ? {
                                    ...current,
                                    providers: current.providers.map((provider) =>
                                      provider.providerName ===
                                      selectedProvider.providerName
                                        ? {
                                            ...provider,
                                            endpoint: event.target.value,
                                          }
                                        : provider,
                                    ),
                                  }
                                : current,
                            )
                          }
                        />
                      </div>
                    </div>
                  </>
                ) : (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="先在 AI 提供方里选择一个提供方。"
                  />
                )}
              </div>
              <div style={sectionPanelStyle}>
                <Typography.Text strong>提供方密钥</Typography.Text>
                {settings.isError ? (
                  <StudioNoticeCard
                    type="error"
                    title="读取提供方密钥失败"
                    description={describeError(settings.error)}
                  />
                ) : selectedProvider ? (
                  <>
                    <Space wrap size={[8, 8]}>
                      {selectedProvider.apiKeyConfigured ? (
                        <Tag color="success">已保存密钥</Tag>
                      ) : null}
                      {selectedProvider.clearApiKeyRequested ? (
                        <Tag color="warning">保存后将清除</Tag>
                      ) : null}
                      {selectedProvider.apiKeyConfigured ||
                      selectedProvider.clearApiKeyRequested ? (
                        <Button
                          danger={!selectedProvider.clearApiKeyRequested}
                          onClick={() =>
                            onSetSettingsDraft((current) =>
                              current
                                ? {
                                    ...current,
                                    providers: current.providers.map((provider) =>
                                      provider.providerName ===
                                      selectedProvider.providerName
                                        ? {
                                            ...provider,
                                            apiKey: '',
                                            clearApiKeyRequested:
                                              !provider.clearApiKeyRequested,
                                          }
                                        : provider,
                                    ),
                                  }
                                : current,
                            )
                          }
                        >
                          {selectedProvider.clearApiKeyRequested
                            ? '保留已保存密钥'
                            : '清除已保存密钥'}
                        </Button>
                      ) : null}
                    </Space>
                    <Input.Password
                      aria-label="Studio provider API key"
                      value={selectedProvider.apiKey}
                      placeholder={
                        selectedProvider.clearApiKeyRequested
                          ? '保存时会移除当前密钥'
                          : selectedProvider.apiKeyConfigured
                            ? '当前已配置，输入新密钥后会替换'
                            : '可选'
                      }
                      onChange={(event) =>
                        onSetSettingsDraft((current) =>
                          current
                            ? {
                                ...current,
                                providers: current.providers.map((provider) =>
                                  provider.providerName ===
                                  selectedProvider.providerName
                                    ? {
                                        ...provider,
                                        apiKey: event.target.value,
                                        clearApiKeyRequested: false,
                                      }
                                    : provider,
                                ),
                              }
                            : current,
                        )
                      }
                    />
                    <Typography.Text type="secondary">
                      留空表示保留当前已保存密钥。
                    </Typography.Text>
                  </>
                ) : (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="先在 AI 提供方里选择一个提供方。"
                  />
                )}
              </div>
            </div>
          </div>
        ),
      },
    ],
    [
      canEditRuntime,
      canManageDirectories,
      directoryLabel,
      directoryPath,
      hostModeLabel,
      onAddDirectory,
      onAddProvider,
      onDeleteSelectedProvider,
      onRemoveDirectory,
      onSelectProviderName,
      onSetDefaultProvider,
      onSetDirectoryLabel,
      onSetDirectoryPath,
      onSetSettingsDraft,
      onTestRuntime,
      openAdvancedSection,
      providerFormGridStyle,
      providerTypes,
      providers,
      providersGridStyle,
      resolveProviderTypeLabel,
      runtimeActionLabel,
      runtimeBaseUrl,
      runtimeFieldLabel,
      runtimeHealthTone,
      runtimeHealthValue,
      runtimeModeLabel,
      runtimeTestPending,
      runtimeTestResult,
      sectionPanelStyle,
      selectedProvider,
      selectedProviderConnectionTone,
      selectedProviderConnectionValue,
      selectedProviderCredentialTone,
      selectedProviderCredentialValue,
      selectedProviderIsDefault,
      selectedProviderTypeLabel,
      settings,
      settingsDraft,
      settingsPending,
      workflowDirectories,
      workflowSourcesLabel,
      workflowStorageMode,
      workspaceSettings,
    ],
  );

  return (
    <div style={studioSurfaceStyle}>
      <div style={studioSurfaceHeaderStyle}>
        <div style={cardStackStyle}>
          <Typography.Text strong>工作区设置</Typography.Text>
        </div>
        <Button
          type="primary"
          loading={settingsPending}
          disabled={!settingsDirty || !settingsDraft}
          onClick={onSaveSettings}
        >
          保存工作区设置
        </Button>
      </div>
      <div style={studioSurfaceBodyStyle}>
        {settingsDraft ? (
          <>
            {settingsNotice ? (
              <div style={studioNoticeStripStyle}>
                <StudioNoticeCard
                  type={settingsNotice.type}
                  title={
                    settingsNotice.type === 'error'
                      ? '配置操作失败'
                      : '配置已更新'
                  }
                  description={settingsNotice.message}
                />
              </div>
            ) : null}
            <div style={sectionPanelStyle}>
              <div style={summaryMetricGridStyle}>
                <StudioSummaryMetric label="提供方" value={providers.length} />
                <StudioSummaryMetric
                  label="默认"
                  tone="info"
                  value={settingsDraft.defaultProviderName || '暂无'}
                />
                <StudioSummaryMetric
                  label="运行时"
                  tone={runtimeHealthTone}
                  value={runtimeHealthValue}
                />
                <StudioSummaryMetric
                  label="工作流来源"
                  value={workflowDirectories.length}
                />
              </div>
              <div style={summaryFieldGridStyle}>
                <StudioSummaryField
                  label="当前提供方"
                  value={selectedProvider?.providerName || '未选择'}
                />
                <StudioSummaryField label="宿主模式" value={hostModeLabel} />
                <StudioSummaryField
                  label="工作流模式"
                  value={workflowSourcesLabel}
                />
              </div>
            </div>
          </>
        ) : null}
        <style>{studioSettingsTabsCss}</style>
        <Tabs
          activeKey={activeSection}
          animated={false}
          className={studioSettingsTabsClassName}
          destroyOnHidden
          items={settingsTabs}
          onChange={(key) => setActiveSection(key as StudioSettingsSectionKey)}
          style={studioSettingsTabsStyle}
          tabBarStyle={{ marginBottom: 16, width: 220 }}
          tabPlacement="start"
        />
      </div>
    </div>
  );
};
