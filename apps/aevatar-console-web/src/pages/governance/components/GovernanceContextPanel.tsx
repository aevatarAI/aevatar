import { Button, Space, Typography } from 'antd';
import React from 'react';
import {
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
} from '@/shared/ui/proComponents';
import type { GovernanceDraft } from './governanceQuery';

type GovernanceContextPanelProps = {
  draft: GovernanceDraft;
  includeRevision?: boolean;
  onChangeService: () => void;
};

function renderValue(value: string): string {
  const normalized = value.trim();
  return normalized || 'n/a';
}

const GovernanceContextPanel: React.FC<GovernanceContextPanelProps> = ({
  draft,
  includeRevision = false,
  onChangeService,
}) => (
  <div
    style={{
      ...embeddedPanelStyle,
      background: 'var(--ant-color-fill-quaternary)',
    }}
  >
    <Space orientation="vertical" size={12} style={{ width: '100%' }}>
      <div>
        <Typography.Text strong>Current service context</Typography.Text>
        <Typography.Paragraph style={{ margin: '8px 0 0' }} type="secondary">
          Stay on the current service and move across Governance tabs. Change
          service only when you need to jump to another tenant/app/namespace
          context.
        </Typography.Paragraph>
      </div>

      <div style={summaryFieldGridStyle}>
        <div style={summaryFieldStyle}>
          <Typography.Text style={summaryFieldLabelStyle}>Service</Typography.Text>
          <Typography.Text strong>{renderValue(draft.serviceId)}</Typography.Text>
        </div>
        <div style={summaryFieldStyle}>
          <Typography.Text style={summaryFieldLabelStyle}>Tenant</Typography.Text>
          <Typography.Text strong>{renderValue(draft.tenantId)}</Typography.Text>
        </div>
        <div style={summaryFieldStyle}>
          <Typography.Text style={summaryFieldLabelStyle}>App</Typography.Text>
          <Typography.Text strong>{renderValue(draft.appId)}</Typography.Text>
        </div>
        <div style={summaryFieldStyle}>
          <Typography.Text style={summaryFieldLabelStyle}>Namespace</Typography.Text>
          <Typography.Text strong>{renderValue(draft.namespace)}</Typography.Text>
        </div>
        {includeRevision ? (
          <div style={summaryFieldStyle}>
            <Typography.Text style={summaryFieldLabelStyle}>Revision</Typography.Text>
            <Typography.Text strong>{renderValue(draft.revisionId)}</Typography.Text>
          </div>
        ) : null}
      </div>

      <div>
        <Button onClick={onChangeService}>Change service</Button>
      </div>
    </Space>
  </div>
);

export default GovernanceContextPanel;
