import React from 'react';
import WorkflowEditorPage from '../workflows/WorkflowEditorPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function WorkflowEditorRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'workflowEditor') return null;
  return (
    <WorkflowEditorPage scopeId={route.scopeId} workflowId={route.workflowId} />
  );
}
