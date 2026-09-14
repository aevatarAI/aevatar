import { useQuery } from '@tanstack/react-query';
import { channelsApi } from '@/shared/api/channelsApi';

export const channelKeys = {
  list: (scopeId: string) => ['channels', scopeId, 'registrations'] as const,
  status: (scopeId: string, registrationId: string) =>
    ['channels', scopeId, 'status', registrationId] as const,
};

export function useChannelRegistrations(scopeId: string, observeUntil = 0) {
  return useQuery({
    queryKey: channelKeys.list(scopeId),
    queryFn: ({ signal }) => channelsApi.list(signal),
    enabled: Boolean(scopeId),
    retry: false,
    staleTime: 0,
    refetchOnWindowFocus: true,
    refetchInterval: () => (Date.now() < observeUntil ? 2000 : false),
  });
}

export function useChannelStatus(scopeId: string, registrationId: string) {
  return useQuery({
    queryKey: channelKeys.status(scopeId, registrationId),
    queryFn: ({ signal }) => channelsApi.status(registrationId, signal),
    enabled: Boolean(scopeId && registrationId),
    retry: false,
    staleTime: 15_000,
    refetchOnWindowFocus: true,
    refetchInterval: 30_000,
  });
}
