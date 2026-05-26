import React from 'react';
import { describeError } from '@/shared/ui/errorText';
import StudioStatusBanner from './StudioStatusBanner';

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

const studioBootstrapBannerWrapStyle: React.CSSProperties = {
  marginBottom: 16,
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
  const loading = appContextLoading || authLoading || workspaceLoading;
  const issues: string[] = [];

  if (appContextError) {
    issues.push(`团队上下文：${renderErrorMessage(appContextError)}`);
  }

  if (workspaceError) {
    issues.push(`工作区设置：${renderErrorMessage(workspaceError)}`);
  }

  if (authError) {
    issues.push(`登录状态：${renderErrorMessage(authError)}`);
  }

  const authOnlyIssue =
    Boolean(authError) &&
    !appContextError &&
    !workspaceError &&
    !appContextLoading &&
    !workspaceLoading;

  const notice: StudioBootstrapNoticeProps | null = issues.length > 0
    ? authOnlyIssue
      ? null
      : {
        type: appContextError || workspaceError ? 'error' : 'warning',
        title:
          issues.length > 1
            ? 'Studio 当前有部分能力暂时不可用'
            : appContextError
              ? '团队上下文暂时不可用'
              : workspaceError
                ? '工作区设置暂时不可用'
                : '登录状态待确认',
        description: issues.join(' · '),
      }
    : loading
      ? {
          type: 'info',
          title: '正在准备 Studio',
          description: '正在加载团队上下文、登录状态和工作区设置。',
        }
      : null;

  return (
    <>
      {notice ? (
        <div style={studioBootstrapBannerWrapStyle}>
          <StudioStatusBanner {...notice} />
        </div>
      ) : null}
      {children}
    </>
  );
};

export default StudioBootstrapGate;
