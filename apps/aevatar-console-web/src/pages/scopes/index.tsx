import { useQuery } from '@tanstack/react-query';
import { Button, Result } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { resolveStudioScopeContext } from '@/shared/scope/context';
import { studioApi } from '@/shared/studio/api';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { buildWorkflowActivitySectionHref } from '../workflow-activity-vnext/navigation';

export default function ConsoleScopeEntry() {
  const session = useQuery({
    queryKey: ['scopes', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    staleTime: 0,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  });
  // A cached previous account must not determine the destination. Wait for
  // this entry's server confirmation; subject/local storage are not scope IDs.
  const scopeId =
    session.isSuccess && !session.isFetching && session.data.authenticated
      ? resolveStudioScopeContext(session.data)?.scopeId
      : undefined;

  React.useEffect(() => {
    if (scopeId) {
      history.replace(buildWorkflowActivitySectionHref(scopeId, 'workflows'));
    }
  }, [scopeId]);

  if (session.isPending || session.isFetching || scopeId) {
    return <AevatarPageLoading fullscreen />;
  }

  return (
    <Result
      status="error"
      title={t('console.home.unavailable', 'Could not open your workspace')}
      extra={
        <Button type="primary" onClick={() => void session.refetch()}>
          {t('console.home.retry', 'Try again')}
        </Button>
      }
    />
  );
}
