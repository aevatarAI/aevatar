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
    issues.push(`App context: ${renderErrorMessage(appContextError)}`);
  }

  if (workspaceError) {
    issues.push(`Workspace settings: ${renderErrorMessage(workspaceError)}`);
  }

  if (authError) {
    issues.push(`Authentication: ${renderErrorMessage(authError)}`);
  }

  const notice: StudioBootstrapNoticeProps | null = issues.length > 0
    ? {
        type: appContextError || workspaceError ? 'error' : 'warning',
        title:
          issues.length > 1
            ? 'Studio setup needs attention'
            : appContextError
              ? 'App context unavailable'
              : workspaceError
                ? 'Workspace settings unavailable'
                : 'Authentication needs attention',
        description: issues.join(' · '),
      }
    : loading
      ? {
          type: 'info',
          title: 'Preparing Studio',
          description: 'Loading session, app context, and workspace settings.',
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
