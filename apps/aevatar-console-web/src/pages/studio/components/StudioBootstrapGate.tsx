import {
  Alert,
} from 'antd';
import React from 'react';

type StudioBootstrapGateProps = {
  readonly appContextLoading: boolean;
  readonly appContextError: unknown;
  readonly authLoading: boolean;
  readonly authError: unknown;
  readonly workspaceLoading: boolean;
  readonly workspaceError: unknown;
  readonly children: React.ReactNode;
};

function renderErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

const StudioBootstrapGate: React.FC<StudioBootstrapGateProps> = ({
  appContextLoading,
  appContextError,
  authLoading,
  authError,
  workspaceLoading,
  workspaceError,
  children,
}) => (
  <>
    {appContextLoading || authLoading || workspaceLoading ? (
      <Alert
        showIcon
        type="info"
        title="Bootstrapping Studio host context"
        description="Studio is loading the host session, app context, and workspace settings before the workbench fully hydrates."
      />
    ) : null}

    {appContextError ? (
      <Alert
        showIcon
        type="error"
        title="Failed to load Studio app context"
        description={renderErrorMessage(appContextError)}
      />
    ) : null}

    {workspaceError ? (
      <Alert
        showIcon
        type="error"
        title="Failed to load Studio workspace settings"
        description={renderErrorMessage(workspaceError)}
      />
    ) : null}

    {authError ? (
      <Alert
        showIcon
        type="warning"
        title="Studio authentication bootstrap returned an error"
        description={renderErrorMessage(authError)}
      />
    ) : null}

    {children}
  </>
);

export default StudioBootstrapGate;
