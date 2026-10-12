import { useIntl } from '@umijs/max';
import { Empty, Tabs, Typography } from 'antd';
import React from 'react';
import { formatDateTime } from '@/shared/datetime/dateTime';
import type {
  WorkflowActivityRunDetail,
  WorkflowActivityStep,
  WorkflowActivityTimelineEvent,
} from '@/shared/models/workflowActivity';

interface RunDetailInspectorTabsProps {
  readonly run: WorkflowActivityRunDetail;
  readonly selectedStep: WorkflowActivityStep | null;
}

type InspectorTab = 'output' | 'input' | 'timeline';

interface InspectorPaneProps extends RunDetailInspectorTabsProps {
  readonly active: boolean;
  readonly tab: InspectorTab;
}

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function filterTimelineForStep(
  timeline: readonly WorkflowActivityTimelineEvent[],
  selectedStepId: string,
): readonly WorkflowActivityTimelineEvent[] {
  if (!selectedStepId) return timeline;
  const scoped = timeline.filter(
    (event) => trimOptional(event.stepId) === selectedStepId,
  );
  return scoped.length ? scoped : timeline;
}

function renderKeyValueRows(values: Readonly<Record<string, string>>) {
  const entries = Object.entries(values).filter(
    ([key, value]) => trimOptional(key) || trimOptional(value),
  );
  if (!entries.length) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
  }

  return (
    <div className="wa-vnext-run-detail__kv">
      {entries.map(([key, value]) => (
        <div className="wa-vnext-run-detail__kv-row" key={key}>
          <div className="wa-vnext-run-detail__kv-key">{key}</div>
          <div className="wa-vnext-run-detail__kv-value">{value || 'n/a'}</div>
        </div>
      ))}
    </div>
  );
}

function renderTextBlock(value: string) {
  const normalized = value.trim();
  if (!normalized) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
  }
  return <pre className="wa-vnext-run-detail__pre">{normalized}</pre>;
}

const InspectorPane = React.memo(
  function InspectorPane({ run, selectedStep, tab }: InspectorPaneProps) {
    if (tab === 'output') {
      return renderTextBlock(
        selectedStep
          ? selectedStep.error || selectedStep.outputPreview
          : run.finalError || run.finalOutput,
      );
    }

    if (tab === 'input') {
      return selectedStep
        ? renderKeyValueRows(selectedStep.requestParameters)
        : renderTextBlock(run.input);
    }

    const timeline = filterTimelineForStep(
      run.timeline,
      selectedStep?.stepId ?? '',
    );
    if (!timeline.length) {
      return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} />;
    }

    return (
      <div className="wa-vnext-run-detail__timeline">
        {timeline.map((event) => (
          <div
            className="wa-vnext-run-detail__timeline-row"
            key={`${event.timestampUtc}-${event.stage}-${event.kind}-${event.stepId}-${event.agentId}`}
          >
            <div className="wa-vnext-run-detail__timeline-key">
              {formatDateTime(event.timestampUtc)}
            </div>
            <div className="wa-vnext-run-detail__timeline-value">
              <Typography.Text strong>
                {event.stage || event.kind || 'event'}
              </Typography.Text>
              <br />
              <Typography.Text type="secondary">
                {trimOptional(event.message) ||
                  trimOptional(event.agentId) ||
                  'event'}
              </Typography.Text>
            </div>
          </div>
        ))}
      </div>
    );
  },
  // Keep visited hidden panes mounted without rebuilding their content. A
  // visibility change always reconciles the latest run and selected step.
  (previous, next) =>
    previous.active === next.active &&
    previous.tab === next.tab &&
    (!next.active ||
      (previous.run === next.run &&
        previous.selectedStep === next.selectedStep)),
);

const RunDetailInspectorTabs = React.memo(function RunDetailInspectorTabs({
  run,
  selectedStep,
}: RunDetailInspectorTabsProps) {
  const intl = useIntl();
  const [activeTab, setActiveTab] = React.useState('output');

  return (
    <Tabs
      activeKey={activeTab}
      onChange={setActiveTab}
      size="small"
      items={[
        {
          key: 'output',
          label: intl.formatMessage({
            id: 'workflowActivityVNext.run.output',
            defaultMessage: 'Output',
          }),
          children: (
            <InspectorPane
              active={activeTab === 'output'}
              run={run}
              selectedStep={selectedStep}
              tab="output"
            />
          ),
        },
        {
          key: 'input',
          label: intl.formatMessage({
            id: 'workflowActivityVNext.run.input',
            defaultMessage: 'Input',
          }),
          children: (
            <InspectorPane
              active={activeTab === 'input'}
              run={run}
              selectedStep={selectedStep}
              tab="input"
            />
          ),
        },
        {
          key: 'timeline',
          label: intl.formatMessage({
            id: 'workflowActivityVNext.run.timeline',
            defaultMessage: 'Timeline',
          }),
          children: (
            <InspectorPane
              active={activeTab === 'timeline'}
              run={run}
              selectedStep={selectedStep}
              tab="timeline"
            />
          ),
        },
      ]}
    />
  );
});

export default RunDetailInspectorTabs;
