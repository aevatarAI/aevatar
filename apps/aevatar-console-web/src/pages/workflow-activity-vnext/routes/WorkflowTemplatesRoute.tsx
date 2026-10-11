import React from 'react';
import WorkflowTemplatesPage from '../workflows/WorkflowTemplatesPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function WorkflowTemplatesRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'workflowTemplates') return null;
  return <WorkflowTemplatesPage scopeId={route.scopeId} />;
}
