import type { AlertProps } from 'antd';
import { Alert, Space, Tag, Typography } from 'antd';
import React from 'react';
import type { CSSProperties } from 'react';
import {
  cardStackStyle,
  embeddedPanelStyle,
  summaryFieldLabelStyle,
} from './proComponents';
import {
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from "@/shared/i18n/messages";

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
  label: ConsoleMessageDescriptor;
  description: ConsoleMessageDescriptor;
  note?: ConsoleMessageDescriptor;
};

type FlowPath = {
  id: string;
  title: ConsoleMessageDescriptor;
  description: ConsoleMessageDescriptor;
  tagColor: string;
  steps: FlowPathStep[];
};

type DistinctionCard = {
  id: string;
  title: ConsoleMessageDescriptor;
  description: ConsoleMessageDescriptor;
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
    title: {
      id: 'shared.ui.aevatarappflowguide.draft.path',
      defaultMessage: 'Draft path',
    },
    description: {
      id: 'shared.ui.aevatarappflowguide.use.this.while.authoring.it',
      defaultMessage:
        'Use this while authoring. It runs the inline bundle directly from Studio instead of the published project binding.',
    },
    tagColor: 'processing',
    steps: [
      {
        id: 'studio-draft',
        label: {
          id: 'shared.ui.aevatarappflowguide.studio.draft',
          defaultMessage: 'Studio draft',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.new.workflow.and.graph.editing',
          defaultMessage:
            'New workflow and graph editing stay in Studio draft state until you decide to save or run.',
        },
      },
      {
        id: 'save-asset',
        label: {
          id: 'shared.ui.aevatarappflowguide.save.asset',
          defaultMessage: 'Save asset',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.save.upserts.the.named.workflow',
          defaultMessage:
            'Save upserts the named workflow asset inside the project, but still does not change the default binding.',
        },
      },
      {
        id: 'run-draft',
        label: {
          id: 'shared.ui.aevatarappflowguide.run.draft',
          defaultMessage: 'Run draft',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.run.draft.calls.api.scopes',
          defaultMessage:
            'Run draft calls /api/scopes/{scopeId}/draft-run with the inline workflow bundle and creates a new run actor.',
        },
      },
      {
        id: 'runs',
        label: {
          id: 'shared.ui.aevatarappflowguide.runs',
          defaultMessage: 'Runs',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.observe.committed.events.current.state',
          defaultMessage:
            'Observe committed events, current-state projection, and pending human interaction from the same run session.',
        },
      },
    ],
  },
  {
    id: 'published-path',
    title: {
      id: 'shared.ui.aevatarappflowguide.published.project.path',
      defaultMessage: 'Published project path',
    },
    description: {
      id: 'shared.ui.aevatarappflowguide.use.this.when.the.project',
      defaultMessage:
        'Use this when the project should expose a stable entrypoint for chat or endpoint invocation.',
    },
    tagColor: 'success',
    steps: [
      {
        id: 'save-asset',
        label: {
          id: 'shared.ui.aevatarappflowguide.save.asset.2',
          defaultMessage: 'Save asset',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.save.determines.which.named.workflow',
          defaultMessage:
            'Save determines which named workflow assets exist inside the project and stay available for reuse.',
        },
      },
      {
        id: 'bind-scope',
        label: {
          id: 'shared.ui.aevatarappflowguide.update.default.route',
          defaultMessage: 'Update default route',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.updating.the.default.route.points',
          defaultMessage:
            'Updating the default route points /invoke at the published active revision without changing member-owned bind facts.',
        },
      },
      {
        id: 'invoke-services',
        label: {
          id: 'shared.ui.aevatarappflowguide.project.invoke',
          defaultMessage: 'Project Invoke',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.invoke.reads.the.default.route',
          defaultMessage:
            'Invoke reads the default route and service catalog, resolves the active serving revision, and starts a new run actor.',
        },
      },
      {
        id: 'open-in-runs',
        label: {
          id: 'shared.ui.aevatarappflowguide.open.in.runs',
          defaultMessage: 'Open in Runs',
        },
        description: {
          id: 'shared.ui.aevatarappflowguide.the.frontend.hands.off.observed',
          defaultMessage:
            'The frontend hands off observed AGUI events, run IDs, and actor IDs so Runs can continue the same session.',
        },
      },
    ],
  },
];

const distinctionCards: DistinctionCard[] = [
  {
    id: 'save-vs-bind',
    title: {
      id: 'shared.ui.aevatarappflowguide.save.is.not.update.default',
      defaultMessage: 'Save is not Update default route',
    },
    description: {
      id: 'shared.ui.aevatarappflowguide.save.updates.named.workflow.assets',
      defaultMessage:
        'Save updates named workflow assets. Updating the default route switches the project service that backs /invoke.',
    },
  },
  {
    id: 'draft-vs-invoke',
    title: {
      id: 'shared.ui.aevatarappflowguide.run.draft.is.not.invoke',
      defaultMessage: 'Run draft is not Invoke services',
    },
    description: {
      id: 'shared.ui.aevatarappflowguide.run.draft.uses.the.inline',
      defaultMessage:
        'Run draft uses the inline bundle at /draft-run. Invoke services uses a published and activated service revision.',
    },
  },
  {
    id: 'definition-vs-run',
    title: {
      id: 'shared.ui.aevatarappflowguide.definition.actor.is.not.run',
      defaultMessage: 'Definition actor is not run actor',
    },
    description: {
      id: 'shared.ui.aevatarappflowguide.the.definition.actor.holds.workflow',
      defaultMessage:
        'The definition actor holds workflow facts. Every execution still creates a separate run actor.',
    },
  },
  {
    id: 'handoff-vs-rerun',
    title: {
      id: 'shared.ui.aevatarappflowguide.open.in.runs.is.not',
      defaultMessage: 'Open in Runs is not rerun',
    },
    description: {
      id: 'shared.ui.aevatarappflowguide.runs.replays.the.observed.events',
      defaultMessage:
        'Runs replays the observed events from the browser handoff and then hydrates actor snapshot. It does not silently start another execution.',
    },
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
              <Tag color={path.tagColor}>{formatConsoleMessage(path.title)}</Tag>
            </Space>
            <Typography.Paragraph style={{ margin: '8px 0 0' }} type="secondary">
              {formatConsoleMessage(path.description)}
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
                      <Typography.Text strong>{formatConsoleMessage(step.label)}</Typography.Text>
                      {highlighted ? <Tag color="processing">{t("shared.ui.aevatarappflowguide.you.are.here", "You are here")}</Tag> : null}
                    </Space>
                    <Typography.Paragraph
                      style={{ margin: '4px 0 0' }}
                      type="secondary"
                    >
                      {formatConsoleMessage(step.description)}
                    </Typography.Paragraph>
                    {step.note ? (
                      <Typography.Text style={summaryFieldLabelStyle}>
                        {formatConsoleMessage(step.note)}
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
            <Typography.Text strong>{t("shared.ui.aevatarappflowguide.key.distinctions.for.the.console", "Key distinctions for the console")}</Typography.Text>
            <Typography.Paragraph style={{ margin: '8px 0 0' }} type="secondary">
              {t("shared.ui.aevatarappflowguide.these.are.the.four.distinctions", "These are the four distinctions users need in order to read the console correctly and choose the right page on purpose.")}</Typography.Paragraph>
          </div>

          <div style={distinctionGridStyle}>
            {distinctionCards.map((item) => (
              <div key={item.id} style={distinctionCardStyle}>
                <Typography.Text strong>{formatConsoleMessage(item.title)}</Typography.Text>
                <Typography.Text type="secondary">{formatConsoleMessage(item.description)}</Typography.Text>
              </div>
            ))}
          </div>
        </div>
      </div>
    ) : null}

    <Alert
      showIcon
      type="info"
      title={t("shared.ui.aevatarappflowguide.one.projection.pipeline", "One projection pipeline")}
      description={t("shared.ui.aevatarappflowguide.draft.runs.and.published.invokes", "Draft runs and published invokes both end in committed run events, live SSE or AGUI updates, and durable current-state read models.")}
    />
  </div>
);

export default AevatarAppFlowGuide;
