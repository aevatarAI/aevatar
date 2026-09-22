import React from 'react';
import { t } from '@/shared/i18n/messages';
import ActivityPage from './activity/ActivityPage';
import RunDetailPage from './activity/RunDetailPage';
import ChannelConfigurationPage from './channels/ChannelConfigurationPage';
import ChannelDetailsPage from './channels/ChannelDetailsPage';
import ChannelsPage from './channels/ChannelsPage';
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
  const scopeMatch =
    /^\/scopes\/([^/]+)\/(?:workflows|activity|channels|settings)(?:\/|$)/.exec(
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

  const channelBindMatch = /\/channels\/bind\/([^/]+)$/.exec(pathname);
  if (channelBindMatch) {
    const skill = new URLSearchParams(location.search).get('skill')?.trim();
    return (
      <ChannelConfigurationPage
        key={`${scopeId}:bind:${channelBindMatch[1]}`}
        scopeId={scopeId}
        botId={decodeURIComponent(channelBindMatch[1])}
        defaultSkillName={skill && skill.length <= 128 ? skill : undefined}
      />
    );
  }
  const channelEditMatch = /\/channels\/([^/]+)\/edit$/.exec(pathname);
  if (channelEditMatch) {
    return (
      <ChannelConfigurationPage
        key={`${scopeId}:${channelEditMatch[1]}`}
        scopeId={scopeId}
        registrationId={decodeURIComponent(channelEditMatch[1])}
      />
    );
  }
  const channelMatch = /\/channels\/([^/]+)$/.exec(pathname);
  if (channelMatch) {
    return (
      <ChannelDetailsPage
        key={`${scopeId}:${channelMatch[1]}`}
        scopeId={scopeId}
        registrationId={decodeURIComponent(channelMatch[1])}
      />
    );
  }
  if (pathname.endsWith('/channels')) {
    return <ChannelsPage scopeId={scopeId} />;
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
