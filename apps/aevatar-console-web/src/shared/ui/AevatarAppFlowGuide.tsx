import type { AlertProps } from 'antd';
import { Alert, Space, Tag, Typography } from 'antd';
import React from 'react';
import type { CSSProperties } from 'react';
import {
  cardStackStyle,
  embeddedPanelStyle,
  summaryFieldLabelStyle,
} from './proComponents';

export type AevatarAppFlowGuideStepId =
  | 'studio-draft'
  | 'save-asset'
  | 'run-draft'
  | 'bind-scope'
  | 'invoke-services'
  | 'open-in-runs'
  | 'runs';

type AevatarAppFlowGuideProps = {
  contextTitle: string;
  contextDescription: string;
  highlightSteps?: readonly AevatarAppFlowGuideStepId[];
  tone?: AlertProps['type'];
  compact?: boolean;
};

type FlowPathStep = {
  id: AevatarAppFlowGuideStepId;
  label: string;
  description: string;
  note?: string;
};

type FlowPath = {
  id: string;
  title: string;
  description: string;
  tagColor: string;
  steps: FlowPathStep[];
};

type DistinctionCard = {
  id: string;
  title: string;
  description: string;
};

const pathGridStyle: CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))',
};

const pathCardStyle: CSSProperties = {
  ...embeddedPanelStyle,
  background: 'var(--ant-color-fill-quaternary)',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const stepListStyle: CSSProperties = {
  display: 'grid',
  gap: 10,
};

const stepCardStyle: CSSProperties = {
  background: 'var(--ant-color-bg-container)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 10,
  display: 'flex',
  gap: 12,
  padding: '10px 12px',
};

const stepIndexStyle: CSSProperties = {
  alignItems: 'center',
  background: 'var(--ant-color-fill-secondary)',
  borderRadius: 999,
  display: 'inline-flex',
  height: 28,
  justifyContent: 'center',
  minWidth: 28,
};

const distinctionGridStyle: CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
};

const distinctionCardStyle: CSSProperties = {
  background: 'var(--ant-color-fill-quaternary)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 10,
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  minWidth: 0,
  padding: '12px 14px',
};

const flowPaths: FlowPath[] = [
  {
    id: 'draft-path',
    title: 'Draft path',
    description:
      'Use this while authoring. It runs the inline bundle directly from Studio instead of the published project binding.',
    tagColor: 'processing',
    steps: [
      {
        id: 'studio-draft',
        label: 'Studio draft',
        description:
          'New workflow and graph editing stay in Studio draft state until you decide to save or run.',
      },
      {
        id: 'save-asset',
        label: 'Save asset',
        description:
          'Save upserts the named workflow asset inside the project, but still does not change the default binding.',
      },
      {
        id: 'run-draft',
        label: 'Run draft',
        description:
          'Run draft calls /api/scopes/{scopeId}/draft-run with the inline workflow bundle and creates a new run actor.',
      },
      {
        id: 'runs',
        label: 'Runs',
        description:
          'Observe committed events, current-state projection, and pending human interaction from the same run session.',
      },
    ],
  },
  {
    id: 'published-path',
    title: 'Published project path',
    description:
      'Use this when the project should expose a stable entrypoint for chat or endpoint invocation.',
    tagColor: 'success',
    steps: [
      {
        id: 'save-asset',
        label: 'Save asset',
        description:
          'Save determines which named workflow assets exist inside the project and stay available for reuse.',
      },
      {
        id: 'bind-scope',
        label: 'Bind scope',
        description:
          'Bind scope updates the default project service so /invoke points at the published active revision.',
      },
      {
        id: 'invoke-services',
        label: 'Project Invoke',
        description:
          'Invoke reads the scope binding and service catalog, resolves the active serving revision, and starts a new run actor.',
      },
      {
        id: 'open-in-runs',
        label: 'Open in Runs',
        description:
          'The frontend hands off observed AGUI events, run IDs, and actor IDs so Runs can continue the same session.',
      },
    ],
  },
];

const distinctionCards: DistinctionCard[] = [
  {
    id: 'save-vs-bind',
    title: 'Save is not Bind scope',
    description:
      'Save updates named workflow assets. Bind scope updates the default project service that backs /invoke.',
  },
  {
    id: 'draft-vs-invoke',
    title: 'Run draft is not Invoke services',
    description:
      'Run draft uses the inline bundle at /draft-run. Invoke services uses a published and activated service revision.',
  },
  {
    id: 'definition-vs-run',
    title: 'Definition actor is not run actor',
    description:
      'The definition actor holds workflow facts. Every execution still creates a separate run actor.',
  },
  {
    id: 'handoff-vs-rerun',
    title: 'Open in Runs is not rerun',
    description:
      'Runs replays the observed events from the browser handoff and then hydrates actor snapshot. It does not silently start another execution.',
  },
];

function isHighlighted(
  id: AevatarAppFlowGuideStepId,
  highlightSteps: readonly AevatarAppFlowGuideStepId[],
): boolean {
  return highlightSteps.includes(id);
}

const AevatarAppFlowGuide: React.FC<AevatarAppFlowGuideProps> = ({
  compact = false,
  contextDescription,
  contextTitle,
  highlightSteps = [],
  tone = 'info',
}) => (
  <div style={cardStackStyle}>
    <Alert
      showIcon
      type={tone}
      title={contextTitle}
      description={contextDescription}
    />

    <div style={pathGridStyle}>
      {flowPaths.map((path) => (
        <div key={path.id} style={pathCardStyle}>
          <div>
            <Space wrap size={[8, 8]}>
              <Tag color={path.tagColor}>{path.title}</Tag>
            </Space>
            <Typography.Paragraph style={{ margin: '8px 0 0' }} type="secondary">
              {path.description}
            </Typography.Paragraph>
          </div>

          <div style={stepListStyle}>
            {path.steps.map((step, index) => {
              const highlighted = isHighlighted(step.id, highlightSteps);
              return (
                <div
                  key={`${path.id}-${step.id}`}
                  style={{
                    ...stepCardStyle,
                    borderColor: highlighted
                      ? 'var(--ant-color-primary)'
                      : 'var(--ant-color-border-secondary)',
                  }}
                >
                  <div style={stepIndexStyle}>
                    <Typography.Text strong>{index + 1}</Typography.Text>
                  </div>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>{step.label}</Typography.Text>
                      {highlighted ? <Tag color="processing">You are here</Tag> : null}
                    </Space>
                    <Typography.Paragraph
                      style={{ margin: '4px 0 0' }}
                      type="secondary"
                    >
                      {step.description}
                    </Typography.Paragraph>
                    {step.note ? (
                      <Typography.Text style={summaryFieldLabelStyle}>
                        {step.note}
                      </Typography.Text>
                    ) : null}
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      ))}
    </div>

    {!compact ? (
      <div style={embeddedPanelStyle}>
        <div style={cardStackStyle}>
          <div>
            <Typography.Text strong>Key distinctions for the console</Typography.Text>
            <Typography.Paragraph style={{ margin: '8px 0 0' }} type="secondary">
              These are the four distinctions users need in order to read the console
              correctly and choose the right page on purpose.
            </Typography.Paragraph>
          </div>

          <div style={distinctionGridStyle}>
            {distinctionCards.map((item) => (
              <div key={item.id} style={distinctionCardStyle}>
                <Typography.Text strong>{item.title}</Typography.Text>
                <Typography.Text type="secondary">{item.description}</Typography.Text>
              </div>
            ))}
          </div>
        </div>
      </div>
    ) : null}

    <Alert
      showIcon
      type="info"
      title="One projection pipeline"
      description="Draft runs and published invokes both end in committed run events, live SSE or AGUI updates, and durable current-state read models."
    />
  </div>
);

export default AevatarAppFlowGuide;
