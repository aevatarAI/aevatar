import {
  ArrowLeftOutlined,
  ExportOutlined,
  EyeInvisibleOutlined,
  EyeOutlined,
} from '@ant-design/icons';
import { Button, Input, Modal } from 'antd';
import * as React from 'react';
import {
  ChannelRegistrationError,
  type ChannelRegistrationFailure,
  channelsApi,
} from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { buildWorkflowActivitySectionHref } from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import ChannelServicePicker from './ChannelServicePicker';
import ChannelSkillField from './ChannelSkillField';
import { getChannelWebhookBaseUrl } from './config';
import { channelConnectionCss } from './connectionStyles';
import { useChannelServiceChoices } from './queries';
import { channelsCss } from './styles';

function registrationErrorMessage(reason: ChannelRegistrationFailure) {
  const messages = {
    token: [
      'channels.connect.error.token',
      'Check the bot token from BotFather and try again.',
    ],
    botName: [
      'channels.connect.error.botName',
      'Could not read the Telegram bot name. Please try again.',
    ],
    services: [
      'channels.connect.error.services',
      'The selected services are no longer available. Review your selection and try again.',
    ],
    skill: [
      'channels.connect.error.skill',
      'Could not configure the bot skill. Check the Skill name and your access in Ornn.',
    ],
    authorization: [
      'channels.connect.error.authorization',
      'Your session or service access needs attention. Sign in again and review your NyxID access.',
    ],
    conflict: [
      'channels.connect.error.conflict',
      'This bot may already be connected. Check your channels before trying again.',
    ],
    configuration: [
      'channels.connect.error.configuration',
      'Channel setup is unavailable. Ask your administrator to check the deployment configuration.',
    ],
    rejected: [
      'channels.connect.error.rejected',
      'Could not connect Telegram. Check the bot token, Skill name, and selected services, then try again.',
    ],
    uncertain: [
      'channels.connect.error.uncertain',
      'Could not confirm the connection. Please try again.',
    ],
  } as const;
  const [key, fallback] = messages[reason];
  return t(key, fallback);
}

export default function TelegramConnectionPage({
  scopeId,
}: {
  readonly scopeId: string;
}) {
  // Secret input stays in this component only, never in Query mutation state,
  // a persisted draft, navigation URL, or navigation state.
  const [botToken, setBotToken] = React.useState('');
  const [channelName, setChannelName] = React.useState('');
  const [skillName, setSkillName] = React.useState('');
  const [selectedIds, setSelectedIds] = React.useState<string[]>([]);
  const [submitting, setSubmitting] = React.useState(false);
  const [submissionAccepted, setSubmissionAccepted] = React.useState(false);
  const [validationAttempted, setValidationAttempted] = React.useState(false);
  const [leaveTarget, setLeaveTarget] = React.useState<string | null>(null);
  const inFlight = React.useRef(false);
  const mounted = React.useRef(true);
  const toast = useConsoleToast();
  const webhookBaseUrl = getChannelWebhookBaseUrl();
  const listHref = buildWorkflowActivitySectionHref(scopeId, 'channels');
  const services = useChannelServiceChoices(scopeId);
  const invalidSelection = selectedIds.some(
    (id) =>
      !services.data?.some(
        (service) => service.id === id && service.active && service.allowed,
      ),
  );
  const locked = submitting || submissionAccepted;
  const dirty = Boolean(
    botToken || channelName || skillName || selectedIds.length,
  );

  React.useEffect(() => {
    if (!services.isSuccess || services.isFetching) return;
    const availableIds = new Set(services.data.map((service) => service.id));
    setSelectedIds((current) => {
      const available = current.filter((id) => availableIds.has(id));
      return available.length === current.length ? current : available;
    });
  }, [services.data, services.isSuccess, services.isFetching]);

  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  React.useEffect(() => {
    if (submissionAccepted || (!dirty && !submitting)) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty, submitting, submissionAccepted]);

  function navigate(target: string) {
    if (submitting) return;
    if (dirty && !submissionAccepted) setLeaveTarget(target);
    else history.push(target);
  }

  async function connect(event: React.FormEvent) {
    event.preventDefault();
    if (inFlight.current || locked) return;
    setValidationAttempted(true);
    if (
      !botToken.trim() ||
      !webhookBaseUrl ||
      !services.isSuccess ||
      services.isFetching ||
      invalidSelection
    )
      return;
    inFlight.current = true;
    setSubmitting(true);
    try {
      await channelsApi.registerTelegram({
        botToken,
        label: channelName,
        skillName,
        serviceIds: selectedIds,
        webhookBaseUrl,
      });
    } catch (failure) {
      if (!mounted.current) return;
      const reason =
        failure instanceof ChannelRegistrationError
          ? failure.reason
          : 'uncertain';
      if (reason === 'services') void services.refetch();
      toast.error(registrationErrorMessage(reason));
      return;
    } finally {
      inFlight.current = false;
      if (mounted.current) setSubmitting(false);
    }
    if (!mounted.current) return;
    setBotToken('');
    setSubmissionAccepted(true);
    // Admission is not completion. The list owns loading the resulting channel;
    // do not leave the form waiting on a one-shot read or start background polls.
    toast.info(
      t(
        'channels.connect.accepted',
        'Telegram connection request submitted. Use Refresh if the channel is not visible yet.',
      ),
    );
    history.replace(listHref);
  }

  return (
    <WorkflowActivityVNextShell
      activeSection="channels"
      scopeId={scopeId}
      title={t('channels.connect.title', 'Connect Telegram')}
      mainClassName="channels__main channels__main--connect"
      contentClassName="channels__content"
      onNavigate={navigate}
    >
      <style>
        {channelsCss}
        {channelConnectionCss}
      </style>
      <div className="channels__connect-heading">
        <nav
          className="channels__breadcrumb"
          aria-label={t('channels.breadcrumb', 'Channel navigation')}
        >
          <a
            href={listHref}
            onClick={(event) => {
              event.preventDefault();
              navigate(listHref);
            }}
          >
            <ArrowLeftOutlined aria-hidden="true" />
            {t('workflowActivityVNext.nav.channels', 'Channels')}
          </a>
        </nav>
        <h1>{t('channels.connect.title', 'Connect Telegram')}</h1>
        <p>
          {t(
            'channels.connect.description',
            'Add your bot and choose the services it can use. Telegram webhook setup is automatic.',
          )}
        </p>
      </div>
      <form
        className="channels__connection-form"
        onSubmit={(event) => void connect(event)}
        noValidate
        aria-label={t('channels.connect.title', 'Connect Telegram')}
      >
        <h2 className="channels__form-section-title">
          {t('channels.connect.botDetails', 'Bot details')}
        </h2>
        <div className="channels__field">
          <div className="channels__field-heading">
            <label htmlFor="telegram-bot-token">
              {t('channels.connect.botToken', 'Bot token')}
              <span className="channels__required" aria-hidden="true">
                *
              </span>
            </label>
            <a
              href="https://t.me/BotFather"
              target="_blank"
              rel="noopener noreferrer"
            >
              {t('channels.connect.botFather', 'Get token from BotFather')}{' '}
              <ExportOutlined aria-hidden="true" />
            </a>
          </div>
          <Input.Password
            id="telegram-bot-token"
            required
            value={botToken}
            disabled={locked}
            autoComplete="new-password"
            spellCheck={false}
            iconRender={(visible) => (
              <button
                type="button"
                disabled={locked}
                aria-label={
                  visible
                    ? t('channels.connect.hideToken', 'Hide bot token')
                    : t('channels.connect.showToken', 'Show bot token')
                }
                aria-pressed={visible}
              >
                {visible ? (
                  <EyeOutlined aria-hidden="true" />
                ) : (
                  <EyeInvisibleOutlined aria-hidden="true" />
                )}
              </button>
            )}
            aria-invalid={
              validationAttempted && !botToken.trim() && !submissionAccepted
            }
            onChange={(event) => setBotToken(event.target.value)}
          />
          {validationAttempted && !botToken.trim() && !locked ? (
            <p className="channels__form-error" role="alert">
              {t(
                'channels.connect.tokenRequired',
                'Enter the bot token from BotFather.',
              )}
            </p>
          ) : null}
        </div>
        <div className="channels__name-fields">
          <div className="channels__field">
            <div className="channels__field-heading">
              <label htmlFor="telegram-channel-name">
                {t('channels.connect.name', 'Channel name')}{' '}
                <span>{t('channels.connect.optional', '(optional)')}</span>
              </label>
            </div>
            <Input
              id="telegram-channel-name"
              value={channelName}
              placeholder={t(
                'channels.connect.botNameDefault',
                'Defaults to the bot name + 6 random digits',
              )}
              disabled={locked}
              onChange={(event) => setChannelName(event.target.value)}
            />
          </div>
          <ChannelSkillField
            value={skillName}
            onChange={setSkillName}
            disabled={locked}
            placeholder={t(
              'channels.connect.skillNameDefault',
              'Defaults to the Telegram bot name',
            )}
          />
        </div>
        <ChannelServicePicker
          services={services.data ?? []}
          selectedIds={selectedIds}
          onChange={setSelectedIds}
          loading={services.isPending}
          failed={services.isError}
          refreshing={services.isFetching}
          disabled={locked}
          retry={() => void services.refetch()}
        />
        {invalidSelection && !locked ? (
          <p className="channels__form-error" role="alert">
            {t(
              'channels.connect.selectionChanged',
              'Some selected services are no longer available. Deselect them before connecting.',
            )}
          </p>
        ) : null}
        {!webhookBaseUrl ? (
          <p className="channels__form-error" role="alert">
            {registrationErrorMessage('configuration')}
          </p>
        ) : null}
        <div className="channels__form-actions">
          <Button disabled={submitting} onClick={() => navigate(listHref)}>
            {submissionAccepted
              ? t('channels.back', 'Back to channels')
              : t('channels.cancel', 'Cancel')}
          </Button>
          <Button
            type="primary"
            htmlType="submit"
            loading={submitting}
            disabled={
              locked ||
              !webhookBaseUrl ||
              !services.isSuccess ||
              services.isFetching ||
              invalidSelection
            }
          >
            {submissionAccepted
              ? t('channels.connect.submitted', 'Request submitted')
              : t('channels.connect.title', 'Connect Telegram')}
          </Button>
        </div>
      </form>
      <Modal
        open={leaveTarget !== null}
        title={t(
          'channels.connect.discardTitle',
          'Discard this connection setup?',
        )}
        onCancel={() => setLeaveTarget(null)}
        onOk={() => {
          if (leaveTarget) history.push(leaveTarget);
          setLeaveTarget(null);
        }}
        okText={t('channels.connect.discard', 'Discard')}
        cancelText={t('channels.connect.stay', 'Stay')}
      >
        {t(
          'channels.connect.discardHelp',
          'Your bot token and unsaved choices will be cleared.',
        )}
      </Modal>
    </WorkflowActivityVNextShell>
  );
}
