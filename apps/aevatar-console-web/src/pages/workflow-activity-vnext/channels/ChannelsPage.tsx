import { ArrowRightOutlined, ReloadOutlined } from '@ant-design/icons';
import { useQueryClient } from '@tanstack/react-query';
import { Button } from 'antd';
import * as React from 'react';
import type { ChannelRegistration } from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import { AevatarContentSkeleton } from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  buildChannelDetailsHref,
  buildTelegramConnectionHref,
} from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import {
  ChannelBadge,
  ChannelIcon,
  ChannelIdentity,
  ChannelLink,
  ChannelLoadError,
  ChannelSkill,
  DeliveryStatus,
  InboundStatus,
  platformName,
} from './presentation';
import {
  channelKeys,
  useChannelBotIdentities,
  useChannelRegistrations,
  useChannelStatus,
} from './queries';
import { channelsCss } from './styles';

const platforms = ['telegram', 'whatsapp'] as const;

function ConnectedRow({
  registration,
  scopeId,
  label,
  namePending,
}: {
  readonly registration: ChannelRegistration;
  readonly scopeId: string;
  readonly label: string | null;
  readonly namePending: boolean;
}) {
  const status = useChannelStatus(scopeId, registration.id);
  return (
    <tr>
      <td data-label={t('channels.column.name', 'Channel name')}>
        <ChannelIdentity
          registration={registration}
          label={label}
          pending={namePending}
        />
      </td>
      <td data-label={t('channels.column.channel', 'Channel')}>
        {platformName(registration.platform)}
      </td>
      <td data-label={t('channels.column.skill', 'Skill')}>
        <ChannelSkill skill={registration.skill} />
      </td>
      <td data-label={t('channels.column.inbound', 'Inbound')}>
        <InboundStatus
          value={status.isError ? undefined : status.data?.status}
          pending={status.isPending}
        />
      </td>
      <td data-label={t('channels.column.delivery', 'Workflow delivery')}>
        <DeliveryStatus
          value={
            status.data?.workflowDeliveryStatus ??
            registration.workflowDeliveryStatus
          }
          platform={registration.platform}
        />
      </td>
      <td className="channels__row-action">
        <ChannelLink
          className="channels__manage"
          href={buildChannelDetailsHref(scopeId, registration.id)}
        >
          {t('channels.manage', 'Manage')}
          <ArrowRightOutlined aria-hidden="true" />
        </ChannelLink>
      </td>
    </tr>
  );
}

export default function ChannelsPage({
  scopeId,
}: {
  readonly scopeId: string;
}) {
  const registrations = useChannelRegistrations(scopeId);
  const hasBotIds = Boolean(registrations.data?.some((row) => row.botId));
  const bots = useChannelBotIdentities(scopeId, hasBotIds);
  const toast = useConsoleToast();
  const queryClient = useQueryClient();
  const [refreshing, setRefreshing] = React.useState(false);
  const reportedNamesError = React.useRef(0);
  React.useEffect(() => {
    if (
      refreshing ||
      !bots.isError ||
      bots.isFetching ||
      bots.errorUpdatedAt === reportedNamesError.current
    )
      return;
    reportedNamesError.current = bots.errorUpdatedAt;
    if (!registrations.isError)
      toast.error(
        t(
          'channels.error.names',
          'Could not load channel names. Use Refresh to try again.',
        ),
      );
  }, [
    bots.errorUpdatedAt,
    bots.isError,
    bots.isFetching,
    refreshing,
    registrations.isError,
    toast,
  ]);
  async function refresh() {
    setRefreshing(true);
    try {
      const [result] = await Promise.all([
        registrations.refetch(),
        queryClient.refetchQueries({
          queryKey: ['channels', scopeId, 'status'],
          type: 'active',
        }),
        queryClient.refetchQueries({
          queryKey: channelKeys.bots(scopeId),
          type: 'active',
        }),
      ]);
      if (result.isError)
        toast.error(
          t('channels.error.refresh', 'Could not refresh channels. Try again.'),
        );
    } finally {
      setRefreshing(false);
    }
  }
  return (
    <WorkflowActivityVNextShell
      activeSection="channels"
      scopeId={scopeId}
      title={t('channels.title', 'Connect your channels to your agent')}
      description={t(
        'channels.description',
        'Choose a channel to connect your bot and select the services it can use.',
      )}
      mainClassName="channels__main"
      contentClassName="channels__content"
    >
      <style>{channelsCss}</style>
      <p className="channels__intro">
        {t(
          'channels.intro',
          'Once connected, talk to your Aevatar bot directly from the channel.',
        )}
      </p>
      <section aria-labelledby="available-channels">
        <h2 id="available-channels" className="channels__section-label">
          {t('channels.available', 'Available channels')}
        </h2>
        <div className="channels__platforms">
          {platforms.map((platform) => {
            const available = platform === 'telegram';
            return (
              <article
                className={`channels__platform${available ? '' : ' channels__platform--soon'}`}
                key={platform}
              >
                <div className="channels__platform-top">
                  <ChannelIcon platform={platform} />
                  <ChannelBadge tone={available ? 'success' : 'neutral'}>
                    {available
                      ? t('channels.availableNow', 'Available')
                      : t('channels.soon', 'Soon')}
                  </ChannelBadge>
                </div>
                <h3>{platformName(platform)}</h3>
                <p>
                  {t(
                    `channels.platform.${platform}.description`,
                    {
                      telegram:
                        'Connect with a BotFather bot token. Webhook setup is handled for you.',
                      whatsapp: 'Chat with your bot on WhatsApp.',
                    }[platform],
                  )}
                </p>
                {available ? (
                  <ChannelLink
                    className="channels__connect"
                    href={buildTelegramConnectionHref(scopeId)}
                    aria-label={t(
                      'channels.connectPlatform',
                      'Connect {platform}',
                      { platform: platformName(platform) },
                    )}
                  >
                    {t('channels.connect', 'Connect')}
                    <ArrowRightOutlined aria-hidden="true" />
                  </ChannelLink>
                ) : (
                  <span className="channels__muted">
                    {t('channels.soon', 'Soon')}
                  </span>
                )}
              </article>
            );
          })}
        </div>
      </section>
      <section
        aria-labelledby="connected-channels"
        className="channels__connections"
      >
        <div className="channels__section-heading">
          <div>
            <h2 id="connected-channels">
              {t('channels.connected', 'Connected')}
              {registrations.data ? (
                <span className="channels__count">
                  {registrations.data.length}
                </span>
              ) : null}
            </h2>
            <p>
              {t(
                'channels.connectedDescription',
                'Channels connected to your account.',
              )}
            </p>
          </div>
          <Button
            className="channels__refresh"
            icon={<ReloadOutlined />}
            loading={refreshing}
            disabled={registrations.isPending}
            onClick={() => void refresh()}
            aria-label={t('channels.refresh', 'Refresh channels')}
          />
        </div>
        {registrations.isPending ? (
          <AevatarContentSkeleton
            ariaLabel={t('channels.loading', 'Loading channels')}
            variant="table"
            rows={3}
          />
        ) : registrations.isError && !registrations.data ? (
          <ChannelLoadError
            error={registrations.error}
            pending={registrations.isFetching}
            retry={() => void registrations.refetch()}
          />
        ) : registrations.data?.length ? (
          <div className="channels__table-wrap">
            <table
              className="channels__table"
              aria-labelledby="connected-channels"
            >
              <thead>
                <tr>
                  {[
                    ['name', 'Channel name'],
                    ['channel', 'Channel'],
                    ['skill', 'Skill'],
                    ['inbound', 'Inbound'],
                    ['delivery', 'Workflow delivery'],
                    ['actions', 'Actions'],
                  ].map(([key, label]) => (
                    <th scope="col" key={key}>
                      {t(`channels.column.${key}`, label)}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {registrations.data.map((registration) => (
                  <ConnectedRow
                    key={registration.id}
                    registration={registration}
                    scopeId={scopeId}
                    label={
                      bots.data?.find(
                        (bot) =>
                          bot.id === registration.botId &&
                          bot.platform.toLowerCase() ===
                            registration.platform.toLowerCase(),
                      )?.label ?? null
                    }
                    namePending={Boolean(registration.botId) && bots.isPending}
                  />
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="channels__state">
            <ChannelIcon platform="unknown" />
            <h3>{t('channels.empty.title', 'No channels connected yet')}</h3>
            <p>
              {t(
                'channels.empty.description',
                'Connect Telegram above to start talking to your agent.',
              )}
            </p>
          </div>
        )}
      </section>
    </WorkflowActivityVNextShell>
  );
}
