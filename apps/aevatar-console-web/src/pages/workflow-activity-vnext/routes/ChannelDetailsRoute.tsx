import React from 'react';
import ChannelDetailsPage from '../channels/ChannelDetailsPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function ChannelDetailsRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'channelDetails') return null;
  return (
    <ChannelDetailsPage
      key={route.routeKey}
      scopeId={route.scopeId}
      registrationId={route.registrationId}
    />
  );
}
