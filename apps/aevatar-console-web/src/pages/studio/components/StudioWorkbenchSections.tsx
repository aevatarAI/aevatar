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
  FileTextOutlined,
  FolderAddOutlined,
  LeftOutlined,
  LoadingOutlined,
  PlusOutlined,
  QuestionCircleOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  SearchOutlined,
  ToolOutlined,
  UploadOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
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
  Typography,
} from 'antd';
import React from 'react';
import type { Edge, Node } from '@xyflow/react';
import ReactDOM from 'react-dom';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import type { PlaygroundPromptHistoryEntry } from '@/shared/playground/promptHistory';
import {
  buildRuntimeGAgentAssemblyQualifiedName,
  buildRuntimeGAgentTypeLabel,
  collectRuntimeGAgentActorIds,
  matchesRuntimeGAgentTypeDescriptor,
  type RuntimeGAgentActorGroup,
  type RuntimeGAgentTypeDescriptor,
} from '@/shared/models/runtime/gagents';
import type {
  StudioConnectorCatalog,
  StudioConnectorDefinition,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioOrnnHealthResult,
  StudioOrnnSkillSearchResult,
  StudioProviderSettings,
  StudioProviderType,
  StudioRoleDefinition,
  StudioScopeBindingStatus,
  StudioRuntimeTestResult,
  StudioUserConfig,
  StudioUserConfigModelsResponse,
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
import AevatarAppFlowGuide, {
  type AevatarAppFlowGuideStepId,
} from '@/shared/ui/AevatarAppFlowGuide';
import StudioStatusBanner from './StudioStatusBanner';
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
  readonly isFetching?: boolean;
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
  readonly preferredActorId: string;
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

const DEFAULT_GAGENT_REQUEST_TYPE_URL =
  'type.googleapis.com/google.protobuf.StringValue';

function createStudioGAgentBindingEndpointDraft(
  overrides?: Partial<StudioGAgentBindingEndpointDraft>,
): StudioGAgentBindingEndpointDraft {
  return {
    endpointId: overrides?.endpointId ?? 'run',
    displayName: overrides?.displayName ?? 'Run',
    kind: overrides?.kind ?? 'command',
    requestTypeUrl:
      overrides?.requestTypeUrl ?? DEFAULT_GAGENT_REQUEST_TYPE_URL,
    responseTypeUrl: overrides?.responseTypeUrl ?? '',
    description: overrides?.description ?? 'Run the bound GAgent.',
  };
}

function createStudioGAgentBindingDraft(
  displayName: string,
): StudioGAgentBindingDraft {
  const defaultEndpoint = createStudioGAgentBindingEndpointDraft();
  return {
    displayName,
    actorTypeName: '',
    preferredActorId: '',
    endpoints: [defaultEndpoint],
    openRunsEndpointId: defaultEndpoint.endpointId,
    prompt: '',
    payloadBase64: '',
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
  flex: 1,
  minHeight: 0,
  height: '100%',
  overflow: 'hidden',
};

const workflowSidebarStackStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
};

const workflowStretchColumnStyle: React.CSSProperties = {
  ...stretchColumnStyle,
  flexDirection: 'column',
  minHeight: 0,
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
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: '0.12em',
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
  padding: 12,
};

const workflowToolbarStackStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
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
  alignItems: 'flex-start',
  gap: 8,
};

const workflowSummaryCardStyle: React.CSSProperties = {
  ...workflowSurfaceStyle,
  flex: '1 1 340px',
  minWidth: 0,
  padding: '10px 12px',
  borderRadius: 14,
};

const workflowSummaryCardRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  gap: 12,
  flexWrap: 'wrap',
  minWidth: 0,
};

const workflowSummaryCardBodyStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 2,
  minWidth: 0,
  flex: '1 1 220px',
};

const workflowSummaryEyebrowRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  flexWrap: 'wrap',
  minWidth: 0,
};

const workflowSummaryHintStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 11,
  lineHeight: 1.3,
  color: 'var(--ant-color-text-tertiary)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
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
  flex: 1,
  minHeight: 0,
  display: 'flex',
  flexDirection: 'column',
  overflowX: 'hidden',
  overflowY: 'auto',
  overscrollBehavior: 'contain',
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
  fontSize: 14,
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
  minWidth: 220,
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

const studioWorkflowHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255,255,255,0.96)',
  borderBottom: '1px solid #E8E2D9',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 12,
  justifyContent: 'space-between',
  padding: '14px 18px',
};

const studioWorkflowHeaderMainStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: '1 1 360px',
  gap: 12,
  minWidth: 0,
};

const studioWorkflowHeaderActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'flex-end',
  maxWidth: '100%',
  minWidth: 0,
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

type StudioWorkflowHeaderProps = {
  readonly activeView: 'editor' | 'execution';
  readonly workflowDisplayName: string;
  readonly draftWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly descriptionEditorOpen: boolean;
  readonly descriptionDraft: string;
  readonly actions: React.ReactNode;
  readonly onSwitchStudioView: (view: 'editor' | 'execution') => void;
  readonly onSetDraftWorkflowName: (value: string) => void;
  readonly onOpenDescriptionEditor: (open: boolean) => void;
  readonly onDescriptionDraftChange: (value: string) => void;
  readonly onCancelDescriptionEdit: () => void;
  readonly onSaveDescription: () => void;
};

const StudioWorkflowHeader: React.FC<StudioWorkflowHeaderProps> = ({
  activeView,
  workflowDisplayName,
  draftWorkflowName,
  activeWorkflowDescription,
  descriptionEditorOpen,
  descriptionDraft,
  actions,
  onSwitchStudioView,
  onSetDraftWorkflowName,
  onOpenDescriptionEditor,
  onDescriptionDraftChange,
  onCancelDescriptionEdit,
  onSaveDescription,
}) => (
  <div style={studioWorkflowHeaderStyle}>
    <div style={studioWorkflowHeaderMainStyle}>
      <Space.Compact>
        <Button
          type={activeView === 'editor' ? 'primary' : 'default'}
          onClick={() => onSwitchStudioView('editor')}
        >
          Edit
        </Button>
        <Button
          type={activeView === 'execution' ? 'primary' : 'default'}
          onClick={() => onSwitchStudioView('execution')}
        >
          Runs
        </Button>
      </Space.Compact>
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
            ariaLabel="Edit workflow description"
            onOpenChange={onOpenDescriptionEditor}
          >
            <textarea
              aria-label="Studio workflow description"
              placeholder="Workflow description"
              value={descriptionDraft}
              onChange={(event) => onDescriptionDraftChange(event.target.value)}
              style={studioDescriptionEditorStyle}
            />
            <div style={studioInfoPopoverActionsStyle}>
              <Button
                size="small"
                style={{ minWidth: 72, borderRadius: 10 }}
                onClick={onCancelDescriptionEdit}
              >
                Cancel
              </Button>
              <Button
                type="primary"
                size="small"
                style={{ minWidth: 72, borderRadius: 10 }}
                onClick={onSaveDescription}
              >
                Save
              </Button>
            </div>
          </StudioInfoPopover>
        </div>
      </div>
    </div>

    <div style={studioWorkflowHeaderActionsStyle}>{actions}</div>
  </div>
);

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

const studioGuidanceBarStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  backdropFilter: 'blur(10px)',
  borderRadius: 20,
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  minWidth: 0,
  padding: '12px 16px',
  pointerEvents: 'auto',
  width: 'min(420px, calc(100vw - 176px))',
  boxShadow: '0 22px 48px rgba(15, 23, 42, 0.14)',
};

const studioGuidanceFloatingWrapStyle: React.CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
  left: 0,
  pointerEvents: 'none',
  position: 'absolute',
  right: 0,
  top: 0,
  zIndex: 12,
};

const studioGuidanceFloatingCardStyle: React.CSSProperties = {
  pointerEvents: 'auto',
  transformOrigin: 'top right',
};

const studioGuidanceHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 12,
  justifyContent: 'space-between',
  minWidth: 0,
};

const studioGuidanceTagRowStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  flex: 1,
  flexWrap: 'wrap',
  gap: 8,
  minWidth: 0,
};

const studioGuidanceControlRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexShrink: 0,
  gap: 4,
};

const studioGuidanceDragHandleStyle: React.CSSProperties = {
  alignItems: 'center',
  color: 'var(--ant-color-text-tertiary)',
  cursor: 'grab',
  display: 'inline-flex',
  justifyContent: 'center',
};

const studioGuidanceMainStyle: React.CSSProperties = {
  ...cardStackStyle,
  gap: 4,
  minWidth: 0,
};

const studioGuidanceCollapsedStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  alignItems: 'center',
  backdropFilter: 'blur(10px)',
  borderRadius: 999,
  boxShadow: '0 16px 36px rgba(15, 23, 42, 0.12)',
  display: 'flex',
  gap: 6,
  minWidth: 0,
  padding: '8px 10px',
  pointerEvents: 'auto',
};

const studioGuidanceCollapsedTitleStyle: React.CSSProperties = {
  color: 'var(--ant-color-text)',
  display: 'block',
  fontSize: 12,
  fontWeight: 600,
  minWidth: 0,
};

const studioGuidanceDescriptionStyle: React.CSSProperties = {
  display: '-webkit-box',
  lineHeight: 1.5,
  margin: 0,
  overflow: 'hidden',
  WebkitBoxOrient: 'vertical',
  WebkitLineClamp: 2,
  wordBreak: 'break-word',
};

const studioGuidanceActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-start',
};

const studioGuidanceNoticeStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  minWidth: 0,
};

const studioSettingsShellStyle: React.CSSProperties = {
  background: 'rgba(255, 255, 255, 0.96)',
  border: '1px solid #E6E3DE',
  borderRadius: 38,
  boxShadow: '0 26px 64px rgba(17, 24, 39, 0.08)',
  display: 'flex',
  flex: 1,
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
};

const studioSettingsSidebarStyle: React.CSSProperties = {
  display: 'flex',
  gap: 8,
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflowY: 'auto',
  padding: '28px 20px',
  width: 260,
};

const studioSettingsSidebarHeaderStyle: React.CSSProperties = {
  ...cardStackStyle,
  marginBottom: 12,
};

const studioSettingsSidebarEyebrowStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-tertiary)',
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: '0.12em',
  margin: 0,
  textTransform: 'uppercase',
};

const studioSettingsSidebarTitleStyle: React.CSSProperties = {
  color: 'var(--ant-color-text)',
  fontSize: 24,
  fontWeight: 700,
  lineHeight: 1.2,
  margin: 0,
};

const studioSettingsSidebarCopyStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontSize: 13,
  lineHeight: 1.6,
  margin: 0,
};

const studioSettingsNavListStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
};

const studioSettingsContentPaneStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
  minWidth: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  padding: '40px 40px 40px',
};

const studioSettingsSectionBodyStyle: React.CSSProperties = {
  margin: '0 auto',
  maxWidth: 920,
  display: 'flex',
  flexDirection: 'column',
  gap: 32,
  width: '100%',
};

const studioSettingsSectionHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 16,
  justifyContent: 'space-between',
};

const studioSettingsSectionTitleStyle: React.CSSProperties = {
  color: 'var(--ant-color-text)',
  fontSize: 32,
  fontWeight: 700,
  lineHeight: 1.15,
  margin: 0,
};

const studioSettingsSectionDescriptionStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontSize: 12,
  lineHeight: 1.6,
  margin: '4px 0 0',
};

const studioSettingsSectionCardStyle: React.CSSProperties = {
  background:
    'linear-gradient(180deg, rgba(255, 255, 255, 0.96) 0%, rgba(250, 248, 244, 0.92) 100%)',
  border: '1px solid #EDE8DF',
  borderRadius: 28,
  boxShadow: '0 20px 44px rgba(17, 24, 39, 0.06)',
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  minWidth: 0,
  padding: 24,
};

const studioSettingsSectionGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
};

const studioSettingsStatusCardStyle: React.CSSProperties = {
  background: '#fff',
  border: '1px solid #E6E1D8',
  borderRadius: 22,
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  minWidth: 0,
  padding: '16px 18px',
};

const studioSettingsProviderPillsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
};

const studioSettingsProviderPillStyle: React.CSSProperties = {
  alignItems: 'center',
  borderRadius: 999,
  display: 'inline-flex',
  gap: 6,
  minWidth: 0,
  padding: '4px 12px',
};

const studioSettingsProviderPillReadyStyle: React.CSSProperties = {
  background: '#F0FDF4',
  color: '#15803D',
};

const studioSettingsProviderPillPendingStyle: React.CSSProperties = {
  background: '#F3F4F6',
  color: '#9CA3AF',
};

const studioSettingsMutedBlockStyle: React.CSSProperties = {
  background: '#FAF8F4',
  border: '1px solid #EEEAE4',
  borderRadius: 20,
  color: 'var(--ant-color-text-secondary)',
  fontSize: 13,
  lineHeight: 1.6,
  padding: '12px 16px',
};

const studioSettingsPrimaryActionStyle: React.CSSProperties = {
  alignItems: 'center',
  background:
    'linear-gradient(180deg, var(--accent, #2563eb) 0%, rgba(var(--accent-rgb, 37, 99, 235), 0.92) 100%)',
  border: '1px solid rgba(var(--accent-rgb, 37, 99, 235), 0.24)',
  borderRadius: 12,
  color: '#fff',
  cursor: 'pointer',
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 700,
  gap: 8,
  justifyContent: 'center',
  minHeight: 38,
  minWidth: 180,
  padding: '0 14px',
  textDecoration: 'none',
};

const studioSettingsPillDotStyle: React.CSSProperties = {
  borderRadius: '50%',
  flex: '0 0 auto',
  height: 8,
  width: 8,
};

function formatStudioUserConfigProviderLabel(provider: {
  readonly providerName: string;
  readonly providerSlug: string;
}): string {
  const explicitName = provider.providerName.trim();
  if (explicitName) {
    return explicitName;
  }

  const slug = provider.providerSlug.trim();
  if (!slug) {
    return 'Provider';
  }

  return slug
    .split(/[-_]+/)
    .filter(Boolean)
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(' ');
}

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
  flex: 1,
  minHeight: 'calc(100vh - 278px)',
  overflow: 'hidden',
  position: 'relative',
};

const studioToolDrawerSectionStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const studioCanvasDrawerRootStyle: React.CSSProperties = {
  position: 'absolute',
};

function getStudioNoticeAccent(
  type: StudioNoticeLike['type'] | 'default',
): { border: string; background: string; label: string } {
  switch (type) {
    case 'success':
      return {
        border: 'rgba(82, 196, 26, 0.28)',
        background: 'rgba(246, 255, 237, 0.96)',
        label: 'Success',
      };
    case 'warning':
      return {
        border: 'rgba(250, 173, 20, 0.28)',
        background: 'rgba(255, 251, 230, 0.96)',
        label: 'Warning',
      };
    case 'error':
      return {
        border: 'rgba(255, 77, 79, 0.28)',
        background: 'rgba(255, 241, 240, 0.96)',
        label: 'Error',
      };
    case 'info':
      return {
        border: 'rgba(22, 119, 255, 0.24)',
        background: 'rgba(240, 245, 255, 0.96)',
        label: 'Info',
      };
    default:
      return {
        border: 'var(--ant-color-border-secondary)',
        background: 'var(--ant-color-fill-quaternary)',
        label: 'Status',
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
  expandLabel = 'Details',
  collapseLabel = 'Hide details',
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

type StudioGuidanceAction = {
  readonly label: string;
  readonly onClick: () => void;
  readonly type?: 'primary' | 'default';
  readonly loading?: boolean;
  readonly disabled?: boolean;
};

type StudioGuidanceTag = {
  readonly label: string;
  readonly color?: string;
};

type StudioGuidanceBarProps = {
  readonly type: StudioNoticeLike['type'];
  readonly title: string;
  readonly description: string;
  readonly statusTags?: readonly StudioGuidanceTag[];
  readonly primaryAction?: StudioGuidanceAction;
  readonly secondaryAction?: StudioGuidanceAction;
  readonly notice?: StudioNoticeLike | null;
  readonly guideTitle: string;
  readonly guideDescription: string;
  readonly guideTone?: StudioNoticeLike['type'];
  readonly guideHighlightSteps?: readonly AevatarAppFlowGuideStepId[];
};

type StudioGuidancePresentation = 'expanded' | 'collapsed' | 'dismissed';

type StudioGuidanceOffset = {
  readonly x: number;
  readonly y: number;
};

const studioGuidanceUpdatePulseMs = 820;

function clampStudioGuidanceOffset(
  offset: StudioGuidanceOffset,
  cardRect: Pick<DOMRect, 'height' | 'width'>,
  wrapRect: Pick<DOMRect, 'top' | 'width'>,
): StudioGuidanceOffset {
  const edgePadding = 12;

  if (cardRect.width <= 0 || cardRect.height <= 0 || wrapRect.width <= 0) {
    return {
      x: Math.min(0, offset.x),
      y: Math.max(0, offset.y),
    };
  }

  const maxX = 0;
  const minX = Math.min(0, cardRect.width - wrapRect.width + edgePadding);
  const maxY = Math.max(
    0,
    window.innerHeight - wrapRect.top - cardRect.height - edgePadding,
  );

  return {
    x: Math.min(maxX, Math.max(minX, offset.x)),
    y: Math.min(maxY, Math.max(0, offset.y)),
  };
}

const StudioGuidanceBar: React.FC<StudioGuidanceBarProps> = ({
  type,
  title,
  description,
  statusTags = [],
  primaryAction,
  secondaryAction,
  notice,
  guideTitle,
  guideDescription,
  guideTone = type,
  guideHighlightSteps = [],
}) => {
  const accent = getStudioNoticeAccent(type);
  const [guideOpen, setGuideOpen] = React.useState(false);
  const [presentation, setPresentation] =
    React.useState<StudioGuidancePresentation>('collapsed');
  const [dragOffset, setDragOffset] = React.useState<StudioGuidanceOffset>({
    x: 0,
    y: 0,
  });
  const [dragging, setDragging] = React.useState(false);
  const [updatedPulse, setUpdatedPulse] = React.useState(false);
  const floatingWrapRef = React.useRef<HTMLDivElement | null>(null);
  const floatingCardRef = React.useRef<HTMLDivElement | null>(null);
  const dragSessionRef = React.useRef<{
    originX: number;
    originY: number;
    startX: number;
    startY: number;
  } | null>(null);
  const hasMountedRef = React.useRef(false);
  const guidanceIdentity = React.useMemo(
    () =>
      JSON.stringify({
        type,
        title,
        description,
        statusTags,
        primaryActionLabel: primaryAction?.label ?? '',
        secondaryActionLabel: secondaryAction?.label ?? '',
        noticeMessage: notice?.message ?? '',
        guideTitle,
        guideDescription,
        guideTone,
        guideHighlightSteps,
      }),
    [
      description,
      guideDescription,
      guideHighlightSteps,
      guideTitle,
      guideTone,
      notice?.message,
      primaryAction?.label,
      secondaryAction?.label,
      statusTags,
      title,
      type,
    ],
  );

  const clampDragOffset = React.useCallback(
    (nextOffset: StudioGuidanceOffset): StudioGuidanceOffset => {
      if (typeof window === 'undefined') {
        return nextOffset;
      }

      const cardNode = floatingCardRef.current;
      const wrapNode = floatingWrapRef.current;

      if (!cardNode || !wrapNode) {
        return nextOffset;
      }

      return clampStudioGuidanceOffset(
        nextOffset,
        cardNode.getBoundingClientRect(),
        wrapNode.getBoundingClientRect(),
      );
    },
    [],
  );

  const startDragging = React.useCallback(
    (event: React.MouseEvent<HTMLElement>) => {
      event.preventDefault();
      event.stopPropagation();
      dragSessionRef.current = {
        originX: dragOffset.x,
        originY: dragOffset.y,
        startX: event.clientX,
        startY: event.clientY,
      };
      setDragging(true);
    },
    [dragOffset.x, dragOffset.y],
  );

  React.useEffect(() => {
    setGuideOpen(false);
    setPresentation('collapsed');
    if (!hasMountedRef.current) {
      hasMountedRef.current = true;
      return;
    }
    setUpdatedPulse(true);
    const pulseTimer = window.setTimeout(
      () => setUpdatedPulse(false),
      studioGuidanceUpdatePulseMs,
    );
    return () => window.clearTimeout(pulseTimer);
  }, [guidanceIdentity]);

  React.useEffect(() => {
    if (typeof window === 'undefined') {
      return undefined;
    }

    const syncOffset = window.setTimeout(() => {
      setDragOffset((currentOffset) => clampDragOffset(currentOffset));
    }, 0);

    return () => window.clearTimeout(syncOffset);
  }, [clampDragOffset, guidanceIdentity, presentation]);

  React.useEffect(() => {
    if (typeof window === 'undefined') {
      return undefined;
    }

    const handleResize = () => {
      setDragOffset((currentOffset) => clampDragOffset(currentOffset));
    };

    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
  }, [clampDragOffset]);

  React.useEffect(() => {
    if (!dragging || typeof window === 'undefined') {
      return undefined;
    }

    const previousUserSelect = document.body.style.userSelect;
    document.body.style.userSelect = 'none';

    const stopDragging = () => {
      dragSessionRef.current = null;
      setDragging(false);
      document.body.style.userSelect = previousUserSelect;
    };

    const handlePointerMove = (event: MouseEvent) => {
      const session = dragSessionRef.current;
      if (!session) {
        return;
      }

      setDragOffset(
        clampDragOffset({
          x: session.originX + event.clientX - session.startX,
          y: session.originY + event.clientY - session.startY,
        }),
      );
    };

    window.addEventListener('mousemove', handlePointerMove);
    window.addEventListener('mouseup', stopDragging);

    return () => {
      window.removeEventListener('mousemove', handlePointerMove);
      window.removeEventListener('mouseup', stopDragging);
      document.body.style.userSelect = previousUserSelect;
    };
  }, [clampDragOffset, dragging]);

  const floatingCardStyle: React.CSSProperties = {
    ...studioGuidanceFloatingCardStyle,
    borderRadius: presentation === 'expanded' ? 20 : 999,
    boxShadow: updatedPulse
      ? `0 0 0 3px ${accent.border}, 0 24px 52px rgba(15, 23, 42, 0.16)`
      : dragging
        ? '0 20px 44px rgba(15, 23, 42, 0.18)'
        : undefined,
    transform: `translate3d(${dragOffset.x}px, ${
      dragOffset.y + (updatedPulse && !dragging ? -4 : 0)
    }px, 0) scale(${updatedPulse && !dragging ? 1.02 : 1})`,
    transition: dragging
      ? 'none'
      : 'transform 220ms cubic-bezier(0.22, 1, 0.36, 1), box-shadow 220ms ease',
  };

  return (
    <>
      <div ref={floatingWrapRef} style={studioGuidanceFloatingWrapStyle}>
        {presentation === 'expanded' ? (
          <div
            ref={floatingCardRef}
            data-guidance-presentation={presentation}
            data-guidance-updated={updatedPulse ? 'true' : 'false'}
            data-testid="studio-guidance-floating-card"
            style={{
              ...floatingCardStyle,
              ...studioGuidanceBarStyle,
              background: accent.background,
              borderColor: accent.border,
            }}
          >
            <div style={studioGuidanceHeaderStyle}>
              <div style={studioGuidanceTagRowStyle}>
                <Tag color={type}>Guidance</Tag>
                {statusTags.map((tag) => (
                  <Tag
                    key={`${tag.color || 'default'}:${tag.label}`}
                    color={tag.color}
                  >
                    {tag.label}
                  </Tag>
                ))}
              </div>
              <div style={studioGuidanceControlRowStyle}>
                <Button
                  type="text"
                  size="small"
                  shape="circle"
                  icon={<BarsOutlined />}
                  aria-label="Drag guidance"
                  onMouseDown={startDragging}
                  style={{
                    ...studioGuidanceDragHandleStyle,
                    cursor: dragging ? 'grabbing' : studioGuidanceDragHandleStyle.cursor,
                  }}
                />
                <Button
                  type="text"
                  size="small"
                  shape="circle"
                  icon={<DownOutlined />}
                  aria-label="Collapse guidance"
                  onClick={() => setPresentation('collapsed')}
                />
                <Button
                  type="text"
                  size="small"
                  shape="circle"
                  icon={<CloseOutlined />}
                  aria-label="Close guidance"
                  onClick={() => setPresentation('dismissed')}
                />
              </div>
            </div>

            <div style={studioGuidanceMainStyle}>
              <Typography.Text strong>{title}</Typography.Text>
              <Typography.Paragraph
                style={studioGuidanceDescriptionStyle}
                title={description}
                type="secondary"
              >
                {description}
              </Typography.Paragraph>

              {notice ? (
                <div style={studioGuidanceNoticeStyle}>
                  <Tag color={notice.type}>
                    {notice.type === 'error' ? 'Needs attention' : 'Latest update'}
                  </Tag>
                  <Typography.Text
                    style={{
                      color:
                        notice.type === 'error'
                          ? 'var(--ant-color-error)'
                          : 'var(--ant-color-text-tertiary)',
                    }}
                  >
                    {notice.message}
                  </Typography.Text>
                </div>
              ) : null}
            </div>

            <div style={studioGuidanceActionsStyle}>
              {primaryAction ? (
                <Button
                  type={primaryAction.type ?? 'primary'}
                  onClick={primaryAction.onClick}
                  loading={primaryAction.loading}
                  disabled={primaryAction.disabled}
                >
                  {primaryAction.label}
                </Button>
              ) : null}
              {secondaryAction ? (
                <Button
                  type={secondaryAction.type ?? 'default'}
                  onClick={secondaryAction.onClick}
                  loading={secondaryAction.loading}
                  disabled={secondaryAction.disabled}
                >
                  {secondaryAction.label}
                </Button>
              ) : null}
              <Button
                icon={<QuestionCircleOutlined />}
                onClick={() => setGuideOpen(true)}
              >
                Why?
              </Button>
            </div>
          </div>
        ) : presentation === 'collapsed' ? (
          <div
            ref={floatingCardRef}
            data-guidance-presentation={presentation}
            data-guidance-updated={updatedPulse ? 'true' : 'false'}
            data-testid="studio-guidance-floating-card"
            style={{
              ...floatingCardStyle,
              ...studioGuidanceCollapsedStyle,
              background: accent.background,
              borderColor: accent.border,
            }}
          >
            <Button
              type="text"
              size="small"
              shape="circle"
              icon={<BarsOutlined />}
              aria-label="Drag guidance"
              onMouseDown={startDragging}
              style={{
                ...studioGuidanceDragHandleStyle,
                cursor: dragging ? 'grabbing' : studioGuidanceDragHandleStyle.cursor,
              }}
            />
            <Button
              type="text"
              icon={<ExpandOutlined />}
              aria-label="Expand guidance"
              onClick={() => setPresentation('expanded')}
              style={{ height: 'auto', padding: 0 }}
            >
              <Typography.Text
                style={studioGuidanceCollapsedTitleStyle}
                ellipsis={{ tooltip: title }}
              >
                {title}
              </Typography.Text>
            </Button>
            <Button
              type="text"
              size="small"
              shape="circle"
              icon={<CloseOutlined />}
              aria-label="Close guidance"
              onClick={() => setPresentation('dismissed')}
            />
          </div>
        ) : (
          <Button
            type="default"
            shape="circle"
            icon={<ExpandOutlined />}
            aria-label="Show guidance"
            onClick={() => setPresentation('expanded')}
            data-guidance-presentation={presentation}
            data-guidance-updated={updatedPulse ? 'true' : 'false'}
            data-testid="studio-guidance-floating-card"
            style={{
              ...floatingCardStyle,
              boxShadow: '0 14px 32px rgba(15, 23, 42, 0.12)',
              pointerEvents: 'auto',
            }}
          />
        )}
      </div>

      <Drawer
        title="Workflow guide"
        open={guideOpen}
        width={560}
        destroyOnClose
        onClose={() => setGuideOpen(false)}
        styles={{ body: drawerBodyStyle }}
      >
        <div style={drawerScrollStyle}>
          <AevatarAppFlowGuide
            compact
            contextTitle={guideTitle}
            contextDescription={guideDescription}
            tone={guideTone}
            highlightSteps={guideHighlightSteps}
          />
        </div>
      </Drawer>
    </>
  );
};

type StudioSettingsNavButtonProps = {
  readonly active: boolean;
  readonly description: string;
  readonly icon: React.ReactNode;
  readonly onClick: () => void;
  readonly title: string;
};

const StudioSettingsNavButton: React.FC<StudioSettingsNavButtonProps> = ({
  active,
  description,
  icon,
  onClick,
  title,
}) => (
  <button
    type="button"
    aria-label={title}
    aria-pressed={active}
    title={description}
    onClick={onClick}
    className={`settings-nav-button ${active ? 'active' : ''}`}
  >
    <span className="settings-nav-icon">
      {icon}
    </span>
    <span style={{ ...cardStackStyle, minWidth: 0 }}>
      <span
        style={{
          fontSize: 14,
          fontWeight: 600,
          lineHeight: 1.4,
        }}
      >
        {title}
      </span>
    </span>
  </button>
);

type StudioSettingsStatusPillProps = {
  readonly status: 'idle' | 'testing' | 'success' | 'error';
};

const studioSettingsStatusPillToneMap: Record<
  StudioSettingsStatusPillProps['status'],
  { background: string; color: string; label: string }
> = {
  idle: {
    background: '#F3F4F6',
    color: '#6B7280',
    label: 'Idle',
  },
  testing: {
    background: '#EFF6FF',
    color: '#2563EB',
    label: 'Testing',
  },
  success: {
    background: '#ECFDF3',
    color: '#15803D',
    label: 'Reachable',
  },
  error: {
    background: '#FEF2F2',
    color: '#DC2626',
    label: 'Unreachable',
  },
};

const StudioSettingsStatusPill: React.FC<StudioSettingsStatusPillProps> = ({
  status,
}) => {
  const tone = studioSettingsStatusPillToneMap[status];

  return (
    <span
      className={`settings-status-pill ${status === 'idle' ? '' : status}`.trim()}
      style={status === 'idle' ? { background: tone.background, color: tone.color } : undefined}
    >
      {tone.label}
    </span>
  );
};

type StudioSummaryFieldProps = {
  readonly block?: boolean;
  readonly copyable?: boolean;
  readonly label: string;
  readonly monospace?: boolean;
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
  options?: { readonly block?: boolean; readonly monospace?: boolean },
): React.ReactNode {
  if (typeof value === 'string') {
    if (!value) {
      return <Typography.Text type="secondary">n/a</Typography.Text>;
    }

    const textNode = options?.block ? (
      <Typography.Paragraph
        copyable={copyable}
        style={{
          background: '#FAF8F4',
          border: '1px solid #EDE8DF',
          borderRadius: 12,
          display: 'block',
          fontFamily: options.monospace
            ? 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace'
            : undefined,
          fontSize: 12,
          lineHeight: 1.6,
          margin: 0,
          overflowWrap: 'anywhere',
          padding: '8px 10px',
          whiteSpace: 'normal',
          wordBreak: 'break-word',
        }}
      >
        {value}
      </Typography.Paragraph>
    ) : copyable ? (
      <Typography.Text copyable>{value}</Typography.Text>
    ) : (
      <Typography.Text>{value}</Typography.Text>
    );

    return textNode;
  }

  if (typeof value === 'number') {
    return <Typography.Text>{value}</Typography.Text>;
  }

  return value;
}

const StudioSummaryField: React.FC<StudioSummaryFieldProps> = ({
  block,
  copyable,
  label,
  monospace,
  value,
}) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {renderStudioSummaryValue(value, copyable, { block, monospace })}
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

function formatCompactScopeBindingIdentifier(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return 'n/a';
  }

  if (trimmed.length <= 28) {
    return trimmed;
  }

  return `${trimmed.slice(0, 12)}...${trimmed.slice(-8)}`;
}

type StudioScopeBindingPanelProps = {
  readonly scopeId?: string;
  readonly binding?: StudioScopeBindingStatus;
  readonly loading: boolean;
  readonly error: unknown;
  readonly pendingRevisionId: string;
  readonly pendingRetirementRevisionId: string;
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
    currentRevision?.staticPreferredActorId ||
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
    ? 'Loading'
    : error
      ? 'Unavailable'
      : binding?.available
        ? 'Active'
        : 'Not published';
  const detailsContent = loading ? (
    <StudioNoticeCard
      title="Loading scope binding"
      description="Fetching the current revision history and serving state for this scope."
      compact
    />
  ) : error ? (
    <StudioNoticeCard
      type="error"
      title="Failed to load scope binding"
      description={describeError(error)}
      compact
      defaultExpanded
    />
  ) : !binding?.available ? (
    <StudioNoticeCard
      type="info"
      title="No published binding"
      description="Use Bind scope to publish the current workflow as the scope's default service."
      compact
    />
  ) : (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 16,
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
            <Typography.Text strong>Current posture</Typography.Text>
            <Typography.Text type="secondary">
              The current published default binding, target identity, and serving posture live together here.
            </Typography.Text>
          </Space>

          <div
            style={{
              display: 'grid',
              gap: 12,
              gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
            }}
          >
            <StudioSummaryMetric
              label="Revisions"
              value={binding.revisions.length}
            />
            <StudioSummaryMetric
              label="Binding kind"
              value={formatStudioScopeBindingImplementationKind(
                currentRevision?.implementationKind,
              )}
            />
            <StudioSummaryMetric
              label="Current target"
              value={currentTarget}
            />
            <StudioSummaryMetric
              label="Default revision"
              tone="success"
              value={formatCompactScopeBindingIdentifier(
                binding.defaultServingRevisionId,
              )}
            />
            <StudioSummaryMetric
              label="Active revision"
              tone="info"
              value={formatCompactScopeBindingIdentifier(
                binding.activeServingRevisionId,
              )}
            />
            <StudioSummaryMetric
              label="Deployment"
              value={binding.deploymentStatus || 'n/a'}
            />
          </div>

          <div style={{ display: 'grid', gap: 14 }}>
            <StudioSummaryField
              block
              label="Service key"
              copyable
              monospace
              value={binding.serviceKey}
            />
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
              }}
            >
              <StudioSummaryField
                label="Target"
                value={currentTarget}
              />
              <StudioSummaryField
                label="Deployment"
                value={binding.deploymentId}
              />
              <StudioSummaryField
                label="Updated"
                value={formatDateTime(binding.updatedAt)}
              />
            </div>
            <StudioSummaryField
              block
              label="Target detail"
              copyable
              monospace
              value={currentContext || 'n/a'}
            />
            <StudioSummaryField
              block
              label="Primary actor"
              copyable
              monospace
              value={currentActor || 'n/a'}
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
            <Typography.Text strong>Revision rollout</Typography.Text>
            <Typography.Text type="secondary">
              Review published revisions, switch the default serving revision, or retire stale ones.
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
                    border: '1px solid #E6E3DE',
                    borderRadius: 20,
                    background: '#FFFFFF',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 16,
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
                      <Typography.Text
                        strong
                        copyable
                        style={{
                          fontFamily:
                            'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
                          overflowWrap: 'anywhere',
                          wordBreak: 'break-word',
                        }}
                      >
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
                        <Tag color="success">default</Tag>
                      ) : null}
                      {revision.isActiveServing ? (
                        <Tag color="processing">active</Tag>
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
                      <Typography.Text
                        type="secondary"
                        style={{
                          fontFamily:
                            'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
                          overflowWrap: 'anywhere',
                          wordBreak: 'break-word',
                        }}
                      >
                        {revisionContext}
                      </Typography.Text>
                    ) : null}
                    {revision.failureReason ? (
                      <Typography.Text type="danger">
                        {revision.failureReason}
                      </Typography.Text>
                    ) : null}
                  </div>

                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      flexWrap: 'wrap',
                      gap: 12,
                      justifyContent: 'space-between',
                    }}
                  >
                    <Typography.Text
                      type="secondary"
                      style={{
                        fontFamily:
                          'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
                        overflowWrap: 'anywhere',
                        wordBreak: 'break-word',
                      }}
                    >
                      Deployment {formatCompactScopeBindingIdentifier(revision.deploymentId || 'draft only')}
                    </Typography.Text>
                    <Space wrap size={[8, 8]}>
                      <Button
                        type={canActivate ? 'primary' : 'default'}
                        disabled={!canActivate}
                        loading={pendingRevisionId === revision.revisionId}
                        onClick={() => onActivateRevision(revision.revisionId)}
                      >
                        {revision.isDefaultServing ? 'Serving' : 'Activate'}
                      </Button>
                      <Button
                        danger
                        disabled={!canRetire}
                        loading={
                          pendingRetirementRevisionId === revision.revisionId
                        }
                        onClick={() => onRetireRevision(revision.revisionId)}
                      >
                        Retire
                      </Button>
                    </Space>
                  </div>
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
        ...workflowSectionShellStyle,
        gap: 12,
        padding: 16,
      }}
    >
      <div
        style={{
          ...workflowSectionHeaderStyle,
          alignItems: 'center',
        }}
      >
        <div style={workflowDirectoryTextStackStyle}>
          <Typography.Text style={workflowSectionHeadingStyle}>
            Scope Binding
          </Typography.Text>
          <Space wrap size={[8, 8]}>
            <Typography.Title
              level={5}
              style={{ margin: 0 }}
            >
              {binding?.available
                ? binding.displayName || binding.serviceId
                : 'No active binding'}
            </Typography.Title>
            <Tag color={bindingStateColor}>{bindingStateLabel}</Tag>
          </Space>
        </div>
        <Space wrap size={[8, 8]}>
          <Button
            type="text"
            size="small"
            aria-haspopup="dialog"
            aria-controls="studio-scope-binding-details"
            onClick={() => setDetailsOpen(true)}
            icon={
              <DownOutlined
                style={{
                  transform: 'rotate(0deg)',
                  transition: 'transform 0.2s ease',
                }}
              />
            }
          >
            Show details
          </Button>
        </Space>
      </div>
      <Drawer
        id="studio-scope-binding-details"
        open={detailsOpen}
        title="Scope Binding Details"
        placement="right"
        size={720}
        onClose={() => setDetailsOpen(false)}
        destroyOnClose
        styles={{ body: drawerBodyStyle }}
        extra={
          <Button size="small" onClick={() => setDetailsOpen(false)}>
            Close panel
          </Button>
        }
      >
        <div style={drawerScrollStyle}>{detailsContent}</div>
      </Drawer>
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
}) => {
  if (!authSession?.enabled || authSession.authenticated) {
    return null;
  }

  return (
    <div style={studioNoticeStripStyle}>
      <StudioStatusBanner
        type="warning"
        title="Studio sign-in required"
        description={
          authSession.errorMessage || 'Sign in to access protected Studio APIs.'
        }
        action={
          authSession.loginUrl ? (
            <Button
              type="link"
              href={authSession.loginUrl}
              style={{ paddingInline: 0, alignSelf: 'flex-start' }}
            >
              Sign in
            </Button>
          ) : undefined
        }
      />
    </div>
  );
};

export type StudioWorkflowsPageProps = {
  readonly workflows: QueryState<StudioWorkflowSummary[]>;
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly workflowStorageMode: string;
  readonly selectedWorkflowId: string;
  readonly selectedDirectoryId: string;
  readonly templateWorkflow: string;
  readonly draftMode: DraftMode;
  readonly legacySource: LegacySource;
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
  legacySource,
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
  const activeDirectory =
    directories.find((directory) => directory.directoryId === selectedDirectoryId) ||
    directories[0] ||
    null;
  const filteredWorkflows = (workflows.data ?? []).filter((workflow) => {
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

  const scopeSummaryDescription = workspaceSettings.isLoading
    ? 'Resolving the current scope.'
    : workspaceSettings.isError
      ? describeError(workspaceSettings.error)
      : 'Platform-managed scope for workspace workflows.';
  const draftSummaryDescription =
    activeWorkflowDescription || 'Open the selected workflow or start a blank draft.';
  const isImportedDraft = draftMode === 'new' && legacySource === 'playground';
  const resultsBodyRef = React.useRef<HTMLDivElement | null>(null);
  const [resultsViewportHeight, setResultsViewportHeight] = React.useState<
    number | null
  >(null);

  const clampResultsViewportHeight = React.useCallback((value: number) => {
    const minHeight = 220;
    const maxHeight =
      typeof window === 'undefined'
        ? 520
        : Math.max(minHeight, Math.min(720, window.innerHeight - 220));
    return Math.min(Math.max(value, minHeight), maxHeight);
  }, []);

  React.useLayoutEffect(() => {
    if (typeof window === 'undefined') {
      return undefined;
    }

    let frameId = 0;
    let resizeObserver: ResizeObserver | null = null;

    const updateViewportHeight = () => {
      const rect = resultsBodyRef.current?.getBoundingClientRect();
      if (!rect) {
        return;
      }

      const nextHeight = clampResultsViewportHeight(window.innerHeight - rect.top - 24);
      setResultsViewportHeight((current) =>
        current === nextHeight ? current : nextHeight,
      );
    };

    const scheduleViewportHeightUpdate = () => {
      window.cancelAnimationFrame(frameId);
      frameId = window.requestAnimationFrame(updateViewportHeight);
    };

    scheduleViewportHeightUpdate();

    window.addEventListener('resize', scheduleViewportHeightUpdate);
    window.addEventListener('scroll', scheduleViewportHeightUpdate, true);

    if (typeof ResizeObserver !== 'undefined') {
      resizeObserver = new ResizeObserver(scheduleViewportHeightUpdate);
      if (resultsBodyRef.current) {
        resizeObserver.observe(resultsBodyRef.current);
        if (resultsBodyRef.current.parentElement) {
          resizeObserver.observe(resultsBodyRef.current.parentElement);
        }
      }
    }

    return () => {
      window.cancelAnimationFrame(frameId);
      window.removeEventListener('resize', scheduleViewportHeightUpdate);
      window.removeEventListener('scroll', scheduleViewportHeightUpdate, true);
      resizeObserver?.disconnect();
    };
  }, [
    activeWorkflowName,
    clampResultsViewportHeight,
    filteredWorkflows.length,
    isScopeMode,
    selectedWorkflowId,
    workflowSearch,
  ]);

  return (
    <Row gutter={[16, 16]} align="stretch" style={workflowWorkspaceRowStyle}>
      {!isScopeMode ? (
        <Col xs={24} xl={8} xxl={7} style={workflowStretchColumnStyle}>
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
                    Directories
                  </Typography.Text>
                  <Typography.Text style={workflowSectionCopyStyle}>
                    New workflows are created in the selected directory.
                  </Typography.Text>
                </div>
                <Button
                  type="text"
                  icon={<FolderAddOutlined />}
                  onClick={onToggleDirectoryForm}
                  aria-label="Toggle workflow directory form"
                  style={workflowPanelIconButtonStyle}
                />
              </div>

              {workspaceSettings.isLoading ? (
                <Typography.Text type="secondary">
                  Loading workflow directories...
                </Typography.Text>
              ) : workspaceSettings.isError ? (
                <StudioNoticeCard
                  type="error"
                  title="Failed to load workspace settings"
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
                          placeholder="Directory label"
                          value={directoryLabel}
                          onChange={(event) => onSetDirectoryLabel(event.target.value)}
                        />
                        <Button type="primary" onClick={onAddDirectory}>
                          Add directory
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
                          description="No workflow directories are configured yet."
                        />
                      </div>
                    </div>
                  )}
                </div>
              )}
            </section>

          </div>
        </Col>
      ) : null}

      <Col
        xs={24}
        xl={isScopeMode ? 24 : 16}
        xxl={isScopeMode ? 24 : 17}
        style={workflowStretchColumnStyle}
      >
        <section style={workflowBrowserStyle}>
          <input
            ref={workflowImportInputRef}
            hidden
            accept=".yaml,.yml,text/yaml,text/x-yaml"
            type="file"
            onChange={onWorkflowImportChange}
          />

          <div style={workflowToolbarSurfaceStyle}>
            <div style={workflowToolbarStackStyle}>
              <div style={workflowSummaryStripStyle}>
                {isScopeMode ? (
                  <div
                    style={{
                      ...workflowSummaryCardStyle,
                      ...(activeDirectory ? workflowSurfaceActiveStyle : {}),
                    }}
                  >
                    <div style={workflowSummaryCardRowStyle}>
                      <div style={workflowSummaryCardBodyStyle}>
                        <Typography.Text style={workflowSectionHeadingStyle}>
                          Current scope
                        </Typography.Text>
                        <Typography.Text
                          strong
                          style={workflowCardTitleStyle}
                          ellipsis={{ tooltip: activeDirectory?.label || 'Resolved by Studio' }}
                        >
                          {activeDirectory?.label || 'Resolved by Studio'}
                        </Typography.Text>
                        <Typography.Text
                          style={workflowSummaryHintStyle}
                          ellipsis={{ tooltip: scopeSummaryDescription }}
                        >
                          {scopeSummaryDescription}
                        </Typography.Text>
                      </div>
                    </div>
                  </div>
                ) : null}

                <div style={workflowSummaryCardStyle}>
                  <div style={workflowSummaryCardRowStyle}>
                    <div style={workflowSummaryCardBodyStyle}>
                      <div style={workflowSummaryEyebrowRowStyle}>
                        <Typography.Text style={workflowSectionHeadingStyle}>
                          Current draft
                        </Typography.Text>
                        <Space wrap size={[6, 6]}>
                          {selectedWorkflowId ? (
                            <Tag color="processing">Workspace workflow</Tag>
                          ) : null}
                          {templateWorkflow ? (
                            <Tag color="success">Published template</Tag>
                          ) : null}
                          {isImportedDraft ? (
                            <Tag color="warning">Imported draft</Tag>
                          ) : draftMode === 'new' ? (
                            <Tag color="gold">Blank draft</Tag>
                          ) : null}
                        </Space>
                      </div>
                      <Typography.Text
                        strong
                        style={workflowCardTitleStyle}
                        ellipsis={{ tooltip: activeWorkflowName || 'No draft selected' }}
                      >
                        {activeWorkflowName || 'No draft selected'}
                      </Typography.Text>
                      <Typography.Text
                        style={workflowSummaryHintStyle}
                        ellipsis={{ tooltip: draftSummaryDescription }}
                      >
                        {draftSummaryDescription}
                      </Typography.Text>
                    </div>
                  </div>
                </div>
              </div>

              <div style={workflowToolbarLayoutStyle}>
                <div style={workflowSearchFieldStyle}>
                  <SearchOutlined
                    style={{ color: 'var(--ant-color-text-tertiary)' }}
                  />
                  <input
                    aria-label="Search workflows"
                    placeholder="Search workflows"
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
                    Import
                  </Button>
                  <Button
                    type="primary"
                    icon={<PlusOutlined />}
                    onClick={onStartBlankDraft}
                    style={workflowSolidActionStyle}
                  >
                    New workflow
                  </Button>
                </div>
              </div>
            </div>
          </div>

          <div
            data-testid="studio-workflows-results-body"
            ref={resultsBodyRef}
            style={{
              ...workflowResultsBodyStyle,
              ...(resultsViewportHeight
                ? {
                    height: resultsViewportHeight,
                    maxHeight: resultsViewportHeight,
                  }
                : null),
            }}
          >
            {workflows.isLoading ? (
              <Typography.Text type="secondary">
                Loading workflows...
              </Typography.Text>
            ) : workflows.isError ? (
              <StudioNoticeCard
                type="error"
                title="Failed to load workspace workflows"
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
                              <span>{workflow.stepCount} steps</span>
                              <span>{formatDateTime(workflow.updatedAtUtc)}</span>
                            </div>
                            <Typography.Paragraph
                              style={workflowCardDescriptionCompactStyle}
                            >
                              {workflow.description || 'No description provided.'}
                            </Typography.Paragraph>
                          </div>
                        </div>
                        <span
                          style={{
                            ...workflowOpenHintStyle,
                            display: 'block',
                          }}
                        >
                          Open
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
                        ? 'No workflows match the current search.'
                        : 'Create a workflow with the New workflow button above.'
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
  activeWorkflowDescription,
  activeDirectoryLabel,
  savePending,
  canSaveWorkflow,
  runPending,
  canOpenRunWorkflow,
  canRunWorkflow,
  executionCanStop,
  executionStopPending,
  runPrompt,
  executionNotice,
  logsPopoutMode = false,
  logsDetached = false,
  onSwitchStudioView,
  onOpenExecution,
  onSaveDraft,
  onExportDraft,
  onSetDraftWorkflowName,
  onSetWorkflowDescription,
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
  const [descriptionEditorOpen, setDescriptionEditorOpen] = React.useState(false);
  const [descriptionDraft, setDescriptionDraft] = React.useState(
    activeWorkflowDescription,
  );
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
    if (!descriptionEditorOpen) {
      setDescriptionDraft(activeWorkflowDescription);
    }
  }, [activeWorkflowDescription, descriptionEditorOpen]);

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
      ? `${currentWorkflowExecutions.length} runs`
      : 'No runs';
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
            title="Failed to load execution detail"
            description={describeError(selectedExecution.error)}
          />
        ) : selectedExecution.data ? (
          renderExecutionLogsSection({ fullscreen: true })
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="Pick a Studio execution to inspect its logs."
          />
        )}
      </div>
    );
  }

  const executionLogsBottomInset = logsCollapsed ? 84 : logsHeight + 28;

  return (
    <div style={{ ...cardStackStyle, position: 'relative' }}>
      {executions.isError ? (
        <StudioNoticeCard
          type="error"
          title="Failed to load Studio executions"
          description={describeError(executions.error)}
        />
      ) : null}
      {selectedExecution.isError ? (
        <StudioNoticeCard
          type="error"
          title="Failed to load execution detail"
          description={describeError(selectedExecution.error)}
        />
      ) : null}
      {executionNotice ? (
        <StudioNoticeCard
          type={executionNotice.type}
          title={
            executionNotice.type === 'error'
              ? 'Execution action failed'
              : executionNotice.type === 'info'
                ? 'Execution stop requested'
                : 'Execution updated'
          }
          description={executionNotice.message}
        />
      ) : null}
      {selectedExecutionDetail?.error ? (
        <StudioNoticeCard
          type="error"
          title="Execution error"
          description={selectedExecutionDetail.error}
        />
      ) : null}

      <div
        style={{
          background: '#F2F1EE',
          border: '1px solid #E6E3DE',
          borderRadius: 36,
          boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
          display: 'flex',
          flexDirection: 'column',
          minHeight: 'calc(100vh - 176px)',
          overflow: 'hidden',
        }}
      >
        <StudioWorkflowHeader
          activeView="execution"
          workflowDisplayName={activeWorkflowName || 'draft'}
          draftWorkflowName={draftWorkflowName}
          activeWorkflowDescription={activeWorkflowDescription}
          descriptionEditorOpen={descriptionEditorOpen}
          descriptionDraft={descriptionDraft}
          onSwitchStudioView={onSwitchStudioView}
          onSetDraftWorkflowName={onSetDraftWorkflowName}
          onOpenDescriptionEditor={(open) => {
            setDescriptionEditorOpen(open);
            if (open) {
              setDescriptionDraft(activeWorkflowDescription);
            }
          }}
          onDescriptionDraftChange={setDescriptionDraft}
          onCancelDescriptionEdit={() => {
            setDescriptionDraft(activeWorkflowDescription);
            setDescriptionEditorOpen(false);
          }}
          onSaveDescription={() => {
            onSetWorkflowDescription(descriptionDraft);
            setDescriptionEditorOpen(false);
          }}
          actions={
            <>
              <Button
                type="text"
                shape="circle"
                icon={<CheckOutlined />}
                loading={savePending}
                disabled={!canSaveWorkflow}
                onClick={onSaveDraft}
                aria-label="Save"
                title="Save"
              />
              <Button
                type="text"
                shape="circle"
                icon={<UploadOutlined />}
                onClick={onExportDraft}
                aria-label="Export"
                title="Export"
              />
              <Button
                type="primary"
                shape="circle"
                icon={runPending ? <LoadingOutlined /> : <CaretRightFilled />}
                disabled={!canOpenRunWorkflow || runPending}
                onClick={() => setRunModalOpen(true)}
                aria-label="Run"
                title="Run"
              />
              {executionCanStop ? (
                <Button
                  danger
                  type="text"
                  shape="circle"
                  icon={executionStopPending ? <LoadingOutlined /> : <CloseCircleFilled />}
                  onClick={onStopExecution}
                  disabled={executionStopPending}
                  aria-label="Stop"
                  title="Stop"
                />
              ) : null}
            </>
          }
        />

        <section
          style={{
            background: '#F2F1EE',
            display: 'flex',
            flex: 1,
            flexDirection: 'column',
            minHeight: 0,
            overflow: 'hidden',
          }}
        >
          <div
            style={{
              flex: 1,
              minHeight: 0,
              overflow: 'hidden',
              position: 'relative',
            }}
          >
            <div className="canvas-overlay-stack">
              <div className="canvas-meta-card">
                <div className="canvas-meta-label">{activeDirectoryLabel}</div>
                <div className="canvas-meta-value">
                  {workflowGraph.nodes.length} nodes · {workflowGraph.edges.length} edges
                </div>
              </div>

              <div className="canvas-meta-card canvas-meta-card-wide">
                <div className="canvas-meta-label">Run</div>
                <select
                  className="canvas-meta-select"
                  value={selectedExecutionDetail?.executionId || ''}
                  onChange={(event) => {
                    if (event.target.value) {
                      onOpenExecution(event.target.value);
                    }
                  }}
                >
                  <option value="">{executionSummaryLabel}</option>
                  {currentWorkflowExecutions.map((execution) => (
                    <option key={execution.executionId} value={execution.executionId}>
                      {formatDateTime(execution.startedAtUtc)} · {execution.status}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <GraphCanvas
              height="100%"
              bottomInset={executionLogsBottomInset}
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
            {renderExecutionLogsSection({ overlay: true })}
          </div>
        </section>
      </div>

      <Modal
        open={runModalOpen}
        title="Run"
        onCancel={() => setRunModalOpen(false)}
        onOk={() => {
          void onStartExecution();
          setRunModalOpen(false);
        }}
        okText="Run"
        okButtonProps={{
          disabled: !canRunWorkflow,
          loading: runPending,
          icon: <CaretRightFilled />,
        }}
      >
        <div style={cardStackStyle}>
          <Typography.Text type="secondary">
            Execution prompt will be passed into the workflow as{' '}
            <Typography.Text code>$input</Typography.Text>.
          </Typography.Text>
          <Input.TextArea
            aria-label="Studio execution prompt"
            autoSize={{ minRows: 6, maxRows: 10 }}
            className="run-prompt-textarea"
            placeholder="What should this run do?"
            value={runPrompt}
            onChange={(event) => onRunPromptChange(event.target.value)}
          />
        </div>
      </Modal>
    </div>
  );
};

export type StudioEditorPageProps = {
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
  readonly projectEntrySurface?: 'chat' | 'invoke';
  readonly projectEntryReadyForCurrentWorkflow?: boolean;
  readonly gAgentTypes: readonly RuntimeGAgentTypeDescriptor[];
  readonly gAgentTypesLoading: boolean;
  readonly gAgentTypesError: unknown;
  readonly gAgentActorGroups: readonly RuntimeGAgentActorGroup[];
  readonly gAgentActorsLoading: boolean;
  readonly gAgentActorsError: unknown;
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
  readonly onBindGAgent: (input: {
    displayName?: string;
    actorTypeName: string;
    preferredActorId?: string;
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
  selectedWorkflow,
  templateWorkflow,
  connectors,
  draftYaml,
  draftWorkflowName,
  draftDirectoryId,
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
  projectEntrySurface = 'invoke',
  projectEntryReadyForCurrentWorkflow = false,
  gAgentTypes,
  gAgentTypesLoading,
  gAgentTypesError,
  gAgentActorGroups,
  gAgentActorsLoading,
  gAgentActorsError,
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
  const [inspectorDrawerOpen, setInspectorDrawerOpen] = React.useState(false);
  const [runModalOpen, setRunModalOpen] = React.useState(false);
  const [gAgentModalOpen, setGAgentModalOpen] = React.useState(false);
  const [gAgentBindingPending, setGAgentBindingPending] = React.useState(false);
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
  const savedGAgentActorIds = React.useMemo(
    () =>
      collectRuntimeGAgentActorIds(
        gAgentDraft.actorTypeName,
        gAgentActorGroups,
        selectedDiscoveredGAgentType,
      ),
    [gAgentActorGroups, gAgentDraft.actorTypeName, selectedDiscoveredGAgentType],
  );
  const selectedSavedGAgentActorId = React.useMemo(() => {
    const normalizedPreferredActorId = gAgentDraft.preferredActorId.trim();
    if (!normalizedPreferredActorId || !savedGAgentActorIds.includes(normalizedPreferredActorId)) {
      return undefined;
    }

    return normalizedPreferredActorId;
  }, [gAgentDraft.preferredActorId, savedGAgentActorIds]);
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

  const openToolDrawer = React.useCallback((mode: StudioToolDrawerMode) => {
    setInspectorDrawerOpen(false);
    setToolDrawerMode(mode);
  }, []);

  const openInspectorDrawer = React.useCallback((tab: StudioInspectorTab) => {
    setToolDrawerMode(null);
    onSetInspectorTab(tab);
    setInspectorDrawerOpen(true);
  }, [onSetInspectorTab]);

  const openGAgentModal = React.useCallback(() => {
    setGAgentDraft((current) => ({
      ...current,
      displayName: current.displayName || draftWorkflowName || templateWorkflowName || '',
      actorTypeName:
        current.actorTypeName.trim() ||
        (gAgentTypes[0]
          ? buildRuntimeGAgentAssemblyQualifiedName(gAgentTypes[0])
          : '') ||
        '',
      endpoints:
        current.endpoints.length > 0
          ? current.endpoints
          : [createStudioGAgentBindingEndpointDraft()],
      openRunsEndpointId:
        current.openRunsEndpointId ||
        current.endpoints[0]?.endpointId ||
        'run',
    }));
    setGAgentModalOpen(true);
  }, [draftWorkflowName, gAgentTypes, templateWorkflowName]);

  const submitGAgentBinding = React.useCallback(async (openRuns: boolean) => {
    setGAgentBindingPending(true);
    try {
      await onBindGAgent(
        {
          displayName: gAgentDraft.displayName,
          actorTypeName: gAgentDraft.actorTypeName,
          preferredActorId: gAgentDraft.preferredActorId,
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
      setGAgentModalOpen(false);
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

  React.useEffect(() => {
    if (inspectorTab === 'node') {
      setInspectorDrawerOpen(hasSelectedGraphNode && toolDrawerMode === null);
    }
  }, [hasSelectedGraphNode, inspectorTab, toolDrawerMode]);

  React.useEffect(() => {
    if (askAiPending || askAiNotice || askAiAnswer || askAiReasoning) {
      openToolDrawer('ask-ai');
    }
  }, [askAiAnswer, askAiNotice, askAiPending, askAiReasoning, openToolDrawer]);

  React.useEffect(() => {
    if (!descriptionEditorOpen) {
      setDescriptionDraft(activeWorkflowDescription);
    }
  }, [activeWorkflowDescription, descriptionEditorOpen]);

  const handleAddNodeFromPalette = (stepType: string, connectorName?: string) => {
    onAddGraphNode(stepType, connectorName, pendingAddPosition);
    setToolDrawerMode(null);
  };

  const askAiStatusText = askAiPending
    ? 'Generating and validating YAML...'
    : askAiAnswer.trim()
      ? 'Validated YAML applied to the current draft.'
      : 'Return format: workflow YAML only.';
  const toolDrawerVisible = Boolean(draftYaml) && toolDrawerMode !== null;
  const toolDrawerTitle =
    toolDrawerMode === 'palette' ? 'Add node' : 'Ask AI';
  const inspectorDrawerVisible =
    Boolean(draftYaml) &&
    inspectorDrawerOpen &&
    (inspectorTab !== 'node' || hasSelectedGraphNode);
  const inspectorDrawerTitle =
    inspectorTab === 'node'
      ? selectedGraphNodeId
        ? `Node · ${selectedGraphNodeId}`
        : 'Node'
      : inspectorTab === 'roles'
        ? 'Roles'
        : 'YAML';
  const nodePaletteDrawerContent = (
    <div style={cardStackStyle}>
      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>Node library</Typography.Text>
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          Search primitives or configured connectors, then insert the next step at
          the current canvas target.
        </Typography.Paragraph>
        <Input
          allowClear
          prefix={<SearchOutlined />}
          placeholder="Search primitives or connectors"
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
                          Insert
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
              Configured connectors
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
                          <Tag color="success">enabled</Tag>
                        ) : (
                          <Tag color="default">disabled</Tag>
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
                        Insert
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
  const askAiDrawerContent = (
    <div style={cardStackStyle}>
      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>Workflow prompt</Typography.Text>
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          Describe the workflow outcome. Studio applies validated YAML back into
          the current draft automatically.
        </Typography.Paragraph>
        <Input.TextArea
          aria-label="Studio AI workflow prompt"
          autoSize={{ minRows: 5, maxRows: 10 }}
          placeholder="Build a workflow that triages incidents, routes risky cases to human approval, and posts the result to Slack."
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
            {askAiPending ? 'Thinking' : 'Generate'}
          </Button>
        </div>
      </div>

      {askAiNotice ? (
        <StudioNoticeCard
          type={askAiNotice.type}
          title={
            askAiNotice.type === 'error'
              ? 'Studio AI generation failed'
              : 'Studio AI generation updated the draft'
          }
          description={askAiNotice.message}
        />
      ) : null}

      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>Thinking</Typography.Text>
        <pre style={{ ...codeBlockStyle, margin: 0, maxHeight: 160 }}>
          {askAiReasoning || 'LLM reasoning will stream here.'}
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
          <Typography.Text strong>Validated YAML</Typography.Text>
          <Tag color={askAiAnswer.trim() ? 'success' : 'default'}>
            {askAiAnswer.trim() ? 'Applied to draft' : 'Waiting for valid YAML'}
          </Tag>
        </div>
        <pre style={{ ...codeBlockStyle, margin: 0, maxHeight: 260 }}>
          {askAiAnswer || 'Validated workflow YAML will appear here.'}
        </pre>
      </div>
    </div>
  );

  const editorStatusNotice =
    [publishNotice, runNotice, workflowImportNotice, saveNotice].find(
      (notice) => notice?.type === 'error',
    ) || null;
  const projectEntryIsChat = projectEntrySurface === 'chat';

  const editorGuidance = !hasResolvedProject
    ? {
        type: 'warning' as const,
        title: 'Next: Resolve project',
        description:
          'Resolve this scope first. Then save, run, and bind from the project path.',
        statusTags: [
          { label: 'Project pending', color: 'warning' },
          { label: 'Draft', color: 'default' },
        ],
        secondaryAction: {
          label: 'Open Project Overview',
          onClick: onOpenProjectOverview,
        },
        guideTitle: 'Resolve project context first',
        guideDescription:
          'Studio can keep editing locally, but project actions stay incomplete until this scope resolves into a real project.',
        guideHighlightSteps: ['studio-draft'] as const,
      }
    : !hasNamedDraft
      ? {
          type: 'warning' as const,
          title: 'Next: Finish draft',
          description:
            'Add a workflow name and valid YAML before save, run, or bind.',
          statusTags: [
            { label: 'Draft incomplete', color: 'warning' },
            { label: 'Name + YAML', color: 'default' },
          ],
          guideTitle: 'Finish the draft before publish',
          guideDescription:
            'Save, run, and bind unlock after the draft has a valid name and YAML payload.',
          guideHighlightSteps: ['studio-draft'] as const,
        }
      : isDraftDirty && canSaveWorkflow
        ? {
            type: 'info' as const,
            title: 'Next: Save asset',
            description:
              'Save the named workflow asset first so the project catalog stays in sync.',
            statusTags: [
              { label: 'Unsaved changes', color: 'processing' },
              { label: 'Asset pending', color: 'default' },
            ],
            primaryAction: {
              label: 'Save asset',
              onClick: onSaveDraft,
              loading: savePending,
            },
            guideTitle: 'Save before verify or bind',
            guideDescription:
              'Saving creates or updates the project asset without changing the published binding.',
            guideHighlightSteps: ['save-asset'] as const,
          }
        : saveNotice?.type === 'success'
          ? {
              type: 'success' as const,
              title: 'Next: Run draft',
              description:
                'Run the inline draft first. Bind the project after the result looks right.',
              statusTags: [
                { label: 'Asset saved', color: 'success' },
                { label: 'Ready to test', color: 'processing' },
              ],
              primaryAction: {
                label: 'Run draft',
                onClick: onRunInConsole,
                disabled: !canOpenRunWorkflow,
              },
              secondaryAction: {
                label: 'Bind project',
                onClick: onPublishWorkflow,
                loading: publishPending,
                disabled: !canPublishWorkflow,
              },
              guideTitle: 'Verify the draft first',
              guideDescription:
                'Run draft tests the inline bundle, while Bind project switches the published entrypoint.',
              guideHighlightSteps: ['run-draft', 'bind-scope'] as const,
            }
          : !scopeBinding?.available
            ? {
                type: 'warning' as const,
                title: 'Next: Bind project',
                description:
                  'The asset is saved, but Project Invoke still has no default binding.',
                statusTags: [
                  { label: 'Saved asset', color: 'success' },
                  { label: 'No binding', color: 'warning' },
                ],
                primaryAction: {
                  label: 'Bind project',
                  onClick: onPublishWorkflow,
                  loading: publishPending,
                  disabled: !canPublishWorkflow,
                },
                secondaryAction: {
                  label: 'Open Project Overview',
                  onClick: onOpenProjectOverview,
                },
                guideTitle: 'Publish the project entrypoint',
                guideDescription:
                  'Binding makes this workflow the default published path for Project Invoke.',
                guideHighlightSteps: ['bind-scope'] as const,
              }
            : !projectEntryReadyForCurrentWorkflow
              ? {
                  type: 'warning' as const,
                  title: 'Next: Bind project',
                  description:
                    projectEntryIsChat
                      ? 'A different published service is live. Bind this workflow before opening Chat.'
                      : 'A different published service is live. Bind this workflow before opening Project Invoke.',
                  statusTags: [
                    { label: 'Binding active', color: 'success' },
                    { label: 'Different workflow', color: 'warning' },
                  ],
                  primaryAction: {
                    label: 'Bind project',
                    onClick: onPublishWorkflow,
                    loading: publishPending,
                    disabled: !canPublishWorkflow,
                  },
                  secondaryAction: {
                    label: 'Run draft',
                    onClick: onRunInConsole,
                    disabled: !canOpenRunWorkflow,
                  },
                  guideTitle: 'Publish this workflow before opening the live entry',
                  guideDescription:
                    projectEntryIsChat
                      ? 'Chat still points at a previously published workflow. Bind this workflow first if you want Chat to open the same flow you are editing.'
                      : 'Project Invoke still points at a previously published workflow. Bind this workflow first if you want the live entry to match the editor.',
                  guideHighlightSteps: ['bind-scope'] as const,
                }
            : {
                type: 'success' as const,
                title: projectEntryIsChat
                  ? 'Next: Open Chat'
                  : 'Next: Open Project Invoke',
                description:
                  projectEntryIsChat
                    ? 'The published binding is live. Use Chat to verify the user path, then Runs for deeper traces.'
                    : 'The published binding is live. Use Project Invoke, then Runs for deeper traces.',
                statusTags: [
                  { label: 'Binding active', color: 'success' },
                  {
                    label: projectEntryIsChat ? 'Chat ready' : 'Invoke ready',
                    color: 'processing',
                  },
                ],
                primaryAction: {
                  label: projectEntryIsChat
                    ? 'Open Chat'
                    : 'Open Project Invoke',
                  onClick: onOpenProjectInvoke,
                },
                secondaryAction: {
                  label: 'Run draft',
                  onClick: onRunInConsole,
                  disabled: !canOpenRunWorkflow,
                },
                guideTitle: projectEntryIsChat
                  ? 'Move from authoring to chat'
                  : 'Move from authoring to invoke',
                guideDescription:
                  projectEntryIsChat
                    ? 'Chat now routes through the published binding, while Run draft still works for inline verification.'
                    : 'Project Invoke now targets the published entrypoint, while Run draft still works for inline verification.',
                guideHighlightSteps: ['invoke-services', 'open-in-runs'] as const,
              };

  const editorFatalNotice = selectedWorkflow.isError ? (
    <StudioNoticeCard
      key="selected-workflow-error"
      type="error"
      title="Failed to load Studio workflow"
      description={describeError(selectedWorkflow.error)}
      compact
    />
  ) : templateWorkflow.isError ? (
    <StudioNoticeCard
      key="template-workflow-error"
      type="error"
      title="Failed to load published workflow template"
      description={describeError(templateWorkflow.error)}
      compact
    />
  ) : null;

  return (
    <div
      style={{
        ...cardStackStyle,
        paddingTop: 20,
        position: 'relative',
      }}
    >
      {!editorFatalNotice ? (
        <StudioGuidanceBar
          type={editorGuidance.type}
          title={editorGuidance.title}
          description={editorGuidance.description}
          statusTags={editorGuidance.statusTags}
          primaryAction={editorGuidance.primaryAction}
          secondaryAction={editorGuidance.secondaryAction}
          notice={editorStatusNotice}
          guideTitle={editorGuidance.guideTitle}
          guideDescription={editorGuidance.guideDescription}
          guideHighlightSteps={editorGuidance.guideHighlightSteps}
        />
      ) : null}

      <StudioScopeBindingPanel
        scopeId={resolvedScopeId}
        binding={scopeBinding}
        loading={scopeBindingLoading}
        error={scopeBindingError}
        pendingRevisionId={bindingActivationRevisionId}
        pendingRetirementRevisionId={bindingRetirementRevisionId}
        onActivateRevision={onActivateBindingRevision}
        onRetireRevision={onRetireBindingRevision}
      />

      {editorFatalNotice ? (
        <div style={studioNoticeStripStyle}>{editorFatalNotice}</div>
      ) : draftYaml ? (
        <>
          <input
            ref={workflowImportInputRef}
            hidden
            accept=".yaml,.yml,text/yaml,text/x-yaml"
            type="file"
            onChange={onWorkflowImportChange}
          />

          <div
            style={{
              background: '#F2F1EE',
              border: '1px solid #E6E3DE',
              borderRadius: 36,
              boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
              display: 'flex',
              flexDirection: 'column',
              minHeight: 'calc(100vh - 176px)',
              overflow: 'hidden',
            }}
          >
            <StudioWorkflowHeader
              activeView="editor"
              workflowDisplayName={workflowDisplayName}
              draftWorkflowName={draftWorkflowName}
              activeWorkflowDescription={activeWorkflowDescription}
              descriptionEditorOpen={descriptionEditorOpen}
              descriptionDraft={descriptionDraft}
              onSwitchStudioView={onSwitchStudioView}
              onSetDraftWorkflowName={onSetDraftWorkflowName}
              onOpenDescriptionEditor={(open) => {
                setDescriptionEditorOpen(open);
                if (open) {
                  setDescriptionDraft(activeWorkflowDescription);
                }
              }}
              onDescriptionDraftChange={setDescriptionDraft}
              onCancelDescriptionEdit={() => {
                setDescriptionDraft(activeWorkflowDescription);
                setDescriptionEditorOpen(false);
              }}
              onSaveDescription={() => {
                onSetWorkflowDescription(descriptionDraft);
                setDescriptionEditorOpen(false);
              }}
              actions={
                <>
                  <Button
                    icon={<AppstoreOutlined />}
                    onClick={() => openToolDrawer('palette')}
                  >
                    Add node
                  </Button>
                  <Button
                    icon={<RobotOutlined />}
                    type={toolDrawerMode === 'ask-ai' ? 'primary' : 'default'}
                    onClick={() => openToolDrawer('ask-ai')}
                  >
                    Ask AI
                  </Button>
                  <Button
                    icon={<BarsOutlined />}
                    onClick={onRunInConsole}
                    disabled={!resolvedScopeId}
                  >
                    Run draft
                  </Button>
                  <Button
                    icon={<RobotOutlined />}
                    onClick={openGAgentModal}
                    disabled={!resolvedScopeId}
                  >
                    GAgent service
                  </Button>
                  <Button
                    icon={<SafetyCertificateOutlined />}
                    loading={publishPending}
                    disabled={!canPublishWorkflow}
                    onClick={onPublishWorkflow}
                  >
                    Bind project
                  </Button>
                  <Button
                    type="text"
                    shape="circle"
                    icon={<CheckOutlined />}
                    loading={savePending}
                    disabled={!canSaveWorkflow}
                    onClick={onSaveDraft}
                    aria-label="Save"
                    title="Save"
                  />
                  <Button
                    type="text"
                    shape="circle"
                    icon={<UploadOutlined />}
                    onClick={onExportDraft}
                    aria-label="Export"
                    title="Export"
                  />
                  <Button
                    type="primary"
                    shape="circle"
                    icon={<CaretRightFilled />}
                    disabled={!canOpenRunWorkflow}
                    onClick={() => setRunModalOpen(true)}
                    aria-label="Run"
                    title="Run"
                  />
                </>
              }
            />

            <div style={{ padding: '12px 12px 0' }}>
              <div style={studioStatusStripStyle}>
                <div style={studioStatusPillStyle}>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    Active directory
                  </Typography.Text>
                  <Typography.Text strong ellipsis={{ tooltip: activeDirectoryLabel }}>
                    {activeDirectoryLabel}
                  </Typography.Text>
                </div>
                <div style={studioStatusPillStyle}>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    Graph
                  </Typography.Text>
                  <Typography.Text strong>
                    {workflowGraph.nodes.length} nodes · {workflowGraph.edges.length}{' '}
                    edges
                  </Typography.Text>
                </div>
                <div style={studioStatusPillStyle}>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    Draft state
                  </Typography.Text>
                  <Space wrap size={[6, 6]}>
                    <Tag color={isDraftDirty ? 'warning' : 'success'}>
                      {isDraftDirty ? 'Unsaved changes' : 'In sync'}
                    </Tag>
                    {selectedGraphNodeId ? (
                      <Typography.Text type="secondary">
                        Selected: {selectedGraphNodeId}
                      </Typography.Text>
                    ) : null}
                  </Space>
                </div>
              </div>
            </div>

            <div
              style={{
                display: 'flex',
                flex: 1,
                minHeight: 0,
                padding: 12,
              }}
            >
              <div
                style={studioCanvasViewportStyle}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 10,
                    position: 'absolute',
                    right: 16,
                    top: 16,
                    zIndex: 4,
                  }}
                >
                  <Button
                    shape="circle"
                    icon={<UserOutlined />}
                    onClick={() => openInspectorDrawer('roles')}
                    type={inspectorTab === 'roles' && inspectorDrawerOpen ? 'primary' : 'default'}
                    aria-label="Open roles inspector"
                    title="Roles"
                    style={{ height: 40, width: 40 }}
                  />
                  <Button
                    shape="circle"
                    icon={<CodeOutlined />}
                    onClick={() => openInspectorDrawer('yaml')}
                    type={inspectorTab === 'yaml' && inspectorDrawerOpen ? 'primary' : 'default'}
                    aria-label="Open YAML inspector"
                    title="YAML"
                    style={{ height: 40, width: 40 }}
                  />
                </div>

                {selectedGraphEdge ? (
                  <div
                    style={{
                      bottom: 24,
                      left: 24,
                      position: 'absolute',
                      zIndex: 2,
                    }}
                  >
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
                        <Typography.Text strong>Connection selected</Typography.Text>
                        <Typography.Text type="secondary">
                          {selectedGraphEdge.branchLabel
                            ? `${selectedGraphEdge.sourceStepId} -> ${selectedGraphEdge.targetStepId} (${selectedGraphEdge.branchLabel})`
                            : `${selectedGraphEdge.sourceStepId} -> ${selectedGraphEdge.targetStepId}`}
                        </Typography.Text>
                        <Space wrap size={[8, 8]}>
                          <Tag color={selectedGraphEdge.kind === 'branch' ? 'purple' : 'processing'}>
                            {selectedGraphEdge.kind === 'branch' ? 'branch' : 'next'}
                          </Tag>
                          {selectedGraphEdge.implicit ? <Tag>auto-flow</Tag> : null}
                          <Button
                            danger
                            size="small"
                            disabled={selectedGraphEdge.implicit}
                            onClick={onDeleteSelectedGraphEdge}
                          >
                            Remove connection
                          </Button>
                        </Space>
                      </div>
                    </div>
                  </div>
                ) : null}

                {workflowGraph.nodes.length === 0 ? (
                  <div
                    style={{
                      alignItems: 'center',
                      color: '#6B7280',
                      display: 'flex',
                      inset: 12,
                      justifyContent: 'center',
                      pointerEvents: 'none',
                      position: 'absolute',
                      textAlign: 'center',
                      zIndex: 1,
                    }}
                  >
                    <div>
                      <Typography.Text strong style={{ display: 'block' }}>
                        This workflow has no steps yet.
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        Right click the canvas to place the first workflow step.
                      </Typography.Text>
                    </div>
                  </div>
                ) : null}

                <GraphCanvas
                  nodes={workflowGraph.nodes}
                  edges={workflowGraph.edges}
                  height="calc(100vh - 278px)"
                  variant="studio"
                  overlayContent={
                    <>
                      <Drawer
                        open={toolDrawerVisible}
                        title={toolDrawerTitle}
                        placement="left"
                        size={420}
                        getContainer={false}
                        mask={false}
                        rootStyle={studioCanvasDrawerRootStyle}
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

                      <Drawer
                        open={inspectorDrawerVisible}
                        title={inspectorDrawerTitle}
                        placement="left"
                        size={420}
                        getContainer={false}
                        mask={false}
                        rootStyle={studioCanvasDrawerRootStyle}
                        onClose={() => setInspectorDrawerOpen(false)}
                        destroyOnClose={false}
                        styles={{ body: drawerBodyStyle }}
                      >
                        <div style={drawerScrollStyle}>
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
                      </Drawer>
                    </>
                  }
                  selectedNodeId={selectedGraphNodeId}
                  selectedEdgeId={selectedGraphEdge?.edgeId}
                  onNodeSelect={(nodeId) => {
                    setToolDrawerMode(null);
                    setInspectorDrawerOpen(true);
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
                    openToolDrawer('palette');
                  }}
                  onConnectNodes={onConnectGraphNodes}
                  onNodeLayoutChange={onUpdateGraphLayout}
                />

              </div>
            </div>
          </div>

          <Modal
            open={gAgentModalOpen}
            title="Bind GAgent service"
            onCancel={() => setGAgentModalOpen(false)}
            footer={[
              <Button
                key="cancel"
                onClick={() => setGAgentModalOpen(false)}
                disabled={gAgentBindingPending}
              >
                Cancel
              </Button>,
              <Button
                key="bind"
                onClick={() => void submitGAgentBinding(false)}
                loading={gAgentBindingPending}
                disabled={!resolvedScopeId}
              >
                Bind
              </Button>,
              <Button
                key="bind-open-runs"
                type="primary"
                onClick={() => void submitGAgentBinding(true)}
                loading={gAgentBindingPending}
                disabled={!resolvedScopeId}
              >
                Bind + Open Runs
              </Button>,
            ]}
          >
            <div style={cardStackStyle}>
              <Typography.Text type="secondary">
                Bind the scope default service directly to a static GAgent and optionally open the runtime runs workbench for the configured endpoint.
              </Typography.Text>
              <Input
                aria-label="GAgent display name"
                placeholder="Display name"
                value={gAgentDraft.displayName}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    displayName: event.target.value,
                  }))
                }
              />
              <Select
                aria-label="Discovered GAgent type"
                showSearch
                style={{ width: '100%' }}
                placeholder={
                  gAgentTypesLoading
                    ? 'Loading discovered GAgent types'
                    : gAgentTypes.length > 0
                    ? 'Select a discovered GAgent type'
                    : 'No discovered GAgent types available'
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
                  gAgentTypesLoading ? 'Loading GAgent types...' : 'No GAgent types found.'
                }
                onChange={(value) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    actorTypeName: value,
                  }))
                }
              />
              {gAgentTypesError ? (
                <Typography.Text type="danger">
                  {describeError(gAgentTypesError)}
                </Typography.Text>
              ) : (
                <Typography.Text type="secondary">
                  Studio discovers bindable GAgent types from the runtime capability endpoint and fills the actor type contract for you.
                </Typography.Text>
              )}
              <Input
                aria-label="GAgent actor type name"
                placeholder="Assembly-qualified actor type name"
                value={gAgentDraft.actorTypeName}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    actorTypeName: event.target.value,
                  }))
                }
              />
              <Select
                aria-label="Saved GAgent actor id"
                allowClear
                showSearch
                style={{ width: '100%' }}
                placeholder={
                  gAgentActorsLoading
                    ? 'Loading saved actors'
                    : savedGAgentActorIds.length > 0
                    ? 'Reuse a saved actor id (optional)'
                    : 'No saved actors for the selected type'
                }
                value={selectedSavedGAgentActorId}
                optionFilterProp="label"
                options={savedGAgentActorIds.map((actorId) => ({
                  value: actorId,
                  label: actorId,
                }))}
                notFoundContent={
                  gAgentActorsLoading ? 'Loading actor ids...' : 'No saved actors found.'
                }
                onChange={(value) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    preferredActorId: value ?? '',
                  }))
                }
              />
              {gAgentActorsError ? (
                <Typography.Text type="danger">
                  {describeError(gAgentActorsError)}
                </Typography.Text>
              ) : (
                <Typography.Text type="secondary">
                  Saved actor ids come from previous GAgent draft runs. You can still type a custom actor id below.
                </Typography.Text>
              )}
              <Input
                aria-label="GAgent preferred actor id"
                placeholder="Preferred actor id (optional)"
                value={gAgentDraft.preferredActorId}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    preferredActorId: event.target.value,
                  }))
                }
              />
              <Divider style={{ marginBlock: 8 }}>Endpoints</Divider>
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
                  <Typography.Text type="secondary">
                    Add one or more GAgent endpoints, then choose which endpoint
                    Runs should open after binding.
                  </Typography.Text>
                  <Button
                    icon={<PlusOutlined />}
                    onClick={() => addGAgentEndpointDraft()}
                  >
                    Add endpoint
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
                        Endpoint {endpointIndex + 1}
                      </Typography.Text>
                      <Button
                        danger
                        disabled={gAgentDraft.endpoints.length <= 1}
                        icon={<DeleteOutlined />}
                        onClick={() =>
                          removeGAgentEndpointDraft(endpointIndex)
                        }
                      >
                        Remove
                      </Button>
                    </div>
                    <Input
                      aria-label={`GAgent endpoint id ${endpointIndex + 1}`}
                      placeholder="Endpoint ID"
                      value={endpoint.endpointId}
                      onChange={(event) =>
                        updateGAgentEndpointDraft(endpointIndex, {
                          endpointId: event.target.value,
                        })
                      }
                    />
                    <Input
                      aria-label={`GAgent endpoint display name ${endpointIndex + 1}`}
                      placeholder="Endpoint display name"
                      value={endpoint.displayName}
                      onChange={(event) =>
                        updateGAgentEndpointDraft(endpointIndex, {
                          displayName: event.target.value,
                        })
                      }
                    />
                    <Select
                      aria-label={`GAgent endpoint kind ${endpointIndex + 1}`}
                      options={[
                        {
                          label: 'Command endpoint',
                          value: 'command',
                        },
                        {
                          label: 'Chat endpoint',
                          value: 'chat',
                        },
                      ]}
                      value={endpoint.kind}
                      onChange={(value) =>
                        updateGAgentEndpointDraft(endpointIndex, {
                          kind: value,
                        })
                      }
                    />
                    <Input
                      aria-label={`GAgent request type URL ${endpointIndex + 1}`}
                      placeholder="Request type URL"
                      value={endpoint.requestTypeUrl}
                      onChange={(event) =>
                        updateGAgentEndpointDraft(endpointIndex, {
                          requestTypeUrl: event.target.value,
                        })
                      }
                    />
                    <Input
                      aria-label={`GAgent response type URL ${endpointIndex + 1}`}
                      placeholder="Response type URL (optional)"
                      value={endpoint.responseTypeUrl}
                      onChange={(event) =>
                        updateGAgentEndpointDraft(endpointIndex, {
                          responseTypeUrl: event.target.value,
                        })
                      }
                    />
                    <Input.TextArea
                      aria-label={`GAgent endpoint description ${endpointIndex + 1}`}
                      autoSize={{ minRows: 2, maxRows: 4 }}
                      placeholder="Endpoint description"
                      value={endpoint.description}
                      onChange={(event) =>
                        updateGAgentEndpointDraft(endpointIndex, {
                          description: event.target.value,
                        })
                      }
                    />
                  </div>
                ))}
              </Space>
              <Select
                aria-label="GAgent runs endpoint"
                style={{ width: '100%' }}
                placeholder="Choose the endpoint that Runs should open"
                value={selectedOpenRunsEndpoint?.endpointId || undefined}
                options={launchableGAgentEndpoints.map((endpoint) => ({
                  value: endpoint.endpointId,
                  label: `${
                    endpoint.displayName.trim() || endpoint.endpointId.trim()
                  } (${endpoint.kind})`,
                }))}
                notFoundContent="Enter an endpoint ID to enable Runs launch."
                onChange={(value) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    openRunsEndpointId: value,
                  }))
                }
              />
              <Typography.Text type="secondary">
                {selectedOpenRunsEndpoint?.kind === 'chat'
                  ? 'Runs currently recognizes direct chat launches through the special "chat" endpoint id.'
                  : 'Command endpoints can pass either a text prompt or a custom payload draft into Runs.'}
              </Typography.Text>
              <Divider style={{ marginBlock: 8 }} />
              <Input.TextArea
                aria-label="GAgent run prompt"
                autoSize={{ minRows: 4, maxRows: 8 }}
                placeholder="Prompt for the runtime runs console"
                value={gAgentDraft.prompt}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    prompt: event.target.value,
                  }))
                }
              />
              <Input.TextArea
                aria-label="GAgent payload base64"
                autoSize={{ minRows: 3, maxRows: 6 }}
                placeholder="Payload base64 for custom request types (optional)"
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
            title="Test run"
            onCancel={() => setRunModalOpen(false)}
            onOk={() => {
              void onStartExecution();
              setRunModalOpen(false);
            }}
            okText="Open runtime runs"
            okButtonProps={{
              disabled: !canRunWorkflow,
              loading: runPending,
              icon: <CaretRightFilled />,
            }}
          >
            <div style={cardStackStyle}>
              <Typography.Text type="secondary">
                Studio will open the runtime runs console and execute this draft through <Typography.Text code>/api/scopes/{'{scopeId}'}/workflow/draft-run</Typography.Text>.
              </Typography.Text>
              <Input.TextArea
                aria-label="Studio execution prompt"
                autoSize={{ minRows: 6, maxRows: 10 }}
                placeholder="What should this run do?"
                value={runPrompt}
                onChange={(event) => onRunPromptChange(event.target.value)}
              />
            </div>
          </Modal>
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
  readonly userConfig: QueryState<StudioUserConfig>;
  readonly userConfigModels: QueryState<StudioUserConfigModelsResponse>;
  readonly userConfigDraft: StudioUserConfig | null;
  readonly selectedProvider: StudioProviderSettings | null;
  readonly hostMode: 'embedded' | 'proxy';
  readonly workflowStorageMode: 'workspace' | 'scope';
  readonly settingsDirty: boolean;
  readonly settingsPending: boolean;
  readonly userConfigDirty: boolean;
  readonly userConfigPending: boolean;
  readonly runtimeTestPending: boolean;
  readonly settingsNotice: StudioNoticeLike | null;
  readonly userConfigNotice: StudioNoticeLike | null;
  readonly runtimeTestResult: StudioRuntimeTestResult | null;
  readonly ornnHealth: QueryState<StudioOrnnHealthResult>;
  readonly ornnSkills: QueryState<StudioOrnnSkillSearchResult>;
  readonly directoryPath: string;
  readonly directoryLabel: string;
  readonly onSaveSettings: () => void;
  readonly onSaveUserConfig: () => void;
  readonly onTestRuntime: () => void;
  readonly onCloseSettingsPage?: () => void;
  readonly onSetSettingsDraft: React.Dispatch<
    React.SetStateAction<StudioSettingsDraftLike | null>
  >;
  readonly onSetUserConfigDraft: React.Dispatch<
    React.SetStateAction<StudioUserConfig | null>
  >;
  readonly onRefreshOrnnHealth: () => void;
  readonly onRefreshOrnnSkills: () => void;
  readonly onAddProvider: () => void;
  readonly onSelectProviderName: (providerName: string) => void;
  readonly onDeleteSelectedProvider: () => void;
  readonly onSetDefaultProvider: () => void;
  readonly onSetDirectoryPath: (value: string) => void;
  readonly onSetDirectoryLabel: (value: string) => void;
  readonly onAddDirectory: () => void;
  readonly onRemoveDirectory: (directoryId: string) => void;
};

type StudioSettingsSectionKey = 'runtime' | 'llm' | 'skills';

export const StudioSettingsPage: React.FC<StudioSettingsPageProps> = ({
  workspaceSettings,
  settings,
  settingsDraft,
  userConfig,
  userConfigModels,
  userConfigDraft,
  hostMode,
  settingsDirty,
  settingsPending,
  userConfigDirty,
  userConfigPending,
  runtimeTestPending,
  settingsNotice,
  userConfigNotice,
  runtimeTestResult,
  ornnHealth,
  ornnSkills,
  onSaveSettings,
  onSaveUserConfig,
  onTestRuntime,
  onCloseSettingsPage,
  onSetSettingsDraft,
  onSetUserConfigDraft,
  onRefreshOrnnHealth,
  onRefreshOrnnSkills,
}) => {
  const canEditRuntime = hostMode === 'proxy';
  const runtimeBaseUrl =
    settingsDraft?.runtimeBaseUrl ?? workspaceSettings.data?.runtimeBaseUrl ?? '';
  const runtimeFieldLabel = canEditRuntime
    ? 'Runtime URL'
    : 'Host-managed runtime URL';
  const runtimeActionLabel = canEditRuntime
    ? 'Test connection'
    : 'Check host runtime';
  const [activeSection, setActiveSection] =
    React.useState<StudioSettingsSectionKey>('runtime');
  const [llmFilterText, setLlmFilterText] = React.useState('');
  const [llmDropdownOpen, setLlmDropdownOpen] = React.useState(false);
  const llmInputRef = React.useRef<HTMLInputElement | null>(null);
  const llmContainerRef = React.useRef<HTMLDivElement | null>(null);
  const runtimeModeLabel = canEditRuntime ? 'Editable' : 'Host managed';
  const hostModeLabel = hostMode === 'proxy' ? 'Remote proxy' : 'Embedded host';
  const runtimeHealthValue = runtimeTestResult
    ? runtimeTestResult.reachable
      ? 'Reachable'
      : 'Needs attention'
    : 'Not checked';
  const runtimeHealthTone = runtimeTestResult
    ? runtimeTestResult.reachable
      ? 'success'
      : 'warning'
    : 'default';
  const runtimeStatus: 'idle' | 'testing' | 'success' | 'error' =
    runtimeTestPending
      ? 'testing'
      : runtimeTestResult
        ? runtimeTestResult.reachable
          ? 'success'
          : 'error'
        : 'idle';
  const readyLlmProviders = React.useMemo(
    () =>
      (userConfigModels.data?.providers ?? []).filter(
        (provider) => provider.status === 'ready',
      ),
    [userConfigModels.data?.providers],
  );
  const groupedLlmModels = React.useMemo(() => {
    const prefixToProvider: Record<string, string> = {};

    for (const provider of readyLlmProviders) {
      const slug = provider.providerSlug;
      const name = formatStudioUserConfigProviderLabel(provider);
      if (slug === 'openai') {
        for (const prefix of ['gpt-', 'o1-', 'o1', 'o3-', 'o3', 'o4-', 'chatgpt-']) {
          prefixToProvider[prefix] = name;
        }
      } else if (slug === 'anthropic') {
        prefixToProvider['claude-'] = name;
      } else if (slug === 'google-ai') {
        prefixToProvider['gemini-'] = name;
      } else if (slug === 'mistral') {
        for (const prefix of ['mistral-', 'codestral-', 'magistral-']) {
          prefixToProvider[prefix] = name;
        }
      } else if (slug === 'cohere') {
        prefixToProvider['command-'] = name;
      } else if (slug === 'deepseek') {
        prefixToProvider['deepseek-'] = name;
      } else if (slug) {
        prefixToProvider[`${slug}-`] = name;
      }
    }

    const query = llmFilterText.trim().toLowerCase();
    const groups = new Map<string, string[]>();
    for (const model of userConfigModels.data?.supportedModels ?? []) {
      if (query && !model.toLowerCase().includes(query)) {
        continue;
      }

      let providerName = 'Other';
      for (const [prefix, name] of Object.entries(prefixToProvider)) {
        if (model.startsWith(prefix) || model === prefix.replace(/-$/, '')) {
          providerName = name;
          break;
        }
      }

      if (!groups.has(providerName)) {
        groups.set(providerName, []);
      }

      groups.get(providerName)?.push(model);
    }

    return groups;
  }, [llmFilterText, readyLlmProviders, userConfigModels.data?.supportedModels]);
  const llmModelsLoading =
    userConfigModels.isLoading || Boolean(userConfigModels.isFetching);
  const hasLlmModelCatalog =
    (userConfigModels.data?.supportedModels?.length ?? 0) > 0;
  const ornnHealthPending =
    ornnHealth.isLoading || Boolean(ornnHealth.isFetching);
  const ornnSkillsPending =
    ornnSkills.isLoading || Boolean(ornnSkills.isFetching);
  const ornnBaseUrl =
    ornnHealth.data?.baseUrl ?? ornnSkills.data?.baseUrl ?? '';
  const ornnHealthStatus: 'idle' | 'testing' | 'success' | 'error' =
    ornnHealthPending
      ? 'testing'
      : ornnHealth.isError
        ? 'error'
        : ornnHealth.data
          ? ornnHealth.data.reachable
            ? 'success'
            : 'error'
          : 'idle';

  React.useEffect(() => {
    if (!llmDropdownOpen) {
      return undefined;
    }

    const handlePointerDown = (event: MouseEvent) => {
      if (
        llmContainerRef.current &&
        event.target instanceof Node &&
        !llmContainerRef.current.contains(event.target)
      ) {
        setLlmDropdownOpen(false);
      }
    };

    document.addEventListener('mousedown', handlePointerDown);
    return () => document.removeEventListener('mousedown', handlePointerDown);
  }, [llmDropdownOpen]);

  const renderSectionHeader = React.useCallback(
    (title: string, description?: string, action?: React.ReactNode) => (
      <div style={studioSettingsSectionHeaderStyle}>
        <div>
          <div className="panel-eyebrow">Settings</div>
          <div className="panel-title">{title}</div>
          {description ? (
            <Typography.Paragraph style={studioSettingsSectionDescriptionStyle}>
              {description}
            </Typography.Paragraph>
          ) : null}
        </div>
        {action}
      </div>
    ),
    [],
  );
  const settingsNoticeCard = settingsNotice ? (
    <StudioNoticeCard
      type={settingsNotice.type}
      title={settingsNotice.type === 'error' ? 'Settings update failed' : 'Settings updated'}
      description={settingsNotice.message}
      compact
    />
  ) : null;
  const userConfigNoticeCard = userConfigNotice ? (
    <StudioNoticeCard
      type={userConfigNotice.type}
      title={
        userConfigNotice.type === 'error'
          ? 'LLM config update failed'
          : 'LLM config updated'
      }
      description={userConfigNotice.message}
      compact
    />
  ) : null;
  const renderRuntimeSection = () => {
    if (workspaceSettings.isError) {
      return (
        <StudioNoticeCard
          type="error"
          title="Failed to load workspace settings"
          description={describeError(workspaceSettings.error)}
        />
      );
    }

    if (!settingsDraft) {
      return (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="Runtime settings are unavailable right now."
        />
      );
    }

    const runtimeStatusTitle =
      runtimeStatus === 'success'
        ? 'Connection succeeded'
        : runtimeStatus === 'error'
          ? 'Connection failed'
          : 'Testing runtime';
    const runtimeStatusDescription =
      runtimeStatus === 'success' || runtimeStatus === 'error'
        ? runtimeTestResult?.message || ''
        : 'Studio is checking the configured runtime endpoint.';

    return (
      <>
        {!canEditRuntime ? (
          <StudioNoticeCard
            type="info"
            title="Runtime is host-managed in embedded mode"
            description="Studio runs against the local host runtime. The endpoint is shown for reference and connectivity checks only."
            compact
          />
        ) : null}
        <div className="settings-section-card" style={cardStackStyle}>
          <div>
            <label className="field-label" htmlFor="studio-runtime-base-url">
              {runtimeFieldLabel}
            </label>
            <input
              id="studio-runtime-base-url"
              aria-label="Studio runtime base URL"
              className="panel-input mt-1"
              value={runtimeBaseUrl}
              disabled={!canEditRuntime}
              onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                onSetSettingsDraft((current) =>
                  current
                    ? { ...current, runtimeBaseUrl: event.target.value }
                    : current,
                )
              }
            />
          </div>
          <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
            <button
              type="button"
              onClick={() => onTestRuntime()}
              className="ghost-action justify-center"
              disabled={runtimeTestPending}
            >
              {runtimeTestPending ? 'Testing...' : runtimeActionLabel}
            </button>
            <button
              type="button"
              onClick={() => onSaveSettings()}
              className="solid-action justify-center"
              disabled={!settingsDirty || !settingsDraft || settingsPending}
            >
              {settingsPending ? 'Saving...' : 'Save runtime'}
            </button>
          </div>
        </div>
        {runtimeStatus !== 'idle' ? (
          <div className={`settings-status-card ${runtimeStatus}`}>
            <div
              style={{
                alignItems: 'center',
                display: 'flex',
                gap: 12,
                justifyContent: 'space-between',
              }}
            >
              <div style={cardStackStyle}>
                <Typography.Text strong>{runtimeStatusTitle}</Typography.Text>
                <Typography.Text type="secondary">
                  {runtimeStatusDescription}
                </Typography.Text>
              </div>
              <StudioSettingsStatusPill status={runtimeStatus} />
            </div>
            <div style={{ fontSize: 12, color: 'var(--studio-muted-text)' }}>
              {runtimeBaseUrl}
            </div>
            <div style={{ fontSize: 13, color: 'var(--studio-muted-text)' }}>
              {runtimeTestResult?.checkedUrl || runtimeTestResult?.message}
            </div>
          </div>
        ) : null}
      </>
    );
  };

  const renderLlmSection = () => {
    return (
      <>
        {userConfig.isError ? (
          <StudioNoticeCard
            type="error"
            title="Failed to load LLM settings"
            description={describeError(userConfig.error)}
            compact
          />
        ) : null}
        {userConfigModels.isError ? (
          <StudioNoticeCard
            type="error"
            title="Failed to load connected providers"
            description={describeError(userConfigModels.error)}
            compact
          />
        ) : null}
        {(llmModelsLoading ||
          (userConfigModels.data?.providers?.length ?? 0) > 0) ? (
          <div className="settings-section-card" style={cardStackStyle}>
            <div className="section-heading">Connected providers</div>
            {llmModelsLoading ? (
              <div
                style={{
                  alignItems: 'center',
                  color: 'var(--ant-color-text-tertiary)',
                  display: 'flex',
                  fontSize: 12,
                  gap: 8,
                  minHeight: 28,
                }}
              >
                <LoadingOutlined spin />
                <span>Loading providers...</span>
              </div>
            ) : (
              <div style={studioSettingsProviderPillsStyle}>
                {(userConfigModels.data?.providers ?? []).map((provider) => (
                  <span
                    key={provider.providerSlug}
                    style={{
                      ...studioSettingsProviderPillStyle,
                      ...(provider.status === 'ready'
                        ? studioSettingsProviderPillReadyStyle
                        : studioSettingsProviderPillPendingStyle),
                    }}
                  >
                    <span
                      style={{
                        ...studioSettingsPillDotStyle,
                        height: 6,
                        background:
                          provider.status === 'ready' ? '#22C55E' : '#D1D5DB',
                        width: 6,
                      }}
                    />
                    <span style={{ fontSize: 12, fontWeight: 500 }}>
                      {formatStudioUserConfigProviderLabel(provider)}
                    </span>
                  </span>
                ))}
              </div>
            )}
          </div>
        ) : null}
        <div className="settings-section-card" style={cardStackStyle}>
          <div className="section-heading">Default model</div>
          <div ref={llmContainerRef} style={{ position: 'relative' }}>
            <label className="field-label" htmlFor="studio-llm-default-model">
              Model
            </label>
            {hasLlmModelCatalog ? (
              <div style={{ marginTop: 4, position: 'relative' }}>
                <input
                  id="studio-llm-default-model"
                  ref={llmInputRef}
                  aria-label="Studio LLM default model"
                  className="panel-input"
                  value={
                    llmDropdownOpen
                      ? llmFilterText
                      : userConfigDraft?.defaultModel ?? ''
                  }
                  placeholder={
                    llmModelsLoading ? 'Loading...' : 'Select a model...'
                  }
                  disabled={!userConfigDraft || userConfigPending}
                  onChange={(event: React.ChangeEvent<HTMLInputElement>) => {
                    setLlmFilterText(event.target.value);
                    if (!llmDropdownOpen) {
                      setLlmDropdownOpen(true);
                    }
                  }}
                  onFocus={() => {
                    setLlmFilterText('');
                    setLlmDropdownOpen(true);
                  }}
                />
                <button
                  type="button"
                  aria-label="Toggle model list"
                  onClick={() => {
                    setLlmDropdownOpen((current) => {
                      const next = !current;
                      if (next) {
                        setLlmFilterText('');
                        llmInputRef.current?.focus();
                      }
                      return next;
                    });
                  }}
                  disabled={!userConfigDraft || userConfigPending}
                  style={{
                    background: 'transparent',
                    border: 'none',
                    color: 'var(--ant-color-text-tertiary)',
                    cursor: 'pointer',
                    padding: 0,
                    position: 'absolute',
                    right: 12,
                    top: '50%',
                    transform: 'translateY(-50%)',
                  }}
                >
                  <DownOutlined />
                </button>
                {llmDropdownOpen ? (
                  <div
                    style={{
                      background: '#fff',
                      border: '1px solid var(--ant-color-border-secondary)',
                      borderRadius: 12,
                      boxShadow: '0 18px 34px rgba(17, 24, 39, 0.12)',
                      left: 0,
                      marginTop: 4,
                      maxHeight: 280,
                      overflow: 'auto',
                      position: 'absolute',
                      right: 0,
                      zIndex: 20,
                    }}
                  >
                    {llmModelsLoading ? (
                      <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 12, padding: '8px 12px' }}>
                        Loading...
                      </div>
                    ) : groupedLlmModels.size === 0 ? (
                      <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 12, padding: '8px 12px' }}>
                        No matching models
                      </div>
                    ) : (
                      Array.from(groupedLlmModels.entries()).map(
                        ([providerName, models]) => (
                          <div key={providerName}>
                            <div
                              style={{
                                color: 'var(--ant-color-text-tertiary)',
                                fontSize: 10,
                                fontWeight: 600,
                                letterSpacing: '0.08em',
                                padding: '8px 12px 4px',
                                textTransform: 'uppercase',
                              }}
                            >
                              {providerName}
                            </div>
                            {models.map((model) => (
                              <button
                                key={model}
                                type="button"
                                onClick={() => {
                                  onSetUserConfigDraft((current) =>
                                    current
                                      ? { ...current, defaultModel: model }
                                      : current,
                                  );
                                  setLlmDropdownOpen(false);
                                  setLlmFilterText('');
                                }}
                                style={{
                                  background:
                                    model === userConfigDraft?.defaultModel
                                      ? 'rgba(37, 99, 235, 0.08)'
                                      : 'transparent',
                                  border: 'none',
                                  color:
                                    model === userConfigDraft?.defaultModel
                                      ? '#1D4ED8'
                                      : 'var(--ant-color-text)',
                                  cursor: 'pointer',
                                  display: 'block',
                                  fontSize: 13,
                                  fontWeight:
                                    model === userConfigDraft?.defaultModel
                                      ? 600
                                      : 400,
                                  padding: '8px 12px',
                                  textAlign: 'left',
                                  width: '100%',
                                }}
                              >
                                {model}
                              </button>
                            ))}
                          </div>
                        ),
                      )
                    )}
                  </div>
                ) : null}
              </div>
            ) : (
              <input
                id="studio-llm-default-model"
                aria-label="Studio LLM default model"
                className="panel-input mt-1"
                value={userConfigDraft?.defaultModel ?? ''}
                placeholder={llmModelsLoading ? 'Loading...' : 'Enter model name...'}
                disabled={!userConfigDraft || userConfigPending}
                onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                  onSetUserConfigDraft((current) =>
                    current
                      ? { ...current, defaultModel: event.target.value }
                      : current,
                  )
                }
              />
            )}
          </div>
          <div
            style={{
              color: 'var(--ant-color-text-tertiary)',
              fontSize: 11,
              lineHeight: 1.7,
            }}
          >
            The default model used by NyxID Gateway. Select from supported
            models, or type a model name manually.
          </div>
        </div>
      </>
    );
  };

  const renderSkillsSection = () => {
    return (
      <>
        <div className="settings-section-card" style={cardStackStyle}>
          <div className="section-heading">Ornn Platform</div>
          <div
            style={{
              display: 'grid',
              gap: 16,
              alignItems: 'end',
              gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
            }}
          >
            <div>
              <label className="field-label" htmlFor="studio-skills-ornn-url">
                Ornn Base URL
              </label>
              <input
                id="studio-skills-ornn-url"
                aria-label="Studio skills Ornn base URL"
                className="panel-input mt-1"
                value={ornnBaseUrl}
                disabled
                onChange={() => undefined}
              />
            </div>
            <button
              type="button"
              onClick={() => onRefreshOrnnHealth()}
              className="ghost-action justify-center"
              disabled={ornnHealthPending}
            >
              {ornnHealthPending ? 'Testing...' : 'Test connection'}
            </button>
            {ornnBaseUrl ? (
              <button
                type="button"
                onClick={() => {
                  if (typeof window !== 'undefined') {
                    window.open(ornnBaseUrl, '_blank', 'noopener,noreferrer');
                  }
                }}
                className="solid-action"
                style={studioSettingsPrimaryActionStyle}
              >
                Open Ornn Platform
              </button>
            ) : (
              <button
                type="button"
                className="solid-action"
                style={{
                  ...studioSettingsPrimaryActionStyle,
                  cursor: 'not-allowed',
                  opacity: 0.6,
                }}
                disabled
              >
                Open Ornn Platform
              </button>
            )}
          </div>
          {ornnHealthStatus !== 'idle' ? (
            <div className={`settings-status-card ${ornnHealthStatus}`}>
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  gap: 12,
                  justifyContent: 'space-between',
                }}
              >
                <Typography.Text strong>
                  {ornnHealthStatus === 'success'
                    ? 'Connected'
                    : ornnHealthStatus === 'testing'
                      ? 'Testing...'
                      : 'Failed'}
                </Typography.Text>
                <StudioSettingsStatusPill status={ornnHealthStatus} />
              </div>
              <Typography.Text type="secondary">
                {ornnHealth.isError
                  ? describeError(ornnHealth.error)
                  : ornnHealth.data?.message || 'Checking Ornn connectivity.'}
              </Typography.Text>
            </div>
          ) : null}
          <div style={studioSettingsMutedBlockStyle}>
            Agents automatically get <strong>ornn_search_skills</strong> and{' '}
            <strong>ornn_use_skill</strong> tools. To manage skills, use the Ornn
            platform.
          </div>
        </div>
        <div className="settings-section-card" style={cardStackStyle}>
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 12,
              justifyContent: 'space-between',
            }}
          >
            <Typography.Text strong>Your Skills</Typography.Text>
            <button
              type="button"
              onClick={() => onRefreshOrnnSkills()}
              className="ghost-action"
              disabled={ornnSkillsPending}
            >
              {ornnSkillsPending ? 'Loading...' : 'Refresh'}
            </button>
          </div>
          {ornnSkills.isError ? (
            <StudioNoticeCard
              type="error"
              title="Failed to load skills"
              description={describeError(ornnSkills.error)}
              compact
            />
          ) : (ornnSkills.data?.items?.length ?? 0) === 0 ? (
            <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 13 }}>
              {ornnSkillsPending
                ? 'Loading...'
                : ornnSkills.data?.message || 'Click Refresh to load skills from Ornn.'}
            </div>
          ) : (
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
              }}
            >
              {(ornnSkills.data?.items ?? []).map((skill) => (
                <div
                  key={`${skill.guid}:${skill.name}`}
                  style={{
                    background: '#fff',
                    border: '1px solid #EAE4DB',
                    borderRadius: 14,
                    padding: 12,
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 8,
                      justifyContent: 'space-between',
                      marginBottom: 6,
                    }}
                  >
                    <div
                      style={{
                        color: 'var(--ant-color-text)',
                        fontSize: 13,
                        fontWeight: 600,
                        minWidth: 0,
                        overflow: 'hidden',
                        textOverflow: 'ellipsis',
                        whiteSpace: 'nowrap',
                      }}
                    >
                      {skill.name}
                    </div>
                    <span
                      style={{
                        color: 'var(--ant-color-text-tertiary)',
                        fontSize: 10,
                        letterSpacing: '0.06em',
                        textTransform: 'uppercase',
                      }}
                    >
                      {skill.isPrivate ? 'private' : 'public'}
                    </span>
                  </div>
                  <div
                    style={{
                      color: 'var(--ant-color-text-secondary)',
                      fontSize: 12,
                      lineHeight: 1.6,
                    }}
                  >
                    {skill.description || 'No description provided.'}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </>
    );
  };

  const activeSectionContent =
    activeSection === 'runtime'
      ? renderRuntimeSection()
      : activeSection === 'llm'
        ? renderLlmSection()
        : renderSkillsSection();

  return (
    <div style={studioSettingsShellStyle}>
      <aside className="settings-sidebar" style={studioSettingsSidebarStyle}>
        {onCloseSettingsPage ? (
          <button
            type="button"
            onClick={onCloseSettingsPage}
            style={{
              alignItems: 'center',
              background: 'transparent',
              border: 'none',
              color: 'var(--ant-color-text-tertiary)',
              cursor: 'pointer',
              display: 'inline-flex',
              fontSize: 13,
              gap: 8,
              padding: 0,
            }}
          >
            <LeftOutlined style={{ fontSize: 14 }} />
            <span>Back to workspace</span>
          </button>
        ) : null}
        <div
          style={{
            ...studioSettingsNavListStyle,
            marginTop: onCloseSettingsPage ? 32 : 0,
          }}
        >
          <StudioSettingsNavButton
            active={activeSection === 'runtime'}
            icon={<ApiOutlined />}
            title="Runtime"
            description="Base URL and connectivity"
            onClick={() => setActiveSection('runtime')}
          />
          <StudioSettingsNavButton
            active={activeSection === 'llm'}
            icon={<DatabaseOutlined />}
            title="LLM"
            description="Per-user settings on NyxID"
            onClick={() => setActiveSection('llm')}
          />
          <StudioSettingsNavButton
            active={activeSection === 'skills'}
            icon={<ToolOutlined />}
            title="Skills"
            description="Ornn skill platform"
            onClick={() => setActiveSection('skills')}
          />
        </div>
      </aside>
      <div style={studioSettingsContentPaneStyle}>
        <div style={studioSettingsSectionBodyStyle}>
          {renderSectionHeader(
            activeSection === 'runtime'
              ? 'Runtime'
              : activeSection === 'llm'
                ? 'LLM'
                : 'Skills',
            activeSection === 'runtime'
              ? undefined
              : activeSection === 'llm'
                ? 'Per-user configuration stored on NyxID. Changes sync across all your devices.'
                : 'Connect to your Ornn skill library. Skills are automatically available to all agents via tool calling.',
            activeSection === 'llm' ? (
              <button
                type="button"
                className="solid-action"
                style={{
                  ...studioSettingsPrimaryActionStyle,
                  ...(!userConfigDraft || userConfigPending || !userConfigDirty
                    ? { cursor: 'not-allowed', opacity: 0.6 }
                    : {}),
                }}
                onClick={() => onSaveUserConfig()}
                disabled={!userConfigDraft || userConfigPending || !userConfigDirty}
              >
                {userConfigPending ? 'Saving...' : 'Save config'}
              </button>
            ) : undefined,
          )}
          {activeSection === 'runtime'
            ? settingsNoticeCard
            : activeSection === 'llm'
              ? userConfigNoticeCard
              : null}
          {activeSectionContent}
        </div>
      </div>
    </div>
  );
};
