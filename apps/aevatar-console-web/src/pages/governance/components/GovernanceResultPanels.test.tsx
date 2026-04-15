import { render, screen } from '@testing-library/react';
import React from 'react';
import {
  formatGovernanceTimestamp,
  GovernanceSelectionNotice,
  GovernanceSummaryPanel,
} from './GovernanceResultPanels';

describe('GovernanceResultPanels', () => {
  it('renders the selected governance scope and summary metrics', () => {
    render(
      <GovernanceSummaryPanel
        title="绑定目录"
        description="查看绑定目标和已挂接策略。"
        draft={{
          tenantId: 'tenant-a',
          appId: 'app-main',
          namespace: 'ops',
          serviceId: 'svc-payments',
          revisionId: '',
        }}
        extraFields={[
          {
            label: '目录更新时间',
            value: formatGovernanceTimestamp('2026-03-25T08:00:00Z'),
          },
        ]}
        metrics={[
          { label: '绑定', value: '8' },
          { label: '已挂策略', value: '6', tone: 'success' },
        ]}
        status={{ color: 'processing', label: '已加载' }}
      />,
    );

    expect(screen.getByText('绑定目录')).toBeInTheDocument();
    expect(screen.getByText('svc-payments')).toBeInTheDocument();
    expect(screen.getByText('tenant-a')).toBeInTheDocument();
    expect(screen.getByText('app-main')).toBeInTheDocument();
    expect(screen.getByText('2026-03-25 08:00:00 UTC')).toBeInTheDocument();
    expect(screen.getByText('绑定')).toBeInTheDocument();
    expect(screen.getByText('8')).toBeInTheDocument();
    expect(screen.getByText('已加载')).toBeInTheDocument();
  });

  it('renders a lightweight selection notice', () => {
    render(
      <GovernanceSelectionNotice title="选择服务" />,
    );

    expect(screen.getByText('选择服务')).toBeInTheDocument();
  });
});
