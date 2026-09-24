import { useQuery } from '@tanstack/react-query';
import { Button, Result } from 'antd';
import * as React from 'react';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { resolveStudioScopeContext } from '@/shared/scope/context';
import { studioApi } from '@/shared/studio/api';
import { AevatarPageLoading } from '@/shared/ui/AevatarLoading';
import { WORKFLOW_ACTIVITY_ACCOUNT_QUERY_KEY } from './account/useWorkflowActivityAccount';
import { buildWorkflowActivitySectionHref } from './navigation';

export default function WorkflowHomePage() {
  const account = useQuery({
    queryKey: WORKFLOW_ACTIVITY_ACCOUNT_QUERY_KEY,
    queryFn: () => studioApi.getAuthSession(),
    refetchOnMount: 'always',
    staleTime: 0,
    retry: false,
  });
  const pending = account.isPending || account.isFetching;
  const needsSignIn = Boolean(
    account.data?.enabled &&
      (!account.data.authenticated ||
        account.data.session?.authenticated === false),
  );
  const scopeId = needsSignIn
    ? undefined
    : resolveStudioScopeContext(account.data)?.scopeId;
  const ready = !pending && account.isSuccess && Boolean(scopeId);

  React.useEffect(() => {
    if (ready && scopeId) {
      history.replace(buildWorkflowActivitySectionHref(scopeId, 'workflows'));
    }
  }, [ready, scopeId]);

  if (pending || ready) {
    return (
      <AevatarPageLoading
        fullscreen
        tip={t('workflowActivityVNext.home.loading', 'Opening your workflows…')}
      />
    );
  }

  return (
    <main>
      <Result
        status="warning"
        title={t(
          'workflowActivityVNext.home.unavailable',
          'Workflows unavailable',
        )}
        subTitle={
          account.isError
            ? t(
                'workflowActivityVNext.home.loadFailed',
                "We couldn't load your workspace. Try again.",
              )
            : needsSignIn
              ? t(
                  'workflowActivityVNext.home.signInRequired',
                  'Sign in to open your workflows.',
                )
              : t(
                  'workflowActivityVNext.home.scopeUnavailable',
                  'Your account has no available workspace. Check your access and try again.',
                )
        }
        extra={
          needsSignIn && !account.isError ? (
            <Button onClick={() => history.replace('/login')} type="primary">
              {t('workflowActivityVNext.home.signIn', 'Sign in')}
            </Button>
          ) : (
            <Button onClick={() => void account.refetch()} type="primary">
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          )
        }
      />
    </main>
  );
}
