import {
  ArrowLeftOutlined,
  DeleteOutlined,
  EditOutlined,
  ExportOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { Button, Modal, Tooltip } from 'antd';
import * as React from 'react';
import { channelsApi } from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { AevatarContentSkeleton } from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  buildChannelEditHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import ChannelAuthorizedServices from './ChannelAuthorizedServices';
import {
  ChannelIcon,
  ChannelLink,
  ChannelLoadError,
  ChannelSkill,
  DeliveryStatus,
  InboundStatus,
  platformName,
} from './presentation';
import {
  useChannelBotIdentities,
  useChannelRegistrations,
  useChannelStatus,
} from './queries';
import { channelsCss } from './styles';

// Figma links to the NyxID website, separate from its API/OIDC authority.
const NYXID_WEB_BASE_URL = 'https://nyx.chrono-ai.fun';

export default function ChannelDetailsPage({
  scopeId,
  registrationId,
}: {
  readonly scopeId: string;
  readonly registrationId: string;
}) {
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [removing, setRemoving] = React.useState(false);
  const [removalAccepted, setRemovalAccepted] = React.useState(false);
  const [hasWarnings, setHasWarnings] = React.useState(false);
  const [removeError, setRemoveError] = React.useState(false);
  const registrations = useChannelRegistrations(scopeId);
  const registration = registrations.data?.find(
    (row) => row.id === registrationId && row.scopeId === scopeId,
  );
  const bots = useChannelBotIdentities(scopeId, Boolean(registration?.botId));
  const channelName = bots.data?.find(
    (bot) =>
      bot.id === registration?.botId && bot.platform === registration?.platform,
  )?.label;
  const status = useChannelStatus(scopeId, registration ? registrationId : '');
  const toast = useConsoleToast();
  const completed = React.useRef(false);
  const listHref = buildWorkflowActivitySectionHref(scopeId, 'channels');

  React.useEffect(() => {
    if (
      !removalAccepted ||
      !registrations.isSuccess ||
      registrations.isFetching ||
      registration ||
      completed.current
    )
      return;
    completed.current = true;
    if (hasWarnings)
      toast.warning(
        t(
          'channels.remove.warning',
          'Channel removed. Some cleanup needs attention in NyxID.',
        ),
      );
    else toast.success(t('channels.remove.success', 'Channel removed.'));
    history.replace(listHref);
  }, [
    removalAccepted,
    registrations.isSuccess,
    registrations.isFetching,
    registration,
    hasWarnings,
    listHref,
    toast,
  ]);

  async function remove() {
    if (removing || removalAccepted) return;
    setRemoving(true);
    setRemoveError(false);
    try {
      const result = await channelsApi.remove(registrationId);
      setHasWarnings(result.hasWarnings);
      setRemovalAccepted(true);
      setConfirmOpen(false);
      await registrations.refetch();
    } catch {
      setRemoveError(true);
      toast.error(
        t(
          'channels.remove.error',
          'Could not remove this channel. Please try again.',
        ),
      );
    } finally {
      setRemoving(false);
    }
  }

  return (
    <WorkflowActivityVNextShell
      activeSection="channels"
      scopeId={scopeId}
      title={t('channels.details', 'Channel details')}
      mainClassName="channels__main channels__main--detail"
      contentClassName="channels__content"
    >
      <style>{channelsCss}</style>
      <div className="channels__detail-toolbar">
        <nav
          className="channels__breadcrumb"
          aria-label={t('channels.breadcrumb', 'Channel navigation')}
        >
          <ChannelLink href={listHref}>
            <ArrowLeftOutlined aria-hidden="true" />
            {t('workflowActivityVNext.nav.channels', 'Channels')}
          </ChannelLink>
          <span aria-hidden="true">/</span>
          <span>
            {registration
              ? t('channels.managePlatform', '{platform} · Manage', {
                  platform: platformName(registration.platform),
                })
              : t('channels.details', 'Channel details')}
          </span>
        </nav>
        {registration ? (
          <div className="channels__detail-actions">
            <Button
              icon={<EditOutlined />}
              disabled={removing || removalAccepted}
              onClick={() =>
                history.push(buildChannelEditHref(scopeId, registrationId))
              }
            >
              {t('channels.edit', 'Edit')}
            </Button>
            <Button
              danger
              icon={<DeleteOutlined />}
              loading={removing}
              disabled={removalAccepted}
              onClick={() => setConfirmOpen(true)}
            >
              {t('channels.remove', 'Remove')}
            </Button>
          </div>
        ) : null}
      </div>
      {registrations.isPending ? (
        <AevatarContentSkeleton
          ariaLabel={t('channels.details.loading', 'Loading channel details')}
          variant="list"
          rows={6}
        />
      ) : registrations.isError && !registration ? (
        <ChannelLoadError
          error={registrations.error}
          pending={registrations.isFetching}
          retry={() => void registrations.refetch()}
        />
      ) : !registration ? (
        <div className="channels__state" role="status">
          <p>
            {removalAccepted
              ? t('channels.remove.confirming', 'Confirming removal…')
              : t(
                  'channels.error.unavailable',
                  'This channel is unavailable or you do not have access.',
                )}
          </p>
          <ChannelLink href={listHref}>
            {t('channels.back', 'Back to channels')}
          </ChannelLink>
        </div>
      ) : (
        <>
          <section
            className="channels__detail"
            aria-label={t('channels.details', 'Channel details')}
          >
            <div className="channels__detail-heading">
              <ChannelIcon platform={registration.platform} />
              <div className="channels__identity-copy">
                <h1>
                  {channelName ??
                    (registration.botId && bots.isPending
                      ? t('channels.name.loading', 'Loading name…')
                      : t('channels.name.unavailable', 'Name unavailable'))}
                  {registration.botId && !channelName && !bots.isPending ? (
                    <Tooltip
                      title={t('channels.name.retry', 'Reload channel name')}
                    >
                      <Button
                        type="text"
                        icon={<ReloadOutlined />}
                        aria-label={t(
                          'channels.name.retry',
                          'Reload channel name',
                        )}
                        loading={bots.isFetching}
                        onClick={() => void bots.refetch()}
                      />
                    </Tooltip>
                  ) : null}
                </h1>
                <p className="channels__identifier">
                  {t('channels.registration', 'Registration')} {registration.id}
                </p>
              </div>
              <InboundStatus
                value={status.isError ? undefined : status.data?.status}
                pending={status.isPending}
              />
            </div>
            <dl className="channels__facts">
              <div>
                <dt>{t('channels.column.channel', 'Channel')}</dt>
                <dd>{platformName(registration.platform)}</dd>
              </div>
              <div>
                <dt>{t('channels.column.skill', 'Skill')}</dt>
                <dd>
                  <ChannelSkill skill={registration.skill} />
                </dd>
              </div>
              <div>
                <dt>{t('channels.services.title', 'Authorized services')}</dt>
                <dd>
                  <ChannelAuthorizedServices
                    scopeId={scopeId}
                    authorization={registration.serviceAuthorization}
                  />
                </dd>
              </div>
              <div>
                <dt>{t('channels.inboundMessages', 'Inbound messages')}</dt>
                <dd>
                  <InboundStatus
                    value={status.isError ? undefined : status.data?.status}
                    pending={status.isPending}
                  />
                </dd>
              </div>
              <div>
                <dt>{t('channels.column.delivery', 'Workflow delivery')}</dt>
                <dd>
                  <DeliveryStatus
                    value={
                      status.data?.workflowDeliveryStatus ??
                      registration.workflowDeliveryStatus
                    }
                    platform={registration.platform}
                  />
                </dd>
              </div>
              {[
                {
                  label: t('channels.botId', 'Bot ID'),
                  value: registration.botId,
                  path: '/channel-bots/',
                },
                {
                  label: t('channels.provider', 'Provider'),
                  value: registration.providerSlug,
                },
                {
                  label: t('channels.keyId', 'Agent key ID'),
                  value: registration.agentKeyId,
                  path: '/keys/api-key/',
                },
              ].map(({ label, value, path }) => (
                <div key={label}>
                  <dt>{label}</dt>
                  <dd className="channels__identifier">
                    {value && path ? (
                      <a
                        className="channels__identifier-link"
                        href={`${NYXID_WEB_BASE_URL}${path}${encodeURIComponent(value)}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        aria-label={t(
                          'channels.openInNyxID',
                          'Open {label} {id} in NyxID (new tab)',
                          { label, id: value },
                        )}
                      >
                        <span>{value}</span>
                        <ExportOutlined aria-hidden="true" />
                      </a>
                    ) : (
                      value || '—'
                    )}
                  </dd>
                </div>
              ))}
            </dl>
          </section>
          {removalAccepted ? (
            <div className="channels__removal" role="status">
              <p>
                {t(
                  'channels.remove.pending',
                  'Removal requested. Waiting for the channel list to confirm the change.',
                )}
              </p>
              <Button
                loading={registrations.isFetching}
                onClick={() => void registrations.refetch()}
              >
                {t('channels.remove.check', 'Check again')}
              </Button>
            </div>
          ) : null}
          <Modal
            title={t('channels.remove.title', 'Remove this channel?')}
            open={confirmOpen}
            onCancel={() => {
              if (!removing) setConfirmOpen(false);
            }}
            onOk={() => void remove()}
            confirmLoading={removing}
            okButtonProps={{ danger: true }}
            cancelButtonProps={{ disabled: removing }}
            closable={!removing}
            mask={{ closable: !removing }}
            keyboard={!removing}
            okText={t('channels.remove', 'Remove')}
            cancelText={t('channels.cancel', 'Cancel')}
          >
            <p>
              {t(
                'channels.remove.description',
                'This bot will stop routing messages to Aevatar. To reconnect it, you will need to set it up again.',
              )}
            </p>
            <p className="channels__identifier">
              {platformName(registration.platform)} ·{' '}
              {registration.botId ?? registration.id}
            </p>
            {removeError ? (
              <p role="alert">
                {t(
                  'channels.remove.error',
                  'Could not remove this channel. Please try again.',
                )}
              </p>
            ) : null}
          </Modal>
        </>
      )}
    </WorkflowActivityVNextShell>
  );
}
