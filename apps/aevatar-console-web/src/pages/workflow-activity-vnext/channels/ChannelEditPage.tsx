import { ArrowLeftOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Checkbox, Modal } from 'antd';
import * as React from 'react';
import {
  type ChannelConfigDetail,
  ChannelConfigError,
  type ChannelConfigField,
  type ChannelConfigUpdate,
  channelConfigMatches,
  channelRuntimeConfigApi,
} from '@/shared/api/channelRuntimeConfigApi';
import type { ChannelServiceChoice } from '@/shared/api/channelServicesApi';
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
import ChannelServicePicker from './ChannelServicePicker';
import ChannelSkillField from './ChannelSkillField';
import { channelConnectionCss } from './connectionStyles';
import { ChannelLoadError, platformName } from './presentation';
import { channelKeys, useChannelServiceChoices } from './queries';
import { channelsCss } from './styles';

export default function ChannelEditPage({
  scopeId,
  registrationId,
}: {
  readonly scopeId: string;
  readonly registrationId: string;
}) {
  const [initial, setInitial] = React.useState<ChannelConfigDetail | null>(
    null,
  );
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
  React.useEffect(() => {
    if (
      !initial &&
      config.isSuccess &&
      config.isFetchedAfterMount &&
      !config.isFetching
    )
      setInitial(config.data);
  }, [
    initial,
    config.data,
    config.isSuccess,
    config.isFetchedAfterMount,
    config.isFetching,
  ]);
  const title = initial
    ? t('channels.edit.title', 'Edit {platform}', {
        platform: platformName(initial.platform),
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
      {!initial && !config.isError ? (
        <AevatarContentSkeleton
          ariaLabel={t(
            'channels.edit.loading',
            'Loading channel configuration',
          )}
          variant="list"
          rows={4}
        />
      ) : !initial && config.isError ? (
        <ChannelLoadError
          error={config.error}
          pending={config.isFetching}
          retry={() => void config.refetch()}
        />
      ) : initial ? (
        <ChannelEditForm
          key={`${scopeId}:${registrationId}`}
          initial={initial}
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
  title,
  setNavigate,
  readBack,
}: {
  readonly initial: ChannelConfigDetail;
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
  const [selectedIds, setSelectedIds] = React.useState([
    ...baseline.serviceIds,
  ]);
  const [authorizationMode, setAuthorizationMode] = React.useState(
    baseline.authorizationMode,
  );
  const [errors, setErrors] = React.useState<readonly ChannelConfigField[]>([]);
  const [submitting, setSubmitting] = React.useState(false);
  const [leaveTarget, setLeaveTarget] = React.useState<string | null>(null);
  const inFlight = React.useRef(false);
  const mounted = React.useRef(true);
  const completed = React.useRef(false);
  const toast = useConsoleToast();
  const queryClient = useQueryClient();
  const services = useChannelServiceChoices(baseline.scopeId);
  const detailsHref = buildChannelDetailsHref(
    baseline.scopeId,
    baseline.registrationId,
  );
  const usesDefaults = authorizationMode === 'nyxid_default';
  const selectionChanged =
    authorizationMode !== baseline.authorizationMode ||
    JSON.stringify([...selectedIds].sort()) !==
      JSON.stringify([...baseline.serviceIds].sort());
  const missingIds = selectedIds.filter(
    (id) => !services.data?.some((service) => service.id === id),
  );
  const choices: readonly ChannelServiceChoice[] = [
    ...(services.data ?? []),
    ...missingIds.map(
      (id): ChannelServiceChoice => ({
        id,
        slug: id,
        label: t('channels.edit.unavailableService', 'Unavailable service'),
        active: false,
        allowed: false,
        source: 'unknown',
        organizationName: null,
      }),
    ),
  ];
  const selectedSlugs = new Set(
    services.data
      ?.filter((service) => selectedIds.includes(service.id))
      .map((service) => service.slug),
  );
  const update: ChannelConfigUpdate = {
    authorizationMode,
    serviceIds: usesDefaults ? [] : selectedIds,
    runtimeConfig: {
      ...baseline.runtimeConfig,
      defaultSkill: {
        name: skillName,
        version: skillName.trim()
          ? baseline.runtimeConfig.defaultSkill.version
          : '',
      },
      serviceSelectors: selectionChanged
        ? baseline.runtimeConfig.serviceSelectors.filter(
            (selector) =>
              !usesDefaults && selectedSlugs.has(selector.serviceSlug),
          )
        : baseline.runtimeConfig.serviceSelectors,
    },
  };
  const dirty = !channelConfigMatches(update, baseline);
  const serviceBlocked =
    !usesDefaults &&
    (!services.isSuccess || services.isFetching || missingIds.length > 0);

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

  async function finishSave(expected: ChannelConfigUpdate) {
    let confirmed = false;
    let readFailed = false;
    try {
      const actual = await readBack();
      confirmed =
        actual.stateVersion > baseline.stateVersion &&
        channelConfigMatches(actual, expected);
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
    if (
      inFlight.current ||
      submitting ||
      !dirty ||
      serviceBlocked ||
      completed.current
    )
      return;
    const invalid: ChannelConfigField[] = [];
    if (skillName.trim().length > 128) invalid.push('skill');
    setErrors(invalid);
    if (invalid.length) return;
    inFlight.current = true;
    setSubmitting(true);
    try {
      await channelRuntimeConfigApi.update(baseline.registrationId, update);
      if (!mounted.current) return;
      // Read once for truthful feedback, then leave the editor even if the
      // accepted change is not visible yet. Never poll or resubmit to confirm.
      await finishSave(update);
    } catch (error) {
      if (!mounted.current) return;
      if (error instanceof ChannelConfigError) {
        setErrors(error.fields);
        if (error.fields.includes('services')) void services.refetch();
      }
      toast.error(
        error instanceof ChannelApiError && [401, 403].includes(error.status)
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

  const errorText = (field: 'skill' | 'services') =>
    errors.includes(field)
      ? field === 'skill'
        ? t(
            'channels.edit.skillError',
            'Check the skill name. Use no more than 128 characters.',
          )
        : t(
            'channels.edit.selectionError',
            'Review your selected services and try again.',
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
        <ChannelSkillField
          value={skillName}
          onChange={setSkillName}
          disabled={submitting}
          error={errorText('skill')}
        />
        {baseline.authorizationMode === 'nyxid_default' ? (
          <div className="channels__field">
            <Checkbox
              checked={usesDefaults}
              disabled={submitting}
              onChange={(event) => {
                setAuthorizationMode(
                  event.target.checked
                    ? 'nyxid_default'
                    : 'explicit_service_allowlist',
                );
                setSelectedIds([]);
              }}
            >
              {t('channels.edit.useDefaults', 'Use NyxID defaults')}
            </Checkbox>
          </div>
        ) : null}
        <ChannelServicePicker
          services={choices}
          selectedIds={selectedIds}
          onChange={setSelectedIds}
          loading={services.isPending}
          failed={services.isError}
          refreshing={services.isFetching}
          disabled={submitting || usesDefaults}
          retry={() => void services.refetch()}
          editing
          usesDefaults={usesDefaults}
        />
        {missingIds.length > 0 && services.isSuccess && !submitting ? (
          <p className="channels__form-error" role="alert">
            {t(
              'channels.edit.missingServices',
              'Some saved services are no longer available. Deselect them before saving.',
            )}
          </p>
        ) : null}
        {errorText('services') ? (
          <p className="channels__form-error" role="alert">
            {errorText('services')}
          </p>
        ) : null}
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
            disabled={
              submitting || completed.current || !dirty || serviceBlocked
            }
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
