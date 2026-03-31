import {
  ApartmentOutlined,
  CodeOutlined,
  DatabaseOutlined,
  DeploymentUnitOutlined,
  RadarChartOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import type { theme } from 'antd';
import React from 'react';
import type {
  MissionFeedbackTone,
  MissionInterventionKind,
  MissionInspectorPresentation,
  MissionNodeStatus,
  MissionObservationStatus,
  MissionRuntimeConnectionStatus,
  MissionRunStatus,
  MissionTopologyNodeKind,
} from './models';

export type MissionThemeToken = ReturnType<typeof theme.useToken>['token'];

const missionLabelMap: Record<string, string> = {
  active: 'Active',
  build_requested: 'Build In Progress',
  completed: 'Completed',
  connecting: 'Connecting',
  delayed: 'Delayed',
  degraded: 'Fallback Sync',
  disconnected: 'Disconnected',
  draft: 'Draft',
  failed: 'Failed',
  human_approval: 'Approval Required',
  human_input: 'Input Required',
  idle: 'Detached',
  pending: 'Pending',
  promoted: 'Promoted',
  promotion_failed: 'Promotion Failed',
  proposed: 'Proposed',
  projection_settled: 'Projection Settled',
  published: 'Published',
  rejected: 'Rejected',
  rollback_requested: 'Rollback Requested',
  rolled_back: 'Rolled Back',
  running: 'Running',
  snapshot_available: 'Snapshot Available',
  stopped: 'Stopped',
  streaming: 'Streaming',
  suspended: 'Suspended',
  unavailable: 'Unavailable',
  validated: 'Validated',
  validation_failed: 'Validation Failed',
  waiting: 'Waiting',
  waiting_approval: 'Approval Required',
  waiting_signal: 'Waiting for Signal',
  workflow_completed: 'Workflow Completed',
  workflow_resumed: 'Workflow Resumed',
  workflow_role_actor_linked: 'Role Linked',
  workflow_role_reply_recorded: 'Role Reply Recorded',
  workflow_run_execution_started: 'Run Started',
  workflow_run_stopped: 'Run Stopped',
  workflow_signal_buffered: 'Signal Buffered',
  workflow_stopped: 'Workflow Stopped',
  workflow_suspended: 'Workflow Suspended',
  step_completed: 'Step Completed',
  step_requested: 'Step Requested',
  waiting_for_signal: 'Waiting for Signal',
  queued: 'Queued',
};

export function formatMissionLabel(value: string) {
  const normalized = value.trim().toLowerCase();
  if (missionLabelMap[normalized]) {
    return missionLabelMap[normalized];
  }

  return value
    .split('_')
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(' ');
}

export function resolveMissionStatusTone(
  token: MissionThemeToken,
  status: MissionRunStatus | MissionNodeStatus,
) {
  if (status === 'idle') {
    return token.colorTextTertiary;
  }

  if (status === 'completed') {
    return token.colorSuccess;
  }

  if (status === 'failed' || status === 'stopped') {
    return token.colorError;
  }

  if (
    status === 'waiting' ||
    status === 'waiting_signal' ||
    status === 'human_input' ||
    status === 'waiting_approval' ||
    status === 'suspended'
  ) {
    return token.colorWarning;
  }

  if (status === 'active' || status === 'running') {
    return token.colorPrimary;
  }

  return token.colorTextTertiary;
}

export function resolveObservationTone(
  token: MissionThemeToken,
  status: MissionObservationStatus,
) {
  if (status === 'unavailable') {
    return token.colorTextQuaternary;
  }

  if (status === 'projection_settled') {
    return token.colorSuccess;
  }

  if (status === 'delayed') {
    return token.colorError;
  }

  if (status === 'snapshot_available') {
    return token.colorWarning;
  }

  return token.colorPrimary;
}

export function renderMissionKindIcon(kind: MissionTopologyNodeKind) {
  switch (kind) {
    case 'entrypoint':
      return <RadarChartOutlined />;
    case 'coordinator':
      return <DeploymentUnitOutlined />;
    case 'research':
      return <RobotOutlined />;
    case 'tool':
      return <DatabaseOutlined />;
    case 'risk':
      return <SafetyCertificateOutlined />;
    case 'approval':
      return <UserOutlined />;
    case 'execution':
      return <ApartmentOutlined />;
    default:
      return <CodeOutlined />;
  }
}

export function formatInterventionLabel(kind: MissionInterventionKind) {
  switch (kind) {
    case 'waiting_signal':
      return 'Waiting for Signal';
    case 'human_input':
      return 'Input Required';
    case 'human_approval':
      return 'Approval Required';
    default:
      return 'Intervention';
  }
}

export function formatConnectionLabel(status: MissionRuntimeConnectionStatus) {
  switch (status) {
    case 'idle':
      return 'Detached';
    case 'connecting':
      return 'Connecting';
    case 'live':
      return 'Live';
    case 'degraded':
      return 'Fallback Sync';
    case 'disconnected':
      return 'Disconnected';
    default:
      return 'Runtime';
  }
}

export function formatInspectorPresentationLabel(
  presentation: MissionInspectorPresentation,
) {
  return presentation === 'push' ? 'Docked Panel' : 'Overlay Panel';
}

export function resolveConnectionTagColor(
  status: MissionRuntimeConnectionStatus,
): 'default' | 'processing' | 'success' | 'warning' | 'error' {
  switch (status) {
    case 'idle':
      return 'default';
    case 'live':
      return 'success';
    case 'connecting':
      return 'processing';
    case 'degraded':
      return 'warning';
    case 'disconnected':
      return 'error';
    default:
      return 'default';
  }
}

export function resolveFeedbackTagColor(
  tone: MissionFeedbackTone,
): 'processing' | 'success' | 'warning' | 'error' {
  switch (tone) {
    case 'success':
      return 'success';
    case 'warning':
      return 'warning';
    case 'error':
      return 'error';
    default:
      return 'processing';
  }
}
