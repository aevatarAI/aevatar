import React from 'react';
import ChannelsPage from '../channels/ChannelsPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function ChannelsRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'channels') return null;
  return <ChannelsPage scopeId={route.scopeId} />;
}
