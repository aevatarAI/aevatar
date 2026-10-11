import React from 'react';
import ChannelConfigurationPage from '../channels/ChannelConfigurationPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function ChannelConfigurationRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page === 'channelBind') {
    return (
      <ChannelConfigurationPage
        key={route.routeKey}
        scopeId={route.scopeId}
        botId={route.botId}
        defaultSkillId={route.defaultSkillId}
        requestedServiceIds={route.requestedServiceIds}
      />
    );
  }
  if (route?.page === 'channelEdit') {
    return (
      <ChannelConfigurationPage
        key={route.routeKey}
        scopeId={route.scopeId}
        registrationId={route.registrationId}
        requestedServiceIds={route.requestedServiceIds}
      />
    );
  }
  return null;
}
