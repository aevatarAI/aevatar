import { Tag, Typography } from 'antd';
import React from 'react';
import {
  cardStackStyle,
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
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
  includeDefaultFields?: boolean;
  extraFields?: GovernanceSummaryField[];
  metrics?: GovernanceSummaryMetric[];
  status?: GovernanceStatusTag | null;
};

type GovernanceSelectionNoticeProps = {
  title: string;
  description?: string;
  highlights?: Array<{ label: string; value: React.ReactNode }>;
};

const governanceSurfaceStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background:
    'linear-gradient(180deg, rgba(255,255,255,0.98) 0%, rgba(249,250,251,0.94) 100%)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 16,
  boxShadow: '0 10px 24px rgba(15, 23, 42, 0.04)',
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

const governanceHeaderStackStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
  minWidth: 0,
};

const governanceFieldCardStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  background: 'rgba(248, 250, 252, 0.95)',
  border: '1px solid rgba(15, 23, 42, 0.08)',
  borderRadius: 14,
  minWidth: 0,
  padding: '14px 16px',
};

const governanceMetricCardStyle: React.CSSProperties = {
  ...summaryMetricStyle,
  background: 'rgba(255, 255, 255, 0.92)',
  border: '1px solid rgba(15, 23, 42, 0.08)',
  borderRadius: 14,
  gap: 8,
  minHeight: 88,
  padding: '14px 16px',
};

const governanceSelectionHighlightStyle: React.CSSProperties = {
  ...governanceFieldCardStyle,
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
    <div style={governanceMetricCardStyle}>
      <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
      <Typography.Text
        style={{
          ...summaryMetricValueStyle,
          ...governanceMetricToneMap[tone],
          fontSize: 26,
          fontWeight: 700,
          lineHeight: 1,
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
> = ({ title, description, highlights = [] }) => (
  <div style={governanceSurfaceStyle}>
    <div style={cardStackStyle}>
      <div style={governanceHeaderStackStyle}>
        <Typography.Text
          strong
          style={{ color: 'var(--ant-color-text-heading)', fontSize: 16 }}
        >
          {title}
        </Typography.Text>
        {description ? (
          <span
            style={{
              color: 'var(--ant-color-text-secondary)',
              fontSize: 14,
              lineHeight: 1.6,
            }}
          >
            {description}
          </span>
        ) : null}
      </div>
      {highlights.length > 0 ? (
        <div
          style={{
            display: 'grid',
            gap: 10,
            gridTemplateColumns: 'repeat(auto-fit, minmax(148px, 1fr))',
          }}
        >
          {highlights.map((item) => (
            <div key={item.label} style={governanceSelectionHighlightStyle}>
              <Typography.Text style={summaryFieldLabelStyle}>
                {item.label}
              </Typography.Text>
              <Typography.Text
                strong
                style={{ fontSize: 15, lineHeight: 1.5, overflowWrap: 'anywhere' }}
              >
                {renderFieldValue(item.value)}
              </Typography.Text>
            </div>
          ))}
        </div>
      ) : null}
    </div>
  </div>
);

export const GovernanceSummaryPanel: React.FC<GovernanceSummaryPanelProps> = ({
  title,
  description,
  draft,
  revisionId,
  includeDefaultFields = true,
  extraFields = [],
  metrics = [],
  status = null,
}) => {
  const fields: GovernanceSummaryField[] = [
    ...(includeDefaultFields
      ? [
          { label: '服务', value: draft.serviceId },
          { label: '团队', value: draft.tenantId },
          { label: '应用', value: draft.appId },
          { label: '命名空间', value: draft.namespace },
          revisionId ? { label: '版本', value: revisionId } : null,
        ]
      : []),
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
          <div style={governanceHeaderStackStyle}>
            <span
              style={{
                color: 'var(--ant-color-primary)',
                fontSize: 12,
                fontWeight: 700,
                letterSpacing: '0.08em',
                textTransform: 'uppercase',
              }}
            >
              治理摘要
            </span>
            <Typography.Text
              strong
              style={{ color: 'var(--ant-color-text-heading)', fontSize: 20 }}
            >
              {title}
            </Typography.Text>
            {description ? (
              <Typography.Paragraph
                style={{
                  color: 'var(--ant-color-text-secondary)',
                  fontSize: 14,
                  lineHeight: 1.6,
                  margin: 0,
                }}
              >
                {description}
              </Typography.Paragraph>
            ) : null}
          </div>
          {status ? (
            <Tag
              color={status.color}
              style={{
                borderRadius: 999,
                fontSize: 12,
                fontWeight: 600,
                marginInlineEnd: 0,
                paddingInline: 10,
              }}
            >
              {status.label}
            </Tag>
          ) : null}
        </div>

        {fields.length > 0 ? (
          <div style={summaryFieldGridStyle}>
            {fields.map((field) => (
              <div key={field.label} style={governanceFieldCardStyle}>
                <Typography.Text style={summaryFieldLabelStyle}>
                  {field.label}
                </Typography.Text>
                <Typography.Text
                  strong
                  style={{ fontSize: 15, lineHeight: 1.5, overflowWrap: 'anywhere' }}
                >
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
