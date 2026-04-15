import { Tag, Typography } from 'antd';
import React from 'react';
import { embeddedPanelStyle } from '@/shared/ui/proComponents';

type StudioStatusBannerType = 'info' | 'warning' | 'error' | 'success';

export type StudioStatusBannerProps = {
  readonly type: StudioStatusBannerType;
  readonly title: React.ReactNode;
  readonly description?: React.ReactNode;
  readonly action?: React.ReactNode;
};

const studioStatusBannerStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 12,
  justifyContent: 'space-between',
  minWidth: 0,
  padding: '10px 14px',
};

const studioStatusBannerMainStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: '1 1 320px',
  flexWrap: 'wrap',
  gap: 10,
  minWidth: 0,
};

const studioStatusBannerTextStyle: React.CSSProperties = {
  display: 'flex',
  flex: '1 1 240px',
  flexDirection: 'column',
  gap: 2,
  minWidth: 0,
};

const studioStatusBannerDescriptionStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: 1.5,
  margin: 0,
  wordBreak: 'break-word',
};

function getStudioStatusBannerAccent(
  type: StudioStatusBannerType,
): { background: string; borderColor: string; label: string } {
  switch (type) {
    case 'success':
      return {
        background: 'rgba(246, 255, 237, 0.96)',
        borderColor: 'rgba(82, 196, 26, 0.28)',
        label: '完成',
      };
    case 'warning':
      return {
        background: 'rgba(255, 251, 230, 0.96)',
        borderColor: 'rgba(250, 173, 20, 0.28)',
        label: '提醒',
      };
    case 'error':
      return {
        background: 'rgba(255, 241, 240, 0.96)',
        borderColor: 'rgba(255, 77, 79, 0.28)',
        label: '异常',
      };
    default:
      return {
        background: 'rgba(240, 245, 255, 0.96)',
        borderColor: 'rgba(22, 119, 255, 0.24)',
        label: '信息',
      };
  }
}

const StudioStatusBanner: React.FC<StudioStatusBannerProps> = ({
  type,
  title,
  description,
  action,
}) => {
  const accent = getStudioStatusBannerAccent(type);

  return (
    <div
      style={{
        ...studioStatusBannerStyle,
        background: accent.background,
        borderColor: accent.borderColor,
      }}
    >
      <div style={studioStatusBannerMainStyle}>
        <Tag color={type}>{accent.label}</Tag>
        <div style={studioStatusBannerTextStyle}>
          <Typography.Text strong>{title}</Typography.Text>
          {description ? (
            typeof description === 'string' ? (
              <Typography.Paragraph
                style={studioStatusBannerDescriptionStyle}
                title={description}
                type="secondary"
              >
                {description}
              </Typography.Paragraph>
            ) : (
              description
            )
          ) : null}
        </div>
      </div>
      {action ? <div>{action}</div> : null}
    </div>
  );
};

export default StudioStatusBanner;
