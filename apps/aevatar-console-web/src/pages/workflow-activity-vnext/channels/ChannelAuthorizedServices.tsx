import { useQuery } from '@tanstack/react-query';
import { Button } from 'antd';
import * as React from 'react';
import { listChannelServiceIdentities } from '@/shared/api/channelServicesApi';
import type { ChannelServiceAuthorization } from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import { AevatarLoadingDots } from '@/shared/ui/AevatarLoading';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';

export default function ChannelAuthorizedServices({
  scopeId,
  authorization,
}: {
  readonly scopeId: string;
  readonly authorization: ChannelServiceAuthorization;
}) {
  const serviceIds =
    authorization.kind === 'explicit' ? authorization.serviceIds : [];
  const names = useQuery({
    queryKey: ['channels', scopeId, 'service-identities'],
    queryFn: ({ signal }) => listChannelServiceIdentities(signal),
    enabled: Boolean(scopeId && serviceIds.length),
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false,
  });
  const toast = useConsoleToast();
  const reportedError = React.useRef(0);
  React.useEffect(() => {
    if (
      !serviceIds.length ||
      !names.isError ||
      names.errorUpdatedAt === reportedError.current
    )
      return;
    reportedError.current = names.errorUpdatedAt;
    toast.error(
      t(
        'channels.services.namesError',
        'Could not load service names. Please try again.',
      ),
    );
  }, [serviceIds.length, names.isError, names.errorUpdatedAt, toast]);

  if (authorization.kind === 'nyxidDefault')
    return (
      <span>
        {t(
          'channels.services.default',
          'Uses NyxID default authorization; individual services are not listed.',
        )}
      </span>
    );
  if (authorization.kind === 'unavailable')
    return (
      <span>
        {t(
          'channels.services.unavailable',
          'Authorization details are unavailable for this channel.',
        )}
      </span>
    );
  if (!serviceIds.length)
    return (
      <span>{t('channels.services.empty', 'No services authorized.')}</span>
    );

  return (
    <div className="channels__authorized-services">
      {names.isPending ? (
        <AevatarLoadingDots
          ariaLabel={t('channels.services.loading', 'Loading service names')}
          size="small"
        />
      ) : null}
      <ul>
        {serviceIds.map((id) => {
          const service = names.data?.find((item) => item.id === id);
          return (
            <li key={id}>
              {service ? (
                <strong>{service.label}</strong>
              ) : (
                <span className="channels__identifier">{id}</span>
              )}
            </li>
          );
        })}
      </ul>
      {names.isError ? (
        <Button loading={names.isFetching} onClick={() => void names.refetch()}>
          {t('channels.retry', 'Try again')}
        </Button>
      ) : null}
    </div>
  );
}
