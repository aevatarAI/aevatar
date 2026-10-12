import React from 'react';
import SettingsPage from '../settings/SettingsPage';
import { useWorkflowActivityRoute } from './useWorkflowActivityRoute';

export default function SettingsRoute() {
  const route = useWorkflowActivityRoute();
  if (route?.page !== 'settings') return null;
  return <SettingsPage scopeId={route.scopeId} />;
}
