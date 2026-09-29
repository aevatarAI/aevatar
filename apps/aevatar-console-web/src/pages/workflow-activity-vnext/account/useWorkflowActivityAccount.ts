import { useQuery } from '@tanstack/react-query';
import { loadRestorableAuthSession } from '@/shared/auth/session';
import { studioApi } from '@/shared/studio/api';
import { resolveWorkflowActivityAccount } from './resolveWorkflowActivityAccount';

export const WORKFLOW_ACTIVITY_ACCOUNT_QUERY_KEY = [
  'workflow-activity-vnext',
  'account',
] as const;

export function useWorkflowActivityAccount() {
  const query = useQuery({
    queryKey: WORKFLOW_ACTIVITY_ACCOUNT_QUERY_KEY,
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolved = resolveWorkflowActivityAccount(
    query.data,
    loadRestorableAuthSession(),
  );

  return { query, ...resolved };
}
