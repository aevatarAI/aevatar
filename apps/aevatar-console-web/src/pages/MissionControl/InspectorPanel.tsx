import {
  AlertOutlined,
  PauseCircleOutlined,
  ToolOutlined,
} from '@ant-design/icons';
import { Alert, Button, Card, Empty, Input, Space, Tag, Typography, theme } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import {
  drawerBodyStyle,
  drawerScrollStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
} from '@/shared/ui/proComponents';
import type {
  MissionActionFeedback,
  MissionControlSnapshot,
  MissionInterventionActionKind,
  MissionInterventionActionRequest,
  MissionInspectorMode,
  MissionInspectorPresentation,
  MissionRuntimeConnectionStatus,
  MissionTopologyNode,
} from './models';
import {
  formatConnectionLabel,
  formatInspectorPresentationLabel,
  formatInterventionLabel,
  formatMissionLabel,
  renderMissionKindIcon,
  resolveConnectionTagColor,
  resolveFeedbackTagColor,
  resolveMissionStatusTone,
  resolveObservationTone,
} from './presentation';

const monoStyle: React.CSSProperties = {
  fontFamily:
    "'SFMono-Regular', 'SFMono-Regular', Consolas, 'Liberation Mono', monospace",
};

type InspectorPanelProps = {
  actionFeedback?: MissionActionFeedback;
  connectionStatus: MissionRuntimeConnectionStatus;
  mode: MissionInspectorMode;
  onSubmitAction?: (action: MissionInterventionActionRequest) => Promise<void>;
  presentation: MissionInspectorPresentation;
  selectedNode?: MissionTopologyNode;
  snapshot: MissionControlSnapshot;
  submittingActionKind?: MissionInterventionActionKind;
};

const InspectorPanel: React.FC<InspectorPanelProps> = ({
  actionFeedback,
  connectionStatus,
  mode,
  onSubmitAction,
  presentation,
  selectedNode,
  snapshot,
  submittingActionKind,
}) => {
  const { token } = theme.useToken();
  const [comment, setComment] = useState('');
  const [payload, setPayload] = useState('');
  const focusNode =
    selectedNode ||
    snapshot.nodes.find((node) => node.id === snapshot.intervention?.nodeId);
  const showIntervention =
    mode === 'intervention' ? snapshot.intervention : undefined;
  const isDisconnected = connectionStatus === 'disconnected';
  const actionHint = useMemo(() => {
    if (!showIntervention) {
      return undefined;
    }

    switch (showIntervention.kind) {
      case 'waiting_signal':
        return 'Signal payload';
      case 'human_approval':
        return 'Approval note';
      default:
        return 'Operator note';
    }
  }, [showIntervention]);

  useEffect(() => {
    setComment('');
    setPayload('');
  }, [showIntervention?.key]);

  const handleSubmit = (kind: MissionInterventionActionKind) => {
    if (!showIntervention || !onSubmitAction) {
      return;
    }

    void onSubmitAction({
      comment: comment.trim() || undefined,
      kind,
      payload: payload.trim() || undefined,
    });
  };

  return (
    <div style={drawerBodyStyle}>
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          justifyContent: 'space-between',
          marginBottom: 8,
        }}
      >
        <div style={{ minWidth: 0 }}>
          <Typography.Title
            level={5}
            style={{ color: token.colorTextHeading, margin: 0 }}
          >
            Node Insight
          </Typography.Title>
          <Typography.Text style={{ color: token.colorTextTertiary }}>
            Inspect state, calls, and reasoning for the selected node; intervention automatically switches this panel into decision mode.
          </Typography.Text>
        </div>
        <Tag color={presentation === 'push' ? 'blue' : 'default'}>
          {formatInspectorPresentationLabel(presentation)}
        </Tag>
      </div>
      <Space size={[8, 8]} style={{ marginBottom: 10 }} wrap>
        <Tag color={resolveConnectionTagColor(connectionStatus)}>
          Connection: {formatConnectionLabel(connectionStatus)}
        </Tag>
        {actionFeedback ? (
          <Tag color={resolveFeedbackTagColor(actionFeedback.tone)}>
            {actionFeedback.message}
          </Tag>
        ) : null}
      </Space>
      <div style={drawerScrollStyle}>
        {actionFeedback ? (
          <Alert
            message={actionFeedback.message}
            showIcon
            style={{ borderRadius: 4, marginBottom: 12 }}
            type={
              actionFeedback.tone === 'error'
                ? 'error'
                : actionFeedback.tone === 'warning'
                  ? 'warning'
                  : actionFeedback.tone === 'success'
                    ? 'success'
                    : 'info'
            }
          />
        ) : null}
        {showIntervention ? (
          <Card
            size="small"
            styles={{ body: { padding: 14 } }}
            style={{
              background: token.colorWarningBg,
              borderColor: token.colorWarningBorder,
              borderRadius: 4,
              marginBottom: 12,
            }}
          >
            <Space direction="vertical" size={10} style={{ width: '100%' }}>
              <Space wrap size={[8, 8]}>
                <Tag color="gold">
                  <PauseCircleOutlined /> {formatInterventionLabel(showIntervention.kind)}
                </Tag>
                {showIntervention.timeoutLabel ? (
                  <Tag color="red">{showIntervention.timeoutLabel}</Tag>
                ) : null}
              </Space>
              <Typography.Title
                level={5}
                style={{ color: token.colorTextHeading, margin: 0 }}
              >
                {showIntervention.title}
              </Typography.Title>
              <Typography.Text style={{ color: token.colorTextSecondary }}>
                {showIntervention.summary}
              </Typography.Text>
              <div
                style={{
                  background: token.colorBgContainer,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 4,
                  padding: 12,
                }}
              >
                <Typography.Text style={{ color: token.colorTextTertiary }}>
                  Intervention prompt
                </Typography.Text>
                <Typography.Paragraph
                  style={{
                    color: token.colorTextHeading,
                    marginBottom: 0,
                    marginTop: 6,
                  }}
                >
                  {showIntervention.prompt}
                </Typography.Paragraph>
              </div>
              <Input.TextArea
                autoSize={{ minRows: 3, maxRows: 6 }}
                disabled={isDisconnected}
                placeholder={
                  showIntervention.kind === 'waiting_signal'
                    ? 'Example: {"market":"open","riskGate":"cleared"}'
                    : 'Add an approval note, risk comment, or missing context.'
                }
                value={showIntervention.kind === 'waiting_signal' ? payload : comment}
                onChange={(event) => {
                  if (showIntervention.kind === 'waiting_signal') {
                    setPayload(event.target.value);
                    return;
                  }

                  setComment(event.target.value);
                }}
              />
              {actionHint ? (
                <Typography.Text style={{ color: token.colorTextTertiary }}>
                  {actionHint}
                </Typography.Text>
              ) : null}
              <Space wrap size={[8, 8]}>
                {showIntervention.kind === 'waiting_signal' ? (
                  <Button
                    disabled={isDisconnected}
                    loading={submittingActionKind === 'signal'}
                    type="primary"
                    onClick={() => handleSubmit('signal')}
                  >
                    {showIntervention.primaryActionLabel}
                  </Button>
                ) : null}
                {showIntervention.kind === 'human_input' ? (
                  <Button
                    disabled={isDisconnected}
                    loading={submittingActionKind === 'resume'}
                    type="primary"
                    onClick={() => handleSubmit('resume')}
                  >
                    {showIntervention.primaryActionLabel}
                  </Button>
                ) : null}
                {showIntervention.kind === 'human_approval' ? (
                  <>
                    <Button
                      disabled={isDisconnected}
                      loading={submittingActionKind === 'approve'}
                      type="primary"
                      onClick={() => handleSubmit('approve')}
                    >
                      {showIntervention.primaryActionLabel}
                    </Button>
                    <Button
                      danger
                      disabled={isDisconnected}
                      loading={submittingActionKind === 'reject'}
                      onClick={() => handleSubmit('reject')}
                    >
                      {showIntervention.secondaryActionLabel || 'Reject'}
                    </Button>
                  </>
                ) : null}
              </Space>
            </Space>
          </Card>
        ) : null}

        {focusNode ? (
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Card
              size="small"
              styles={{ body: { padding: 14 } }}
              style={{
                background: token.colorBgContainer,
                borderColor: token.colorBorderSecondary,
                borderRadius: 4,
              }}
            >
              <Space direction="vertical" size={10} style={{ width: '100%' }}>
                <Space wrap size={[8, 8]}>
                  <Tag color="processing">{focusNode.lane}</Tag>
                  <Tag color={resolveMissionStatusTone(token, focusNode.status)}>
                    {formatMissionLabel(focusNode.status)}
                  </Tag>
                  <Tag color={resolveObservationTone(token, focusNode.observationStatus)}>
                    Observation: {formatMissionLabel(focusNode.observationStatus)}
                  </Tag>
                </Space>
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 10,
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      background: token.colorFillTertiary,
                      border: `1px solid ${token.colorBorderSecondary}`,
                      borderRadius: 4,
                      color: token.colorPrimary,
                      display: 'flex',
                      height: 36,
                      justifyContent: 'center',
                      width: 36,
                    }}
                  >
                    {renderMissionKindIcon(focusNode.kind)}
                  </div>
                  <div style={{ minWidth: 0 }}>
                    <Typography.Title
                      level={5}
                      style={{ color: token.colorTextHeading, margin: 0 }}
                    >
                      {focusNode.label}
                    </Typography.Title>
                    <Typography.Text style={{ color: token.colorTextTertiary }}>
                      {focusNode.role}
                    </Typography.Text>
                  </div>
                </div>
                <Typography.Text style={{ color: token.colorTextSecondary }}>
                  {focusNode.summary}
                </Typography.Text>
              </Space>
            </Card>

            <Card
              size="small"
              styles={{ body: { padding: 14 } }}
              style={{
                background: token.colorBgContainer,
                borderColor: token.colorBorderSecondary,
                borderRadius: 4,
              }}
            >
              <Space direction="vertical" size={12} style={{ width: '100%' }}>
                <Space size={8}>
                  <AlertOutlined />
                  <Typography.Text strong style={{ color: token.colorTextHeading }}>
                    State Snapshot
                  </Typography.Text>
                </Space>
                <div style={summaryFieldGridStyle}>
                  <div style={summaryFieldStyle}>
                    <span style={summaryFieldLabelStyle}>Current Conclusion</span>
                    <Typography.Text>{focusNode.snapshot.headline}</Typography.Text>
                  </div>
                  <div style={summaryFieldStyle}>
                    <span style={summaryFieldLabelStyle}>Current Step</span>
                    <Typography.Text>{focusNode.snapshot.currentStepId}</Typography.Text>
                  </div>
                  <div style={summaryFieldStyle}>
                    <span style={summaryFieldLabelStyle}>State Version</span>
                    <Typography.Text>{focusNode.snapshot.stateVersion}</Typography.Text>
                  </div>
                  <div style={summaryFieldStyle}>
                    <span style={summaryFieldLabelStyle}>Captured At</span>
                    <Typography.Text>{focusNode.snapshot.capturedAt}</Typography.Text>
                  </div>
                </div>
                <pre
                  style={{
                    ...monoStyle,
                    background: token.colorFillAlter,
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 4,
                    color: token.colorText,
                    margin: 0,
                    maxHeight: 220,
                    overflow: 'auto',
                    padding: 12,
                    whiteSpace: 'pre-wrap',
                    wordBreak: 'break-word',
                  }}
                >
                  {JSON.stringify(focusNode.snapshot.items, null, 2)}
                </pre>
              </Space>
            </Card>

            <Card
              size="small"
              styles={{ body: { padding: 14 } }}
              style={{
                background: token.colorBgContainer,
                borderColor: token.colorBorderSecondary,
                borderRadius: 4,
              }}
            >
              <Space direction="vertical" size={12} style={{ width: '100%' }}>
                <Space size={8}>
                  <ToolOutlined />
                  <Typography.Text strong style={{ color: token.colorTextHeading }}>
                    Tool Calls
                  </Typography.Text>
                </Space>
                {focusNode.toolCalls.map((toolCall) => (
                  <div
                    key={toolCall.id}
                    style={{
                      background: token.colorFillAlter,
                      border: `1px solid ${token.colorBorderSecondary}`,
                      borderRadius: 4,
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 8,
                      padding: 12,
                    }}
                    >
                    <Space wrap size={[8, 8]}>
                      <Tag color="blue">{toolCall.toolName}</Tag>
                      <Tag>{formatMissionLabel(toolCall.status)}</Tag>
                      <Tag>{toolCall.latencyMs} ms</Tag>
                    </Space>
                    <Typography.Text style={{ color: token.colorTextTertiary }}>
                      {toolCall.endpoint}
                    </Typography.Text>
                    <Typography.Text style={{ color: token.colorTextSecondary }}>
                      {toolCall.summary}
                    </Typography.Text>
                    <Typography.Text style={{ color: token.colorTextTertiary }}>
                      Input Summary: {toolCall.paramsSummary}
                    </Typography.Text>
                    <Typography.Text style={{ color: token.colorTextSecondary }}>
                      Output Summary: {toolCall.resultSummary}
                    </Typography.Text>
                  </div>
                ))}
              </Space>
            </Card>

            <Card
              size="small"
              styles={{ body: { padding: 14 } }}
              style={{
                background: token.colorBgContainer,
                borderColor: token.colorBorderSecondary,
                borderRadius: 4,
              }}
            >
              <Space direction="vertical" size={12} style={{ width: '100%' }}>
                <Typography.Text strong style={{ color: token.colorTextHeading }}>
                  Reasoning Summary
                </Typography.Text>
                {focusNode.reasoningChain.map((insight) => (
                  <div
                    key={insight.id}
                    style={{
                      background: token.colorFillAlter,
                      border: `1px solid ${token.colorBorderSecondary}`,
                      borderRadius: 4,
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 8,
                      padding: 12,
                    }}
                  >
                    <Space wrap size={[8, 8]}>
                      <Tag color="cyan">{insight.title}</Tag>
                      {typeof insight.confidence === 'number' ? (
                        <Tag>{Math.round(insight.confidence * 100)}% confidence</Tag>
                      ) : null}
                    </Space>
                    <Typography.Text style={{ color: token.colorTextSecondary }}>
                      {insight.summary}
                    </Typography.Text>
                    <Space wrap size={[6, 6]}>
                      {insight.evidence.map((item) => (
                        <Tag key={item}>{item}</Tag>
                      ))}
                    </Space>
                  </div>
                ))}
              </Space>
            </Card>
          </Space>
        ) : (
          <Empty
            description={
              connectionStatus === 'idle'
                ? 'Open Mission Control from a live run to inspect real snapshots, tool calls, and reasoning.'
                : 'Select a node to inspect its state, calls, and reasoning.'
            }
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        )}
      </div>
    </div>
  );
};

export default InspectorPanel;
