import { useQuery } from '@tanstack/react-query';
import { listChannelBotIdentities } from '@/shared/api/channelBotsApi';
import { channelsApi } from '@/shared/api/channelsApi';

export const channelKeys = {
  list: (scopeId: string) => ['channels', scopeId, 'registrations'] as const,
  bots: (scopeId: string) => ['channels', scopeId, 'bot-identities'] as const,
  status: (scopeId: string, registrationId: string) =>
    ['channels', scopeId, 'status', registrationId] as const,
};

export function useChannelBotIdentities(scopeId: string, enabled: boolean) {
  return useQuery({
    queryKey: channelKeys.bots(scopeId),
    queryFn: ({ signal }) => listChannelBotIdentities(signal),
    enabled: Boolean(scopeId) && enabled,
    retry: false,
    staleTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false,
  });
}

export function useChannelRegistrations(scopeId: string, enabled = true) {
  return useQuery({
    queryKey: channelKeys.list(scopeId),
    queryFn: ({ signal }) => channelsApi.list(signal),
    enabled: Boolean(scopeId) && enabled,
    retry: false,
    staleTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false,
  });
}

export function useChannelStatus(scopeId: string, registrationId: string) {
  return useQuery({
    queryKey: channelKeys.status(scopeId, registrationId),
    queryFn: ({ signal }) => channelsApi.status(registrationId, signal),
    enabled: Boolean(scopeId && registrationId),
    retry: false,
    staleTime: 15_000,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false,
  });
}
