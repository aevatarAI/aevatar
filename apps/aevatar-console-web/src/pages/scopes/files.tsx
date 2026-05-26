import { useQuery } from '@tanstack/react-query';
import { Alert } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import {
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
} from '@/shared/studio/navigation';
import {
  AevatarPageShell,
  AevatarTitleWithHelp,
} from '@/shared/ui/aevatarPageShells';
import StudioFilesPage from '@/pages/studio/components/StudioFilesPage';
import { resolveStudioScopeContext } from './components/resolvedScope';
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from './components/scopeQuery';

const filesShellStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 16,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
};

const filesHeaderStyle: React.CSSProperties = {
  flexShrink: 0,
};

const filesPageBodyStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 16,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
};

const filesViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  minHeight: 0,
  overflow: 'hidden',
  width: '100%',
};

const ProjectFilesPage: React.FC = () => {
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => '',
  );
  const routeDraft = useMemo(() => {
    if (typeof window === 'undefined') {
      return readScopeQueryDraft('', '');
    }

    return readScopeQueryDraft(window.location.search, window.location.pathname);
  }, [locationSnapshot]);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(() =>
    readScopeQueryDraft(),
  );

  const authSessionQuery = useQuery({
    queryKey: ['scopes', 'files', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const appContextQuery = useQuery({
    queryKey: ['studio-app-context'],
    queryFn: () => studioApi.getAppContext(),
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    const nextRouteDraft = normalizeScopeDraft(routeDraft);

    setActiveDraft((currentDraft) =>
      normalizeScopeDraft(currentDraft).scopeId === nextRouteDraft.scopeId
        ? currentDraft
        : nextRouteDraft,
    );
  }, [routeDraft]);

  useEffect(() => {
    history.replace(buildScopeHref('/scopes/files', activeDraft));
  }, [activeDraft]);

  useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
  }, [resolvedScope?.scopeId]);

  const workspaceSettingsQuery = useQuery({
    queryKey: ['studio-workspace-settings'],
    queryFn: () => studioApi.getWorkspaceSettings(),
  });
  const workflowsQuery = useQuery({
    queryKey: ['studio-workspace-workflows'],
    queryFn: () => studioApi.listWorkflows(),
  });
  const connectorsQuery = useQuery({
    queryKey: ['studio-connectors'],
    queryFn: () => studioApi.getConnectorCatalog(),
  });
  const rolesQuery = useQuery({
    queryKey: ['studio-roles'],
    queryFn: () => studioApi.getRoleCatalog(),
  });
  const settingsQuery = useQuery({
    queryKey: ['studio-settings'],
    queryFn: () => studioApi.getSettings(),
  });

  return (
    <AevatarPageShell pageHeaderRender={false} title="Files">
      <div style={filesShellStyle}>
        <header style={filesHeaderStyle}>
          <AevatarTitleWithHelp
            title="Files"
            help="Browse workspace workflows, scope-backed scripts, and reusable Studio catalogs from one structured entry point."
          />
        </header>

        <div style={filesPageBodyStyle}>
          {!activeDraft.scopeId.trim() ? (
            <Alert
              type="info"
              showIcon
              message="Files can browse workspace workflows immediately. Script files will appear automatically once Studio resolves a project scope from the current session."
            />
          ) : null}

          {appContextQuery.isError ? (
            <Alert
              type="warning"
              showIcon
              message="Studio host context could not be resolved."
              description="Files will still show workspace-backed resources, but feature-specific affordances may be limited until Studio host context loads again."
            />
          ) : null}

          <div style={filesViewportStyle}>
            <StudioFilesPage
              workflows={workflowsQuery}
              workspaceSettings={workspaceSettingsQuery}
              roles={rolesQuery}
              connectors={connectorsQuery}
              settings={settingsQuery}
              scopeId={activeDraft.scopeId}
              workflowStorageMode={
                appContextQuery.data?.workflowStorageMode || 'workspace'
              }
              scriptsEnabled={Boolean(appContextQuery.data?.features.scripts)}
              onOpenWorkflowInStudio={(workflowId) =>
                history.push(buildStudioWorkflowEditorRoute({ workflowId }))
              }
              onOpenScriptInStudio={(scriptId) =>
                history.push(buildStudioScriptsWorkspaceRoute({ scriptId }))
              }
              showHeader={false}
            />
          </div>
        </div>
      </div>
    </AevatarPageShell>
  );
};

export default ProjectFilesPage;
