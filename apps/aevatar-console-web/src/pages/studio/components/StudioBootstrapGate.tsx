import React from 'react';
import { translate } from '@/shared/i18n/localization';
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
    issues.push(
      `${translate('studio.bootstrap.teamContext')}：${renderErrorMessage(appContextError)}`,
    );
  }

  if (workspaceError) {
    issues.push(
      `${translate('studio.bootstrap.workspaceSettings')}：${renderErrorMessage(workspaceError)}`,
    );
  }

  if (authError) {
    issues.push(
      `${translate('studio.bootstrap.loginState')}：${renderErrorMessage(authError)}`,
    );
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
            ? translate('studio.bootstrap.partialTitle')
            : appContextError
              ? translate('studio.bootstrap.teamContextUnavailable')
              : workspaceError
                ? translate('studio.bootstrap.workspaceUnavailable')
                : translate('studio.bootstrap.authPending'),
        description: issues.join(' · '),
      }
    : loading
      ? {
          type: 'info',
          title: translate('studio.bootstrap.loadingTitle'),
          description: translate('studio.bootstrap.loadingDescription'),
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
