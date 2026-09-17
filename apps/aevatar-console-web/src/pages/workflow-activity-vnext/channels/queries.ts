import { useQuery } from '@tanstack/react-query';
import { listChannelServices } from '@/shared/api/channelServicesApi';
import { ChannelApiError, channelsApi } from '@/shared/api/channelsApi';

export const channelKeys = {
  detail: (scopeId: string, id: string) =>
    ['channels', scopeId, 'detail', id] as const,
  services: (scopeId: string) =>
    ['channels', scopeId, 'service-choices'] as const,
  skills: (scopeId: string, search: string) =>
    ['channels', scopeId, 'skills', search] as const,
  list: (scopeId: string) => ['channels', scopeId, 'registrations'] as const,
};

const queryOptions = {
  retry: false,
  staleTime: 0,
  refetchOnWindowFocus: false,
  refetchOnReconnect: false,
} as const;

export function useChannelServiceChoices(scopeId: string) {
  return useQuery({
    ...queryOptions,
    queryKey: channelKeys.services(scopeId),
    queryFn: ({ signal }) => listChannelServices(signal),
    enabled: Boolean(scopeId),
  });
}

export function useChannelRegistrations(scopeId: string, enabled = true) {
  return useQuery({
    ...queryOptions,
    queryKey: channelKeys.list(scopeId),
    queryFn: ({ signal }) => channelsApi.list(signal),
    enabled: Boolean(scopeId) && enabled,
  });
}

export function useChannelDetail(scopeId: string, id: string) {
  return useQuery({
    ...queryOptions,
    queryKey: channelKeys.detail(scopeId, id),
    queryFn: async ({ signal }) => {
      const detail = await channelsApi.get(id, signal);
      const inventory = await channelsApi.list(signal);
      const bot = inventory.find(
        (row) =>
          row.id === id &&
          row.botId === detail.botId &&
          row.platform === detail.platform &&
          row.bindingStatus === 'bound',
      );
      if (!bot) throw new ChannelApiError(404);
      // Detail may use the registration ID as its label; inventory owns the
      // current NyxID display name and inbound availability.
      return {
        ...detail,
        label: bot.label,
        nyxStatus: bot.nyxStatus,
        availabilityStatus: bot.availabilityStatus,
      };
    },
    enabled: Boolean(scopeId && id),
  });
}
