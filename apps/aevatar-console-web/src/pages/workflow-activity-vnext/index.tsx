import React from 'react';
import { t } from '@/shared/i18n/messages';
import ActivityPage from './activity/ActivityPage';
import RunDetailPage from './activity/RunDetailPage';
import { useConsoleLocation } from './hooks/useConsoleLocation';
import SettingsPage from './settings/SettingsPage';
import WorkflowActivityVNextShell from './WorkflowActivityVNextShell';
import NewWorkflowPage from './workflows/NewWorkflowPage';
import WorkflowEditorPage from './workflows/WorkflowEditorPage';
import WorkflowsPage from './workflows/WorkflowsPage';
import WorkflowTemplatesPage from './workflows/WorkflowTemplatesPage';

const WorkflowActivityVNextPage: React.FC = () => {
  const location = useConsoleLocation();
  const pathname = location.pathname;
  const scopeMatch = /^\/scopes\/([^/]+)\/workflow-activity-vnext(?:\/|$)/.exec(
    pathname,
  );
  const scopeId = scopeMatch ? decodeURIComponent(scopeMatch[1]) : '';

  if (pathname.endsWith('/workflows/new/templates')) {
    return <WorkflowTemplatesPage scopeId={scopeId} />;
  }

  if (pathname.endsWith('/workflows/new')) {
    return <NewWorkflowPage scopeId={scopeId} />;
  }

  if (pathname.endsWith('/workflows')) {
    return <WorkflowsPage scopeId={scopeId} />;
  }

  const workflowMatch = /\/workflows\/([^/]+)$/.exec(pathname);
  if (workflowMatch) {
    return (
      <WorkflowEditorPage
        scopeId={scopeId}
        workflowId={decodeURIComponent(workflowMatch[1])}
      />
    );
  }

  const runMatch = /\/activity\/([^/]+)$/.exec(pathname);
  if (runMatch) {
    return (
      <RunDetailPage
        runId={decodeURIComponent(runMatch[1])}
        scopeId={scopeId}
      />
    );
  }

  if (pathname.endsWith('/activity')) {
    return <ActivityPage scopeId={scopeId} />;
  }

  if (pathname.endsWith('/settings')) {
    return <SettingsPage scopeId={scopeId} />;
  }

  return (
    <WorkflowActivityVNextShell
      activeSection={
        pathname.includes('/activity')
          ? 'activity'
          : pathname.includes('/settings')
            ? 'settings'
            : 'workflows'
      }
      description={t(
        'workflowActivityVNext.unavailable.description',
        'This vNext surface is not available yet.',
      )}
      scopeId={scopeId}
      title={t('workflowActivityVNext.unavailable.title', 'Unavailable')}
    >
      <div className="wa-vnext__state" role="status">
        <p>
          {t(
            'workflowActivityVNext.unavailable.body',
            'Check the address or return to another section.',
          )}
        </p>
      </div>
    </WorkflowActivityVNextShell>
  );
};

export default WorkflowActivityVNextPage;
