import { Tag, Typography } from 'antd';
import React from 'react';
import { describeError } from '@/shared/ui/errorText';
import { embeddedPanelStyle } from '@/shared/ui/proComponents';

type StudioBootstrapGateProps = {
  readonly appContextLoading: boolean;
  readonly appContextError: unknown;
  readonly authLoading: boolean;
  readonly authError: unknown;
  readonly workspaceLoading: boolean;
  readonly workspaceError: unknown;
  readonly children: React.ReactNode;
};

type StudioBootstrapNoticeProps = {
  readonly type: 'info' | 'warning' | 'error';
  readonly title: string;
  readonly description: string;
};

const studioBootstrapNoticeStripStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
  marginBottom: 16,
};

const studioBootstrapNoticeCardStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const studioBootstrapNoticeDescriptionStyle: React.CSSProperties = {
  margin: 0,
  display: '-webkit-box',
  overflow: 'hidden',
  wordBreak: 'break-word',
  WebkitBoxOrient: 'vertical',
  WebkitLineClamp: 2,
};

function getStudioBootstrapNoticeAccent(
  type: StudioBootstrapNoticeProps['type'],
): { background: string; borderColor: string; label: string } {
  switch (type) {
    case 'error':
      return {
        background: 'rgba(255, 241, 240, 0.96)',
        borderColor: 'rgba(255, 77, 79, 0.28)',
        label: 'Error',
      };
    case 'warning':
      return {
        background: 'rgba(255, 251, 230, 0.96)',
        borderColor: 'rgba(250, 173, 20, 0.28)',
        label: 'Warning',
      };
    default:
      return {
        background: 'rgba(240, 245, 255, 0.96)',
        borderColor: 'rgba(22, 119, 255, 0.24)',
        label: 'Info',
      };
  }
}

const StudioBootstrapNotice: React.FC<StudioBootstrapNoticeProps> = ({
  type,
  title,
  description,
}) => {
  const accent = getStudioBootstrapNoticeAccent(type);

  return (
    <div
      style={{
        ...studioBootstrapNoticeCardStyle,
        background: accent.background,
        borderColor: accent.borderColor,
      }}
    >
      <Tag color={type}>{accent.label}</Tag>
      <Typography.Text strong>{title}</Typography.Text>
      <Typography.Paragraph
        style={studioBootstrapNoticeDescriptionStyle}
        title={description}
        type="secondary"
      >
        {description}
      </Typography.Paragraph>
    </div>
  );
};

function renderErrorMessage(error: unknown): string {
  return describeError(error);
}

const StudioBootstrapGate: React.FC<StudioBootstrapGateProps> = ({
  appContextLoading,
  appContextError,
  authLoading,
  authError,
  workspaceLoading,
  workspaceError,
  children,
}) => {
  const notices: StudioBootstrapNoticeProps[] = [];

  if (appContextLoading || authLoading || workspaceLoading) {
    notices.push({
      type: 'info',
      title: 'Bootstrapping Studio host context',
      description:
        'Studio is loading the host session, app context, and workspace settings before the workbench fully hydrates.',
    });
  }

  if (appContextError) {
    notices.push({
      type: 'error',
      title: 'Failed to load Studio app context',
      description: renderErrorMessage(appContextError),
    });
  }

  if (workspaceError) {
    notices.push({
      type: 'error',
      title: 'Failed to load Studio workspace settings',
      description: renderErrorMessage(workspaceError),
    });
  }

  if (authError) {
    notices.push({
      type: 'warning',
      title: 'Studio authentication bootstrap returned an error',
      description: renderErrorMessage(authError),
    });
  }

  return (
    <>
      {notices.length > 0 ? (
        <div style={studioBootstrapNoticeStripStyle}>
          {notices.map((notice) => (
            <StudioBootstrapNotice
              key={`${notice.type}:${notice.title}`}
              {...notice}
            />
          ))}
        </div>
      ) : null}
      {children}
    </>
  );
};

export default StudioBootstrapGate;
