import { ArrowLeftOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Input, Modal } from 'antd';
import * as React from 'react';
import {
  isValidChannelBotLabel,
  updateChannelBotLabel,
} from '@/shared/api/channelBotsApi';
import { getChannelSkillById } from '@/shared/api/channelSkillsApi';
import {
  ChannelApiError,
  type ChannelConfiguration,
  type ChannelReceipt,
  type ChannelRegistration,
  ChannelRegistrationError,
  channelConfigMatches,
  channelConfiguration,
  channelsApi,
} from '@/shared/api/channelsApi';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { AevatarContentSkeleton } from '@/shared/ui/AevatarContentSkeleton';
import { AevatarLoadingDots } from '@/shared/ui/AevatarLoading';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import {
  buildChannelDetailsHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import ChannelServiceAccessNotice from './ChannelServiceAccessNotice';
import ChannelServicePicker from './ChannelServicePicker';
import ChannelSkillField from './ChannelSkillField';
import { channelConnectionCss } from './connectionStyles';
import {
  ChannelBadge,
  ChannelIdentity,
  ChannelLoadError,
  InboundStatus,
} from './presentation';
import {
  channelKeys,
  useChannelDetail,
  useChannelRegistrations,
  useChannelServiceAccess,
} from './queries';
import {
  type ChannelAccessDraftTarget,
  clearChannelAccessDraft,
  readChannelAccessDraft,
  saveChannelAccessDraft,
} from './serviceAccessDraft';
import { channelsCss } from './styles';

type Target =
  | {
      readonly botId: string;
      readonly registrationId?: never;
      readonly defaultSkillId?: string;
    }
  | {
      readonly registrationId: string;
      readonly botId?: never;
      readonly defaultSkillId?: never;
    };

const requiredServiceSlugs = ['ornn-api', 'chrono-llm-public'] as const;

export default function ChannelConfigurationPage({
  scopeId,
  requestedServiceIds = [],
  ...target
}: Target & {
  readonly scopeId: string;
  readonly requestedServiceIds?: readonly string[];
}) {
  const editing = Boolean(target.registrationId);
  const draftTarget = React.useMemo<ChannelAccessDraftTarget>(
    () =>
      target.botId !== undefined
        ? { kind: 'bind', botId: target.botId }
        : { kind: 'edit', registrationId: target.registrationId },
    [target.botId, target.registrationId],
  );
  const list = useChannelRegistrations(scopeId, !editing);
  const detail = useChannelDetail(scopeId, target.registrationId ?? '');
  const query = editing ? detail : list;
  const row = editing
    ? detail.data
    : list.data?.find((item) => item.botId === target.botId);
  const [initial, setInitial] = React.useState<ChannelRegistration | null>(
    null,
  );
  const [navigate, setNavigate] = React.useState<(target: string) => void>(
    () => history.push,
  );
  const valid =
    row?.owned &&
    (editing
      ? row.bindingStatus === 'bound' && channelConfiguration(row)
      : row.bindingStatus === 'unbound' &&
        row.availabilityStatus === 'available');
  React.useEffect(() => {
    if (
      !initial &&
      query.isFetchedAfterMount &&
      query.isSuccess &&
      !query.isFetching &&
      valid &&
      row
    )
      setInitial(row);
  }, [
    initial,
    query.isFetchedAfterMount,
    query.isSuccess,
    query.isFetching,
    valid,
    row,
  ]);
  const title = editing
    ? t('channels.edit.loadingTitle', 'Edit channel')
    : t('channels.bind.title', 'Bind bot');
  const listHref = buildWorkflowActivitySectionHref(scopeId, 'channels');
  const error =
    query.error ??
    (query.isSuccess && !valid ? new ChannelApiError(404) : null);
  return (
    <WorkflowActivityVNextShell
      activeSection="channels"
      scopeId={scopeId}
      title={title}
      onNavigate={navigate}
      mainClassName="channels__main channels__main--connect"
      contentClassName="channels__content"
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
        {!editing ? (
          <p>
            {t(
              'channels.bind.description',
              'Choose a skill and the services this bot can use.',
            )}
          </p>
        ) : null}
      </div>
      {initial ? (
        <ConfigurationForm
          scopeId={scopeId}
          initial={initial}
          draftTarget={draftTarget}
          defaultSkillId={target.defaultSkillId}
          requestedServiceIds={requestedServiceIds}
          setNavigate={setNavigate}
        />
      ) : error && !query.isFetching ? (
        <ChannelLoadError
          error={error}
          pending={query.isFetching}
          retry={() => void query.refetch()}
        />
      ) : (
        <AevatarContentSkeleton
          ariaLabel={t(
            'channels.edit.loading',
            'Loading channel configuration',
          )}
          variant="list"
          rows={4}
        />
      )}
    </WorkflowActivityVNextShell>
  );
}

function ConfigurationForm({
  scopeId,
  initial,
  draftTarget,
  defaultSkillId,
  requestedServiceIds,
  setNavigate,
}: {
  readonly scopeId: string;
  readonly initial: ChannelRegistration;
  readonly draftTarget: ChannelAccessDraftTarget;
  readonly defaultSkillId?: string;
  readonly requestedServiceIds: readonly string[];
  readonly setNavigate: React.Dispatch<
    React.SetStateAction<(target: string) => void>
  >;
}) {
  const editing = draftTarget.kind === 'edit';
  const baseline = channelConfiguration(initial);
  const [restoredDraft] = React.useState(() =>
    readChannelAccessDraft(scopeId, draftTarget),
  );
  const [reviewPending, setReviewPending] = React.useState(false);
  const reviewLeaving = React.useRef(false);
  const reviewLock = React.useRef(false);
  const requestedIds = [
    ...new Set(
      requestedServiceIds
        .map((id) => id.trim())
        .filter((id) => id && id.length <= 128),
    ),
  ].slice(0, 20);
  const [initialSkillId] = React.useState(editing ? undefined : defaultSkillId);
  // Undefined means the link's default has not been resolved or overridden yet.
  const [skillName, setSkillName] = React.useState<string | undefined>(
    () =>
      restoredDraft?.skillName ??
      (initialSkillId ? undefined : (initial.skill?.name ?? '')),
  );
  const skillReady = skillName !== undefined;
  const defaultSkill = useQuery({
    queryKey: channelKeys.skill(scopeId, initialSkillId ?? ''),
    queryFn: ({ signal }) => getChannelSkillById(initialSkillId ?? '', signal),
    enabled: Boolean(initialSkillId) && !skillReady,
    retry: false,
    refetchOnMount: 'always',
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  });
  React.useEffect(() => {
    if (
      !skillReady &&
      defaultSkill.isFetchedAfterMount &&
      defaultSkill.isSuccess &&
      !defaultSkill.isFetching
    )
      setSkillName(defaultSkill.data.name);
  }, [
    skillReady,
    defaultSkill.isFetchedAfterMount,
    defaultSkill.isSuccess,
    defaultSkill.isFetching,
    defaultSkill.data,
  ]);
  const [chosenServiceIds, setServiceIds] = React.useState<readonly string[]>(
    restoredDraft?.serviceIds ?? baseline?.serviceIds ?? [],
  );
  const [label, setLabel] = React.useState(
    restoredDraft?.label ?? initial.label ?? '',
  );
  const [savedLabel, setSavedLabel] = React.useState(initial.label ?? '');
  const [receipt, setReceipt] = React.useState<ChannelReceipt | null>(null);
  const [submitted, setSubmitted] = React.useState<ChannelConfiguration | null>(
    null,
  );
  const [submitting, setSubmitting] = React.useState(false);
  const [uncertain, setUncertain] = React.useState(false);
  const [delayed, setDelayed] = React.useState(false);
  const [failure, setFailure] = React.useState<string | null>(null);
  const [leaveTarget, setLeaveTarget] = React.useState<string | null>(null);
  const lock = React.useRef(false);
  const completed = React.useRef(false);
  const mounted = React.useRef(true);
  const labelId = React.useId();
  const services = useChannelServiceAccess(scopeId);
  React.useEffect(() => {
    if (restoredDraft) clearChannelAccessDraft(scopeId, draftTarget);
  }, [restoredDraft, scopeId, draftTarget]);
  React.useEffect(() => {
    const resume = (event: PageTransitionEvent) => {
      if (!event.persisted || !reviewLeaving.current) return;
      reviewLeaving.current = false;
      reviewLock.current = false;
      setReviewPending(false);
      clearChannelAccessDraft(scopeId, draftTarget);
      void services.refetch();
    };
    window.addEventListener('pageshow', resume);
    return () => window.removeEventListener('pageshow', resume);
  }, [services.refetch, scopeId, draftTarget]);
  const availableServices = (services.data ?? []).filter(
    (service) => service.active && service.allowed,
  );
  const hasFreshServiceAccess =
    services.isFetchedAfterMount && services.isSuccess && !services.isFetching;
  React.useEffect(() => {
    if (!hasFreshServiceAccess) return;
    // Only confirmed access changes remove draft choices. A failed or pending
    // refresh must not erase them, and later reauthorization must not reselect them.
    const availableIds = new Set(
      services.data
        ?.filter((service) => service.active && service.allowed)
        .map((service) => service.id),
    );
    setServiceIds((selected) => {
      const remaining = selected.filter((id) => availableIds.has(id));
      return remaining.length === selected.length ? selected : remaining;
    });
  }, [hasFreshServiceAccess, services.data]);
  const requiredServices = availableServices.filter((service) =>
    requiredServiceSlugs.some((slug) => service.slug === slug),
  );
  const requiredIds = requiredServices.map((service) => service.id);
  const missingRequiredSlugs = requiredServiceSlugs.filter(
    (slug) => !requiredServices.some((service) => service.slug === slug),
  );
  const serviceIds = [
    ...new Set([
      ...chosenServiceIds.filter(
        (id) =>
          !hasFreshServiceAccess ||
          availableServices.some((service) => service.id === id),
      ),
      ...requiredIds,
    ]),
  ];
  const servicesReady =
    hasFreshServiceAccess && missingRequiredSlugs.length === 0;
  const toast = useConsoleToast();
  const client = useQueryClient();
  const listHref = buildWorkflowActivitySectionHref(scopeId, 'channels');
  const returnHref =
    editing && initial.id
      ? buildChannelDetailsHref(scopeId, initial.id)
      : listHref;
  const config: ChannelConfiguration = {
    skillName: skillName ?? '',
    serviceIds,
    authorizationMode: 'explicit_service_allowlist',
  };
  const configDirty = !editing || !channelConfigMatches(initial, config);
  const labelDirty = editing && label.trim() !== savedLabel;
  const dirty = editing
    ? configDirty || labelDirty
    : Boolean(skillName || chosenServiceIds.length);
  const busy = submitting || Boolean(receipt) || uncertain || reviewPending;
  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  React.useEffect(() => {
    if (!receipt) return;
    const timer = window.setTimeout(() => setDelayed(true), 30_000);
    return () => window.clearTimeout(timer);
  }, [receipt]);
  React.useEffect(() => {
    setNavigate(() => (href: string) => {
      if (lock.current) return;
      if (dirty && !receipt && !uncertain && !completed.current)
        setLeaveTarget(href);
      else history.push(href);
    });
  }, [dirty, receipt, uncertain, setNavigate]);
  React.useEffect(() => {
    if (!dirty || receipt || uncertain) return;
    const warn = (event: BeforeUnloadEvent) => {
      if (reviewLeaving.current) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty, receipt, uncertain]);
  const observation = useQuery({
    queryKey: ['channels', scopeId, 'confirmation', receipt?.commandId],
    enabled: Boolean(receipt),
    queryFn: async ({ signal }) => {
      if (!receipt) return null;
      if (editing) {
        try {
          return await channelsApi.get(receipt.registrationId, signal);
        } catch (error) {
          if (error instanceof ChannelApiError && error.status === 404)
            return null;
          throw error;
        }
      }
      const inventory = await channelsApi.list(signal);
      client.setQueryData(channelKeys.list(scopeId), inventory);
      return (
        inventory.find(
          (row) =>
            row.id === receipt.registrationId && row.botId === initial.botId,
        ) ?? null
      );
    },
    retry: false,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: (query) =>
      completed.current || delayed || query.state.error ? false : 1500,
  });
  React.useEffect(() => {
    const actual = observation.data;
    if (
      !actual ||
      !actual.id ||
      !submitted ||
      completed.current ||
      actual.bindingStatus !== 'bound' ||
      actual.botId !== initial.botId ||
      !channelConfigMatches(actual, submitted)
    )
      return;
    if (
      editing &&
      (initial.stateVersion === null ||
        actual.stateVersion === null ||
        actual.stateVersion <= initial.stateVersion)
    )
      return;
    completed.current = true;
    client.setQueryData(channelKeys.detail(scopeId, actual.id), actual);
    void client.invalidateQueries({ queryKey: channelKeys.list(scopeId) });
    clearChannelAccessDraft(scopeId, draftTarget);
    toast.success(
      editing
        ? t('channels.edit.saved', 'Channel changes saved.')
        : t('channels.bind.success', 'Bot bound successfully.'),
    );
    history.replace(buildChannelDetailsHref(scopeId, actual.id));
  }, [
    observation.data,
    submitted,
    initial,
    editing,
    client,
    scopeId,
    toast,
    draftTarget,
  ]);

  async function reviewServiceAccess() {
    if (busy || reviewLock.current || !skillReady) return;
    reviewLock.current = true;
    setReviewPending(true);
    try {
      // Persist before navigation. If storage fails, keep the editor open.
      saveChannelAccessDraft(scopeId, draftTarget, {
        label,
        skillName: skillName ?? '',
        serviceIds,
      });
      reviewLeaving.current = true;
      await new NyxIDAuthClient(getNyxIDRuntimeConfig()).loginWithRedirect({
        flow: 'serviceAccessReview',
        returnTo: `${window.location.pathname}${window.location.search}${window.location.hash}`,
      });
    } catch {
      reviewLeaving.current = false;
      reviewLock.current = false;
      setReviewPending(false);
      clearChannelAccessDraft(scopeId, draftTarget);
      toast.error(
        t(
          'channels.access.startFailed',
          'Could not open NyxID. Your changes are still here. Try again.',
        ),
      );
    }
  }

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (
      lock.current ||
      busy ||
      completed.current ||
      !servicesReady ||
      !skillReady ||
      (editing && !dirty)
    )
      return;
    setFailure(null);
    if (labelDirty && !isValidChannelBotLabel(label)) {
      setFailure(
        t(
          'channels.edit.labelError',
          'Enter a non-empty label. If it is too long, shorten it and try again.',
        ),
      );
      return;
    }
    if (skillName.trim().length > 128) {
      setFailure(
        t(
          'channels.edit.skillError',
          'Check the skill name. Use no more than 128 characters.',
        ),
      );
      return;
    }
    lock.current = true;
    setSubmitting(true);
    let labelSaved = false;
    try {
      if (labelDirty) {
        const updated = await updateChannelBotLabel(
          { id: initial.botId, platform: initial.platform, label: savedLabel },
          label,
        );
        if (!mounted.current) return;
        setSavedLabel(updated.label ?? '');
        labelSaved = true;
        void client.invalidateQueries({ queryKey: channelKeys.list(scopeId) });
      }
      if (configDirty) {
        const result =
          editing && initial.id
            ? await channelsApi.update(initial.id, config)
            : await channelsApi.adopt(initial.botId, config);
        if (!mounted.current) return;
        setSubmitted(config);
        setReceipt(result);
      } else {
        completed.current = true;
        void client.invalidateQueries({
          queryKey: channelKeys.detail(scopeId, initial.id ?? ''),
        });
        clearChannelAccessDraft(scopeId, draftTarget);
        toast.success(t('channels.edit.saved', 'Channel changes saved.'));
        history.replace(returnHref);
      }
    } catch (error) {
      if (!mounted.current) return;
      if (
        error instanceof ChannelRegistrationError &&
        error.reason === 'uncertain'
      )
        setUncertain(true);
      const message =
        error instanceof ChannelRegistrationError &&
        error.reason === 'configuration'
          ? t(
              'channels.bind.configuration',
              'Bot binding is unavailable because the server callback address is not configured correctly. Contact your administrator.',
            )
          : error instanceof ChannelRegistrationError &&
              error.reason === 'conflict'
            ? t(
                'channels.bind.conflict',
                'This bot could not be bound. Refresh the channel list and review its binding and routes in NyxID.',
              )
            : error instanceof ChannelRegistrationError &&
                error.reason === 'uncertain'
              ? t(
                  'channels.bind.uncertain',
                  'The request could not be confirmed. Return to Channels and refresh before trying again.',
                )
              : labelSaved
                ? t(
                    'channels.edit.partialConfigSave',
                    'Label saved, but the configuration could not be updated. Review your choices and try again.',
                  )
                : t(
                    'channels.bind.failed',
                    'Could not save this bot. Check your skill, services and NyxID access, then try again.',
                  );
      setFailure(message);
      toast.error(message);
    } finally {
      lock.current = false;
      if (mounted.current) setSubmitting(false);
    }
  }

  return (
    <>
      <form
        className="channels__connection-form"
        onSubmit={(event) => void save(event)}
        aria-label={
          editing
            ? t('channels.edit.loadingTitle', 'Edit channel')
            : t('channels.bind.title', 'Bind bot')
        }
      >
        <div className="channels__bot-summary">
          <ChannelIdentity
            registration={initial}
            label={savedLabel || initial.label}
            pending={false}
            secondary="platform"
          />
          {editing ? (
            <InboundStatus value={initial.nyxStatus ?? undefined} />
          ) : (
            <ChannelBadge>
              {t('channels.binding.unbound', 'Not bound')}
            </ChannelBadge>
          )}
        </div>
        {editing ? (
          <div className="channels__field">
            <div className="channels__field-heading">
              <label htmlFor={labelId}>
                {t('channels.edit.label', 'Label')}
              </label>
            </div>
            <Input
              id={labelId}
              value={label}
              onChange={(event) => setLabel(event.target.value)}
              disabled={busy}
            />
          </div>
        ) : null}
        <ChannelSkillField
          scopeId={scopeId}
          value={skillName ?? ''}
          onChange={setSkillName}
          disabled={busy}
          error={
            !skillReady && defaultSkill.isError
              ? t(
                  'channels.skills.defaultFailed',
                  'Could not load the linked skill. Retry, choose another skill, or continue without one.',
                )
              : undefined
          }
        />
        {!skillReady ? (
          <div className="channels__field">
            {!defaultSkill.isError ? (
              <p role="status">
                <AevatarLoadingDots decorative />{' '}
                {t('channels.skills.defaultLoading', 'Loading linked skill...')}
              </p>
            ) : (
              <Button
                type="text"
                disabled={defaultSkill.isFetching}
                loading={defaultSkill.isFetching}
                onClick={() => void defaultSkill.refetch()}
              >
                {t('channels.skills.defaultRetry', 'Retry linked skill')}
              </Button>
            )}
            <Button type="text" onClick={() => setSkillName('')}>
              {t('channels.skills.defaultSkip', 'Continue without a skill')}
            </Button>
          </div>
        ) : null}
        <ChannelServicePicker
          services={availableServices}
          accessAction={{
            onReview: () => void reviewServiceAccess(),
            pending: reviewPending,
            disabled: busy || !skillReady,
          }}
          accessNotice={
            services.isFetchedAfterMount &&
            !services.isFetching &&
            !services.isError ? (
              <ChannelServiceAccessNotice
                requestedIds={requestedIds}
                services={services.data ?? []}
                restored={Boolean(restoredDraft)}
              />
            ) : undefined
          }
          suggestedIds={requestedIds.filter((id) =>
            availableServices.some((service) => service.id === id),
          )}
          selectedIds={serviceIds}
          requiredIds={requiredIds}
          missingRequiredSlugs={missingRequiredSlugs}
          onChange={setServiceIds}
          loading={services.isPending}
          failed={services.isError}
          refreshing={services.isFetching}
          disabled={busy}
          retry={() => void services.refetch()}
          editing={editing}
          replacesDefaults={baseline?.authorizationMode === 'nyxid_default'}
        />
        {failure ? (
          <p role="alert" className="channels__form-error">
            {failure}
          </p>
        ) : null}
        {receipt ? (
          <div role="status" className="channels__connection-status">
            {delayed || observation.isError ? (
              t(
                'channels.bind.delayed',
                'Your request was accepted. The updated binding is not visible yet. You can return to Channels and refresh the table.',
              )
            ) : (
              <>
                <AevatarLoadingDots decorative />{' '}
                {t('channels.bind.confirming', 'Confirming your changes...')}
              </>
            )}
          </div>
        ) : null}
        <div className="channels__form-actions">
          <Button
            disabled={submitting}
            onClick={() =>
              dirty && !receipt && !uncertain
                ? setLeaveTarget(returnHref)
                : history.push(receipt || uncertain ? listHref : returnHref)
            }
          >
            {receipt || uncertain
              ? t('channels.back', 'Back to channels')
              : t('channels.cancel', 'Cancel')}
          </Button>
          <Button
            type="primary"
            htmlType="submit"
            loading={
              submitting ||
              (Boolean(receipt) && !delayed && !observation.isError)
            }
            disabled={
              busy || (editing && !dirty) || !servicesReady || !skillReady
            }
          >
            {editing
              ? t('channels.edit.save', 'Save changes')
              : t('channels.bind.title', 'Bind bot')}
          </Button>
        </div>
      </form>
      <Modal
        open={leaveTarget !== null}
        title={t('channels.edit.discardTitle', 'Discard your changes?')}
        onCancel={() => setLeaveTarget(null)}
        onOk={() => {
          if (leaveTarget) {
            clearChannelAccessDraft(scopeId, draftTarget);
            history.push(leaveTarget);
          }
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
