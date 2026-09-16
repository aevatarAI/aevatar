import { ArrowLeftOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Input, Modal } from 'antd';
import * as React from 'react';
import {
  type ChannelBotIdentity,
  isValidChannelBotLabel,
  updateChannelBotLabel,
} from '@/shared/api/channelBotsApi';
import {
  type ChannelConfigDetail,
  ChannelConfigError,
  type ChannelConfigField,
  type ChannelConfigUpdate,
  channelConfigMatches,
  channelRuntimeConfigApi,
} from '@/shared/api/channelRuntimeConfigApi';
import { ChannelApiError } from '@/shared/api/channelsApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { AevatarContentSkeleton } from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  buildChannelDetailsHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import ChannelSkillField from './ChannelSkillField';
import { channelConnectionCss } from './connectionStyles';
import { ChannelLoadError, platformName } from './presentation';
import {
  channelKeys,
  useChannelBotIdentities,
  useChannelRegistrations,
} from './queries';
import { channelsCss } from './styles';

export default function ChannelEditPage({
  scopeId,
  registrationId,
}: {
  readonly scopeId: string;
  readonly registrationId: string;
}) {
  const [initial, setInitial] = React.useState<{
    config: ChannelConfigDetail;
    bot: ChannelBotIdentity;
  } | null>(null);
  const config = useQuery({
    queryKey: channelKeys.config(scopeId, registrationId),
    queryFn: ({ signal }) =>
      channelRuntimeConfigApi.get(registrationId, scopeId, signal),
    enabled: Boolean(scopeId && registrationId),
    retry: false,
    staleTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  const [navigate, setNavigate] = React.useState<(target: string) => void>(
    () => history.push,
  );
  const registrations = useChannelRegistrations(scopeId);
  const bots = useChannelBotIdentities(scopeId, Boolean(registrationId));
  const queries = [config, registrations, bots];
  const pending = queries.some((query) => query.isFetching);
  const fresh = queries.every(
    (query) =>
      query.isSuccess && query.isFetchedAfterMount && !query.isFetching,
  );
  const registration = registrations.data?.find(
    (row) => row.id === registrationId && row.scopeId === scopeId && row.owned,
  );
  const bot = bots.data?.find(
    (row) =>
      row.id === registration?.botId && row.platform === config.data?.platform,
  );
  const loadError =
    queries.find((query) => query.isError)?.error ??
    (fresh && (!bot || registration?.platform !== config.data?.platform)
      ? new ChannelApiError(404)
      : null);
  const error = pending ? null : loadError;
  React.useEffect(() => {
    if (!initial && fresh && !error && config.data && bot)
      setInitial({ config: config.data, bot });
  }, [initial, fresh, error, config.data, bot]);
  const title = initial
    ? t('channels.edit.title', 'Edit {platform}', {
        platform: platformName(initial.config.platform),
      })
    : t('channels.edit.loadingTitle', 'Edit channel');
  const listHref = buildWorkflowActivitySectionHref(scopeId, 'channels');
  return (
    <WorkflowActivityVNextShell
      activeSection="channels"
      scopeId={scopeId}
      title={title}
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
        <h1>{title}</h1>
      </div>
      {!initial && !error ? (
        <AevatarContentSkeleton
          ariaLabel={t(
            'channels.edit.loading',
            'Loading channel configuration',
          )}
          variant="list"
          rows={4}
        />
      ) : !initial && error ? (
        <ChannelLoadError
          error={error}
          pending={pending}
          retry={() => {
            void config.refetch();
            void registrations.refetch();
            void bots.refetch();
          }}
        />
      ) : initial ? (
        <ChannelEditForm
          key={`${scopeId}:${registrationId}`}
          initial={initial.config}
          initialBot={initial.bot}
          title={title}
          setNavigate={setNavigate}
          readBack={async () => {
            const result = await config.refetch();
            if (result.isError || !result.data) throw result.error;
            return result.data;
          }}
        />
      ) : null}
    </WorkflowActivityVNextShell>
  );
}

function ChannelEditForm({
  initial,
  initialBot,
  title,
  setNavigate,
  readBack,
}: {
  readonly initial: ChannelConfigDetail;
  readonly initialBot: ChannelBotIdentity;
  readonly title: string;
  readonly setNavigate: React.Dispatch<
    React.SetStateAction<(target: string) => void>
  >;
  readonly readBack: () => Promise<ChannelConfigDetail>;
}) {
  const baseline = initial;
  const [skillName, setSkillName] = React.useState(
    baseline.runtimeConfig.defaultSkill.name,
  );
  const [label, setLabel] = React.useState(initialBot.label ?? '');
  const [savedLabel, setSavedLabel] = React.useState(initialBot.label ?? '');
  const labelId = React.useId();
  const [errors, setErrors] = React.useState<
    readonly (ChannelConfigField | 'label')[]
  >([]);
  const [submitting, setSubmitting] = React.useState(false);
  const [leaveTarget, setLeaveTarget] = React.useState<string | null>(null);
  const inFlight = React.useRef(false);
  const mounted = React.useRef(true);
  const completed = React.useRef(false);
  const toast = useConsoleToast();
  const queryClient = useQueryClient();
  const detailsHref = buildChannelDetailsHref(
    baseline.scopeId,
    baseline.registrationId,
  );
  const update: ChannelConfigUpdate = {
    ...baseline,
    runtimeConfig: {
      ...baseline.runtimeConfig,
      defaultSkill: {
        name: skillName,
        version: skillName.trim()
          ? baseline.runtimeConfig.defaultSkill.version
          : '',
      },
    },
  };
  const labelChanged = label.trim() !== savedLabel;
  const skillChanged = !channelConfigMatches(update, baseline);
  const dirty = labelChanged || skillChanged;

  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  React.useEffect(() => {
    if ((!dirty && !submitting) || completed.current) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty, submitting]);
  React.useEffect(() => {
    setNavigate(() => (target: string) => {
      if (inFlight.current) return;
      if (dirty && !completed.current) setLeaveTarget(target);
      else history.push(target);
    });
  }, [dirty, setNavigate]);

  async function finishSave(expected?: ChannelConfigUpdate) {
    let confirmed = !expected;
    let readFailed = false;
    try {
      if (expected) {
        const actual = await readBack();
        confirmed =
          actual.stateVersion > baseline.stateVersion &&
          channelConfigMatches(actual, expected);
      }
    } catch {
      readFailed = true;
    }
    if (!mounted.current) return;
    completed.current = true;
    void queryClient.invalidateQueries({
      queryKey: channelKeys.list(baseline.scopeId),
    });
    void queryClient.invalidateQueries({
      queryKey: channelKeys.status(baseline.scopeId, baseline.registrationId),
    });
    if (confirmed) {
      toast.success(t('channels.edit.saved', 'Channel changes saved.'));
    } else if (readFailed) {
      toast.warning(
        t(
          'channels.edit.readbackUnavailable',
          'Changes submitted, but the latest configuration could not be loaded. Refresh the channel details to view it.',
        ),
      );
    } else {
      toast.info(
        t(
          'channels.edit.submitted',
          'Changes submitted. They may take a moment to appear in channel details.',
        ),
      );
    }
    history.replace(detailsHref);
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (inFlight.current || submitting || !dirty || completed.current) return;
    const invalid: (ChannelConfigField | 'label')[] = [];
    if (labelChanged && !isValidChannelBotLabel(label)) invalid.push('label');
    if (skillName.trim().length > 128) invalid.push('skill');
    setErrors(invalid);
    if (invalid.length) return;
    inFlight.current = true;
    setSubmitting(true);
    let labelSaved = false;
    let updatingLabel = labelChanged;
    try {
      if (labelChanged) {
        const updated = await updateChannelBotLabel(initialBot, label);
        queryClient.setQueryData<ChannelBotIdentity[]>(
          channelKeys.bots(baseline.scopeId),
          (current) =>
            current?.map((bot) => (bot.id === updated.id ? updated : bot)),
        );
        void queryClient.invalidateQueries({
          queryKey: channelKeys.bots(baseline.scopeId),
          refetchType: 'none',
        });
        if (!mounted.current) return;
        setSavedLabel(updated.label ?? '');
        labelSaved = true;
        updatingLabel = false;
      }
      if (skillChanged)
        await channelRuntimeConfigApi.update(
          baseline.registrationId,
          update.runtimeConfig,
        );
      if (!mounted.current) return;
      // Read once for truthful feedback, then leave the editor even if the
      // accepted change is not visible yet. Never poll or resubmit to confirm.
      await finishSave(skillChanged ? update : undefined);
    } catch (error) {
      if (!mounted.current) return;
      if (error instanceof ChannelConfigError) {
        setErrors(error.fields);
      }
      if (
        updatingLabel &&
        error instanceof ChannelApiError &&
        error.status === 400
      )
        setErrors(['label']);
      toast.error(
        !updatingLabel &&
          (labelSaved || savedLabel !== (initialBot.label ?? ''))
          ? t(
              'channels.edit.partialSave',
              'Label saved, but the skill name could not be updated. Try saving again.',
            )
          : (error instanceof ChannelApiError &&
                [401, 403].includes(error.status)) ||
              (error instanceof ChannelConfigError &&
                error.fields.includes('services'))
            ? t(
                'channels.connect.error.authorization',
                'Your session or service access needs attention. Sign in again and review your NyxID access.',
              )
            : error instanceof ChannelApiError &&
                error.status === 404 &&
                !(
                  error instanceof ChannelConfigError &&
                  error.fields.includes('services')
                )
              ? t(
                  'channels.error.unavailable',
                  'This channel is unavailable or you do not have access.',
                )
              : updatingLabel
                ? t(
                    'channels.edit.labelFailed',
                    'Could not save the label. Check it and try again.',
                  )
                : t(
                    'channels.edit.failed',
                    'Could not save channel changes. Review your choices and try again.',
                  ),
      );
      return;
    } finally {
      inFlight.current = false;
      if (mounted.current) setSubmitting(false);
    }
  }

  const labelError = errors.includes('label')
    ? t(
        'channels.edit.labelError',
        'Enter a non-empty label. If it is too long, shorten it and try again.',
      )
    : undefined;
  const skillError = errors.includes('skill')
    ? t(
        'channels.edit.skillError',
        'Check the skill name. Use no more than 128 characters.',
      )
    : undefined;

  return (
    <>
      <form
        className="channels__connection-form"
        onSubmit={(event) => void save(event)}
        noValidate
        aria-label={title}
      >
        <h2 className="channels__form-section-title">
          {t('channels.edit.configuration', 'Configuration')}
        </h2>
        <div className="channels__field">
          <div className="channels__field-heading">
            <label htmlFor={labelId}>{t('channels.edit.label', 'Label')}</label>
          </div>
          <Input
            id={labelId}
            value={label}
            disabled={submitting}
            aria-invalid={Boolean(labelError)}
            aria-describedby={labelError ? `${labelId}-error` : undefined}
            onChange={(event) => setLabel(event.target.value)}
          />
          {labelError ? (
            <p
              id={`${labelId}-error`}
              className="channels__form-error"
              role="alert"
            >
              {labelError}
            </p>
          ) : null}
        </div>
        <ChannelSkillField
          value={skillName}
          onChange={setSkillName}
          disabled={submitting}
          error={skillError}
        />
        <div className="channels__form-actions">
          <Button
            disabled={submitting}
            onClick={() => {
              if (dirty && !completed.current) setLeaveTarget(detailsHref);
              else history.push(detailsHref);
            }}
          >
            {t('channels.cancel', 'Cancel')}
          </Button>
          <Button
            type="primary"
            htmlType="submit"
            loading={submitting}
            disabled={submitting || completed.current || !dirty}
          >
            {t('channels.edit.save', 'Save changes')}
          </Button>
        </div>
      </form>
      <Modal
        open={leaveTarget !== null}
        title={t('channels.edit.discardTitle', 'Discard your changes?')}
        onCancel={() => setLeaveTarget(null)}
        onOk={() => {
          if (leaveTarget) history.push(leaveTarget);
          setLeaveTarget(null);
        }}
        okText={t('channels.connect.discard', 'Discard')}
        cancelText={t('channels.connect.stay', 'Stay')}
      >
        {t(
          'channels.edit.discardHelp',
          'Your unsaved channel changes will be lost.',
        )}
      </Modal>
    </>
  );
}
