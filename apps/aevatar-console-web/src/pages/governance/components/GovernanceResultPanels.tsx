import { Tag, Typography } from 'antd';
import React from 'react';
import {
  cardStackStyle,
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import type { GovernanceDraft } from './governanceQuery';

type GovernanceStatusTag = {
  color: 'default' | 'processing' | 'success' | 'warning' | 'error';
  label: string;
};

type GovernanceSummaryField = {
  label: string;
  value: React.ReactNode;
};

export type GovernanceSummaryMetric = {
  label: string;
  value: React.ReactNode;
  tone?: 'default' | 'success' | 'warning' | 'danger';
};

type GovernanceSummaryPanelProps = {
  title: string;
  description?: string;
  draft: GovernanceDraft;
  revisionId?: string;
  extraFields?: GovernanceSummaryField[];
  metrics?: GovernanceSummaryMetric[];
  status?: GovernanceStatusTag | null;
};

type GovernanceSelectionNoticeProps = {
  title: string;
  description?: string;
};

const governanceSurfaceStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background: 'var(--ant-color-fill-quaternary)',
};

const governanceMetricToneMap: Record<
  NonNullable<GovernanceSummaryMetric['tone']>,
  React.CSSProperties
> = {
  default: {
    color: 'var(--ant-color-text)',
  },
  success: {
    color: 'var(--ant-color-success)',
  },
  warning: {
    color: 'var(--ant-color-warning)',
  },
  danger: {
    color: 'var(--ant-color-error)',
  },
};

function renderFieldValue(value: React.ReactNode): React.ReactNode {
  if (typeof value === 'string') {
    return value.trim() || '暂无';
  }

  if (value === null || value === undefined || value === false) {
    return '暂无';
  }

  return value;
}

function GovernanceMetric({
  label,
  value,
  tone = 'default',
}: GovernanceSummaryMetric): React.ReactElement {
  return (
    <div style={summaryMetricStyle}>
      <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
      <Typography.Text
        style={{
          ...summaryMetricValueStyle,
          ...governanceMetricToneMap[tone],
        }}
      >
        {value}
      </Typography.Text>
    </div>
  );
}

export function formatGovernanceTimestamp(value: string | undefined): string {
  const normalized = value?.trim() ?? '';
  if (!normalized) {
    return '待更新';
  }

  return normalized.replace('T', ' ').replace('Z', ' UTC');
}

export const GovernanceSelectionNotice: React.FC<
  GovernanceSelectionNoticeProps
> = ({ title, description }) => (
  <div style={governanceSurfaceStyle}>
    <div style={cardStackStyle}>
      <Typography.Text strong>{title}</Typography.Text>
      {description ? (
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          {description}
        </Typography.Paragraph>
      ) : null}
    </div>
  </div>
);

export const GovernanceSummaryPanel: React.FC<GovernanceSummaryPanelProps> = ({
  title,
  description,
  draft,
  revisionId,
  extraFields = [],
  metrics = [],
  status = null,
}) => {
  const fields: GovernanceSummaryField[] = [
    { label: '服务', value: draft.serviceId },
    { label: '团队', value: draft.tenantId },
    { label: '应用', value: draft.appId },
    { label: '命名空间', value: draft.namespace },
    revisionId ? { label: '版本', value: revisionId } : null,
    ...extraFields,
  ].filter(Boolean) as GovernanceSummaryField[];

  return (
    <div style={governanceSurfaceStyle}>
      <div style={cardStackStyle}>
        <div
          style={{
            alignItems: 'flex-start',
            display: 'flex',
            flexWrap: 'wrap',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <div style={cardStackStyle}>
            <Typography.Text strong>{title}</Typography.Text>
            {description ? (
              <Typography.Paragraph style={{ margin: 0 }} type="secondary">
                {description}
              </Typography.Paragraph>
            ) : null}
          </div>
          {status ? <Tag color={status.color}>{status.label}</Tag> : null}
        </div>

        {fields.length > 0 ? (
          <div style={summaryFieldGridStyle}>
            {fields.map((field) => (
              <div key={field.label} style={summaryFieldStyle}>
                <Typography.Text style={summaryFieldLabelStyle}>
                  {field.label}
                </Typography.Text>
                <Typography.Text strong>
                  {renderFieldValue(field.value)}
                </Typography.Text>
              </div>
            ))}
          </div>
        ) : null}

        {metrics.length > 0 ? (
          <div style={summaryMetricGridStyle}>
            {metrics.map((metric) => (
              <GovernanceMetric key={metric.label} {...metric} />
            ))}
          </div>
        ) : null}
      </div>
    </div>
  );
};
