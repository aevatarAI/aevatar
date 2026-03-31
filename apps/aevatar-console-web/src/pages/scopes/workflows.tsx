import React, { useEffect } from 'react';
import { history } from '@/shared/navigation/history';
import {
  buildScopeHref,
  readScopeQueryDraft,
} from './components/scopeQuery';

function readWorkflowId(): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return new URLSearchParams(window.location.search).get('workflowId')?.trim() ?? '';
}

const ScopeWorkflowsPage: React.FC = () => {
  useEffect(() => {
    history.replace(
      buildScopeHref('/scopes/assets', readScopeQueryDraft(), {
        tab: 'workflows',
        workflowId: readWorkflowId(),
      }),
    );
  }, []);

  return null;
};

export default ScopeWorkflowsPage;
