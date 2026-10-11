import React from 'react';
import RunDetailPage from '../activity/RunDetailPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function RunDetailRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'runDetail') return null;
  return <RunDetailPage scopeId={route.scopeId} runId={route.runId} />;
}
