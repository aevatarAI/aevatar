import {
  ArrowRightOutlined,
  CheckOutlined,
  ExportOutlined,
  LinkOutlined,
  ReloadOutlined,
  RobotOutlined,
  TableOutlined,
} from '@ant-design/icons';
import { Button } from 'antd';
import * as React from 'react';
import { t } from '@/shared/i18n/messages';
import { AevatarContentSkeleton } from '@/shared/ui/AevatarContentSkeleton';
import { AevatarLoadingOverlay } from '@/shared/ui/AevatarLoading';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { buildChannelBindHref, buildChannelDetailsHref } from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import {
  ChannelBadge,
  ChannelIdentity,
  ChannelLink,
  ChannelLoadError,
  ChannelSkill,
  DeliveryStatus,
  InboundStatus,
  platformName,
} from './presentation';
import { useChannelRegistrations } from './queries';
import { channelsCss } from './styles';

function ConnectionGuide() {
  return (
    <section className="channels__guide" aria-labelledby="channel-guide-title">
      <div className="channels__section-heading">
        <h2 id="channel-guide-title">
          {t('channels.guide.title', 'How to connect a bot')}
        </h2>
        <Button
          type="primary"
          className="channels__add-bot"
          href="https://nyx.chrono-ai.fun/channel-bots"
          target="_blank"
          rel="noopener noreferrer"
          icon={<ExportOutlined />}
          iconPlacement="end"
        >
          {t('channels.guide.add', 'Add bot in NyxID')}
        </Button>
      </div>
      <ol className="channels__guide-steps">
        {[
          {
            icon: <RobotOutlined />,
            title: t('channels.guide.add', 'Add bot in NyxID'),
            description: t(
              'channels.guide.addHelp',
              'Create your bot in NyxID.',
            ),
          },
          {
            icon: <LinkOutlined rotate={45} />,
            title: t('channels.guide.bind', 'Refresh and bind'),
            description: t(
              'channels.guide.bindHelp',
              'Return here, refresh the table, then choose Bind.',
            ),
          },
          {
            icon: <TableOutlined />,
            title: t('channels.guide.view', 'View your bound bot'),
            description: t(
              'channels.guide.viewHelp',
              'Find your bot and its skill in the table below.',
            ),
          },
        ].map((step, index) => (
          <li key={step.title}>
            <span className="channels__guide-icon" aria-hidden="true">
              {step.icon}
            </span>
            <div>
              <span className="channels__guide-number">
                {t('channels.guide.step', 'STEP {number}', {
                  number: `0${index + 1}`,
                })}
              </span>
              <h3>{step.title}</h3>
              <p>{step.description}</p>
            </div>
            {index < 2 ? (
              <ArrowRightOutlined
                className="channels__guide-arrow"
                aria-hidden="true"
              />
            ) : null}
          </li>
        ))}
      </ol>
    </section>
  );
}

export default function ChannelsPage({
  scopeId,
}: {
  readonly scopeId: string;
}) {
  const registrations = useChannelRegistrations(scopeId);
  const toast = useConsoleToast();
  const [refreshing, setRefreshing] = React.useState(false);
  const refreshInFlight = React.useRef(false);
  async function refresh() {
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    setRefreshing(true);
    try {
      const result = await registrations.refetch();
      if (result.isError)
        toast.error(
          t('channels.error.refresh', 'Could not refresh channels. Try again.'),
        );
    } finally {
      refreshInFlight.current = false;
      setRefreshing(false);
    }
  }
  return (
    <WorkflowActivityVNextShell
      activeSection="channels"
      scopeId={scopeId}
      title={t('workflowActivityVNext.nav.channels', 'Channels')}
      description={t(
        'channels.description',
        'Bring your NyxID bots into Aevatar.',
      )}
      mainClassName="channels__main"
      contentClassName="channels__content"
    >
      <style>{channelsCss}</style>
      <ConnectionGuide />
      <section
        aria-labelledby="channel-bots-title"
        className="channels__connections"
      >
        <div className="channels__section-heading">
          <h2 id="channel-bots-title">
            {t('channels.bots.title', 'Channel bots')}
            {registrations.data ? (
              <span className="channels__count">
                {registrations.data.length}
              </span>
            ) : null}
          </h2>
          <Button
            icon={<ReloadOutlined />}
            iconPlacement="end"
            className="channels__refresh"
            loading={refreshing}
            disabled={registrations.isPending || refreshing}
            onClick={() => void refresh()}
            aria-label={t('channels.refresh', 'Refresh channels')}
          >
            {t('workflowActivityVNext.common.refresh', 'Refresh')}
          </Button>
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
            retry={() => void refresh()}
          />
        ) : registrations.data?.length ? (
          <div className="channels__table-wrap" aria-busy={refreshing}>
            <section
              className="channels__table-scroll"
              // biome-ignore lint/a11y/noNoninteractiveTabindex: Keyboard users must be able to scroll the table at narrow widths.
              tabIndex={0}
              aria-labelledby="channel-bots-title"
            >
              <table
                className="channels__table"
                aria-labelledby="channel-bots-title"
                inert={refreshing}
              >
                <thead>
                  <tr>
                    {[
                      ['name', 'Channel name'],
                      ['channel', 'Channel'],
                      ['skill', 'Skill'],
                      ['inbound', 'Inbound'],
                      ['delivery', 'Workflow delivery'],
                      ['binding', 'Binding'],
                      ['actions', 'Actions'],
                    ].map(([key, label]) => (
                      <th scope="col" key={key}>
                        {t(`channels.column.${key}`, label)}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {registrations.data.map((row) => (
                    <tr key={row.botId}>
                      <td>
                        <ChannelIdentity
                          registration={row}
                          label={row.label}
                          pending={false}
                        />
                      </td>
                      <td>{platformName(row.platform)}</td>
                      <td>
                        {row.skill ? (
                          <ChannelSkill skill={row.skill} />
                        ) : (
                          <span className="channels__muted">{'\u2013'}</span>
                        )}
                      </td>
                      <td>
                        {row.bindingStatus === 'unbound' ? (
                          <span className="channels__muted">{'\u2013'}</span>
                        ) : (
                          <InboundStatus value={row.nyxStatus ?? undefined} />
                        )}
                      </td>
                      <td>
                        {row.bindingStatus === 'unbound' ? (
                          <span className="channels__muted">{'\u2013'}</span>
                        ) : (
                          <DeliveryStatus
                            value={row.workflowDeliveryStatus}
                            platform={row.platform}
                          />
                        )}
                      </td>
                      <td>
                        {row.bindingStatus === 'bound' ? (
                          <span className="channels__bound">
                            <CheckOutlined aria-hidden="true" />
                            {t('channels.binding.bound', 'Bound')}
                          </span>
                        ) : row.availabilityStatus === 'available' ? (
                          <ChannelLink
                            className="channels__bind"
                            href={buildChannelBindHref(scopeId, row.botId)}
                          >
                            {t('channels.binding.bind', 'Bind')}
                          </ChannelLink>
                        ) : (
                          <ChannelBadge>
                            {t('channels.binding.unavailable', 'Unavailable')}
                          </ChannelBadge>
                        )}
                      </td>
                      <td className="channels__row-action">
                        {row.bindingStatus === 'bound' && row.id ? (
                          <ChannelLink
                            className="channels__manage"
                            href={buildChannelDetailsHref(scopeId, row.id)}
                          >
                            {t('channels.manage', 'Manage')}
                          </ChannelLink>
                        ) : (
                          <span className="channels__muted">{'\u2013'}</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
            {refreshing ? (
              <AevatarLoadingOverlay
                ariaLabel={t('channels.loading', 'Loading channels')}
              />
            ) : null}
          </div>
        ) : (
          <div className="channels__state">
            <h3>{t('channels.bots.empty', 'No bots yet')}</h3>
            <p>
              {t(
                'channels.bots.emptyHelp',
                'Add a bot in NyxID, then refresh this table.',
              )}
            </p>
          </div>
        )}
      </section>
    </WorkflowActivityVNextShell>
  );
}
