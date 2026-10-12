import React from 'react';
import NewWorkflowPage from '../workflows/NewWorkflowPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function NewWorkflowRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'newWorkflow') return null;
  return <NewWorkflowPage scopeId={route.scopeId} />;
}
