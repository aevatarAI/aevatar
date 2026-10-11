import React from 'react';
import ActivityPage from '../activity/ActivityPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function ActivityRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'activity') return null;
  return <ActivityPage scopeId={route.scopeId} />;
}
