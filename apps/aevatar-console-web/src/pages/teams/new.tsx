import { BuildOutlined } from '@ant-design/icons';
import { Button, Space, Typography } from 'antd';
import React from 'react';
import { history } from '@/shared/navigation/history';
import { buildTeamsHref } from '@/shared/navigation/teamRoutes';
import { buildStudioRoute } from '@/shared/studio/navigation';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import ConsoleMetricCard from '@/shared/ui/ConsoleMetricCard';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';

const primaryActionButtonStyle: React.CSSProperties = {
  background: '#6c5ce7',
  borderColor: '#6c5ce7',
  borderRadius: 10,
  color: '#ffffff',
  fontSize: 14,
  fontWeight: 600,
  height: 44,
  paddingInline: 18,
};

const secondaryActionButtonStyle: React.CSSProperties = {
  borderRadius: 10,
  fontSize: 14,
  fontWeight: 500,
  height: 44,
  paddingInline: 18,
};

const stageChipStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#f6f0ff',
  borderRadius: 20,
  color: '#6c5ce7',
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 500,
  padding: '6px 12px',
};

const TeamCreatePage: React.FC = () => {
  const openBuilder = () =>
    history.push(
      buildStudioRoute({
        draftMode: 'new',
        tab: 'studio',
      }),
    );

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Teams"
      title="Create Team"
    >
      <div
        style={{
          display: 'grid',
          gap: 16,
          gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
          marginBottom: 20,
        }}
      >
        <ConsoleMetricCard label="入口" tone="purple" value="Studio" />
        <ConsoleMetricCard label="构建对象" value="行为 + 脚本" />
        <ConsoleMetricCard label="完成后" value="Team Details" />
        <ConsoleMetricCard label="新增后端流" tone="green" value="0" />
      </div>

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title="Team Builder"
      >
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 20,
          }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
              }}
            >
              {['行为定义', '脚本行为', 'Agent 角色', '集成'].map((item) => (
                <span key={item} style={stageChipStyle}>
                  {item}
                </span>
              ))}
            </div>
            <Space wrap size={[8, 8]}>
              <Button
                icon={<BuildOutlined />}
                onClick={openBuilder}
                style={primaryActionButtonStyle}
              >
                Open Team Builder
              </Button>
              <Button
                onClick={() => history.push(buildTeamsHref())}
                style={secondaryActionButtonStyle}
              >
                Back to My Teams
              </Button>
            </Space>
          </div>
          <Typography.Text
            style={{
              color: '#8c8c8c',
              fontSize: 12,
              fontWeight: 500,
            }}
          >
            Studio
          </Typography.Text>
        </div>
      </AevatarPanel>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
