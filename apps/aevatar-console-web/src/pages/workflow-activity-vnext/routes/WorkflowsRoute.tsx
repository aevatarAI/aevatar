import React from 'react';
import WorkflowsPage from '../workflows/WorkflowsPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function WorkflowsRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'workflows') return null;
  return <WorkflowsPage scopeId={route.scopeId} />;
}
