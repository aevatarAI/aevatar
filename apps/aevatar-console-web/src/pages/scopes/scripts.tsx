import React, { useEffect } from 'react';
import { history } from '@/shared/navigation/history';
import {
  buildScopeHref,
  readScopeQueryDraft,
} from './components/scopeQuery';

function readScriptId(): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return new URLSearchParams(window.location.search).get('scriptId')?.trim() ?? '';
}

const ScopeScriptsPage: React.FC = () => {
  useEffect(() => {
    history.replace(
      buildScopeHref('/scopes/assets', readScopeQueryDraft(), {
        tab: 'scripts',
        scriptId: readScriptId(),
      }),
    );
  }, []);

  return null;
};

export default ScopeScriptsPage;
