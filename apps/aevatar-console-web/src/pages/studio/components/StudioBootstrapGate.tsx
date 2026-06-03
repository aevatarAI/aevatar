import React from 'react';
import { describeError } from '@/shared/ui/errorText';
import StudioStatusBanner from './StudioStatusBanner';
import { t } from "@/shared/i18n/messages";

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
    issues.push(t("pages.studio.studiobootstrapgate.team.context", "team context: {value1}", { value1: renderErrorMessage(appContextError) }));
  }

  if (workspaceError) {
    issues.push(t("pages.studio.studiobootstrapgate.workspace.settings", "Workspace settings: {value1}", { value1: renderErrorMessage(workspaceError) }));
  }

  if (authError) {
    issues.push(t("pages.studio.studiobootstrapgate.login.status", "Login status: {value1}", { value1: renderErrorMessage(authError) }));
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
            ? t("pages.studio.studiobootstrapgate.studio.currently.has.some", "Studio currently has some capabilities that are temporarily unavailable.")
            : appContextError
              ? t("pages.studio.studiobootstrapgate.team.context.is.temporarily", "team context is temporarily unavailable")
              : workspaceError
                ? t("pages.studio.studiobootstrapgate.workspace.settings.are.temporarily", "Workspace settings are temporarily unavailable")
                : t("pages.studio.studiobootstrapgate.login.status.to.be", "Login status to be confirmed"),
        description: issues.join(' · '),
      }
    : loading
      ? {
          type: 'info',
          title: t("pages.studio.studiobootstrapgate.preparing.studio", "Preparing Studio"),
          description: t("pages.studio.studiobootstrapgate.loading.team.context.login", "Loading team context, login status, and workspace settings."),
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
