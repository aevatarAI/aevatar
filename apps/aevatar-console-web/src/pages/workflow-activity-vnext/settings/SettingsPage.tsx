import { ReloadOutlined, SaveOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import {
  Alert,
  Button,
  Descriptions,
  Modal,
  Select,
  Space,
  Typography,
} from 'antd';
import React from 'react';
import { observeUserLlmSave } from '@/pages/settings/userLlmSaveObservation';
import {
  buildUserLlmSelectionOptions,
  cloneUserLlmSelection,
  decodeUserLlmSelectionValue,
  encodeUserLlmSelectionValue,
  resolveSavedUserLlmSelection,
  type UserLlmSelectionDraft,
  userLlmSelectionsEqual,
} from '@/pages/settings/userLlmSelection';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useWorkflowActivityAccount } from '../account/useWorkflowActivityAccount';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import AccountPanel from './AccountPanel';
import { buildAccountIdentity } from './accountIdentity';

type SettingsSection = 'ai' | 'account' | 'advanced';
type SavePhase =
  | 'idle'
  | 'saving'
  | 'accepted'
  | 'observed'
  | 'delayed'
  | 'failed';

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function readSection(search: string): SettingsSection {
  const value = new URLSearchParams(search).get('section');
  return value === 'account' || value === 'advanced' ? value : 'ai';
}

function settingsSectionHref(
  pathname: string,
  section: SettingsSection,
): string {
  return section === 'ai' ? pathname : `${pathname}?section=${section}`;
}

function SettingsLoadingState({ message }: { readonly message: string }) {
  return (
    <div
      aria-live="polite"
      className="wa-vnext__state wa-vnext__state--compact"
    >
      <p>{message}</p>
    </div>
  );
}

function SettingsErrorState({
  error,
  onRetry,
  title,
}: {
  readonly error: unknown;
  readonly onRetry: () => void;
  readonly title: string;
}) {
  return (
    <div className="wa-vnext__state wa-vnext__state--compact" role="alert">
      <div>
        <h3>{title}</h3>
        <p>
          {t(
            'workflowActivityVNext.settings.retryGuidance',
            'Try loading this section again.',
          )}
        </p>
        <Button icon={<ReloadOutlined />} onClick={onRetry}>
          {t('workflowActivityVNext.common.retry', 'Retry')}
        </Button>
        <TechnicalDetails>{errorMessage(error)}</TechnicalDetails>
      </div>
    </div>
  );
}

const SettingsPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const queryClient = useQueryClient();
  const toast = useConsoleToast();
  const [section, setSection] = React.useState<SettingsSection>(() =>
    readSection(location.search),
  );
  const llm = useQuery({
    queryKey: ['workflow-activity-vnext', 'settings', 'llm'],
    queryFn: ({ signal }) => studioApi.getUserLlmSettings(signal),
    retry: false,
  });
  const { auth: resolvedAuth, query: auth } = useWorkflowActivityAccount();
  const runtime = useQuery({
    queryKey: ['workflow-activity-vnext', 'settings', 'runtime'],
    queryFn: () => studioApi.getUserConfigRuntime(),
    retry: false,
  });
  const accountIdentity = React.useMemo(
    () =>
      resolvedAuth
        ? buildAccountIdentity(resolvedAuth, Date.now(), getLocale())
        : null,
    [resolvedAuth],
  );
  const [draft, setDraft] = React.useState<UserLlmSelectionDraft | undefined>(
    undefined,
  );
  const [baseline, setBaseline] = React.useState<
    UserLlmSelectionDraft | undefined
  >(undefined);
  const [savePhase, setSavePhase] = React.useState<SavePhase>('idle');
  const [saveMessage, setSaveMessage] = React.useState('');
  const [pendingNavigation, setPendingNavigation] = React.useState('');
  const saveTokenRef = React.useRef(0);
  const loadedRef = React.useRef(false);

  React.useEffect(() => {
    if (!llm.data || loadedRef.current) return;
    const saved = resolveSavedUserLlmSelection(llm.data);
    setDraft(saved);
    setBaseline(saved);
    loadedRef.current = true;
  }, [llm.data]);
  React.useEffect(
    () => () => {
      saveTokenRef.current += 1;
    },
    [],
  );

  const options = React.useMemo(
    () =>
      buildUserLlmSelectionOptions(llm.data?.routeOptions ?? []).filter(
        (option) => option.modelCatalog.modelIds.length > 0,
      ),
    [llm.data?.routeOptions],
  );
  const selectedOption = draft
    ? options.find((item) => item.value === encodeUserLlmSelectionValue(draft))
    : undefined;
  const modelIds = selectedOption?.modelCatalog.modelIds ?? [];
  const modelValue =
    draft?.modelSelection.kind === 'explicit_model'
      ? draft.modelSelection.modelId
      : 'provider-default';
  const dirty = !userLlmSelectionsEqual(draft, baseline);
  const encodedBaseline = baseline ? encodeUserLlmSelectionValue(baseline) : '';
  const savedSelectionUnavailable = Boolean(
    baseline && !options.some((item) => item.value === encodedBaseline),
  );
  const unavailableSavedModel =
    draft?.modelSelection.kind === 'explicit_model' &&
    !modelIds.includes(draft.modelSelection.modelId)
      ? draft.modelSelection.modelId
      : '';

  React.useEffect(() => {
    const warn = (event: BeforeUnloadEvent) => {
      if (!dirty) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  const selectRoute = (value: string) => {
    if (!value) {
      setDraft(undefined);
      return;
    }
    const next = decodeUserLlmSelectionValue(value, options);
    if (next) setDraft(cloneUserLlmSelection(next));
  };
  const selectModel = (value: string) => {
    if (!draft) return;
    setDraft({
      ...draft,
      modelSelection:
        value === 'provider-default'
          ? { kind: 'provider_default' }
          : { kind: 'explicit_model', modelId: value },
    } as UserLlmSelectionDraft);
  };

  const save = async (): Promise<boolean> => {
    if (!dirty || savePhase === 'saving' || savePhase === 'accepted')
      return false;
    const submitted = draft ? cloneUserLlmSelection(draft) : undefined;
    const token = ++saveTokenRef.current;
    setSavePhase('saving');
    setSaveMessage('');
    try {
      const receipt = await studioApi.saveUserLlmSettings(
        !submitted
          ? { action: 'reset' }
          : submitted.routeKind === 'gateway'
            ? {
                action: 'select_gateway',
                gateway: { model: submitted.modelSelection },
              }
            : {
                action: 'select_user_service',
                userService: {
                  userServiceId: submitted.nyxIdUserServiceId,
                  model: submitted.modelSelection,
                },
              },
      );
      if (!receipt.accepted)
        throw new Error(
          t(
            'workflowActivityVNext.settings.notAccepted',
            'The settings update was not accepted.',
          ),
        );
      setSavePhase('accepted');
      setSaveMessage('');
      const observation = await observeUserLlmSave({
        saveToken: token,
        isCurrent: (candidate) => candidate === saveTokenRef.current,
        read: (signal) => studioApi.getUserLlmSettings(signal),
        isObserved: (sample) =>
          userLlmSelectionsEqual(
            resolveSavedUserLlmSelection(sample),
            submitted,
          ),
        onResponse: (sample) =>
          queryClient.setQueryData(
            ['workflow-activity-vnext', 'settings', 'llm'],
            sample,
          ),
      });
      if (token !== saveTokenRef.current) return false;
      if (observation.phase === 'observed') {
        setBaseline(submitted ? cloneUserLlmSelection(submitted) : undefined);
        setSavePhase('observed');
        toast.success(
          t('workflowActivityVNext.settings.saveObserved', 'Settings saved'),
        );
        return true;
      } else if (observation.phase === 'accepted_unobserved') {
        setSavePhase('delayed');
      }
    } catch (error) {
      if (token === saveTokenRef.current) {
        setSaveMessage(errorMessage(error));
        setSavePhase('failed');
      }
    }
    return false;
  };

  const discard = () => {
    saveTokenRef.current += 1;
    setDraft(baseline ? cloneUserLlmSelection(baseline) : undefined);
    setSavePhase('idle');
    setSaveMessage('');
  };

  const finishNavigation = (target: string) => {
    const targetUrl = new URL(target, 'http://console.local');
    if (targetUrl.pathname === location.pathname) {
      setSection(readSection(targetUrl.search));
      history.replace(`${targetUrl.pathname}${targetUrl.search}`);
      return;
    }
    history.push(target);
  };

  const requestNavigation = (target: string) => {
    if (dirty) setPendingNavigation(target);
    else finishNavigation(target);
  };

  const discardAndLeave = () => {
    const target = pendingNavigation;
    discard();
    setPendingNavigation('');
    if (target) finishNavigation(target);
  };

  const saveAndLeave = async () => {
    if (!(await save())) return;
    const target = pendingNavigation;
    setPendingNavigation('');
    if (target) finishNavigation(target);
  };

  const aiPanel = llm.isPending ? (
    <SettingsLoadingState
      message={t(
        'workflowActivityVNext.settings.llmLoading',
        'Loading AI defaults',
      )}
    />
  ) : llm.isError ? (
    <SettingsErrorState
      error={llm.error}
      onRetry={() => void llm.refetch()}
      title={t(
        'workflowActivityVNext.settings.llmUnavailable',
        'AI defaults unavailable',
      )}
    />
  ) : (
    <div className="wa-vnext__form">
      {llm.data?.catalogStatus === 'unavailable' ? (
        <Alert
          action={
            <Button onClick={() => void llm.refetch()} size="small">
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          description={t(
            'workflowActivityVNext.settings.catalogUnavailable',
            'Your saved choice is unchanged. Try again to load available services.',
          )}
          message={t(
            'workflowActivityVNext.settings.catalogUnavailableTitle',
            'Services unavailable',
          )}
          showIcon
          type="warning"
        />
      ) : null}
      <div className="wa-vnext__settings-fields">
        <div className="wa-vnext__settings-field">
          <div className="wa-vnext__settings-field-copy">
            <strong>
              {t(
                'workflowActivityVNext.settings.preferredService',
                'Preferred service',
              )}
            </strong>
            <span>
              {t(
                'workflowActivityVNext.settings.preferredServiceHelp',
                'Choose which service new sessions use.',
              )}
            </span>
          </div>
          <Select
            aria-label={t(
              'workflowActivityVNext.settings.preferredService',
              'Preferred service',
            )}
            disabled={!llm.data?.capabilities.canEditRoute}
            onChange={selectRoute}
            options={[
              ...options.map((item) => ({
                disabled: !item.allowed || !item.ready,
                label: item.label,
                value: item.value,
              })),
              ...(savedSelectionUnavailable && baseline
                ? [
                    {
                      disabled: true,
                      label: `${llm.data?.savedRouteLabel || baseline.routeValue} (${t(
                        'workflowActivityVNext.common.unavailable',
                        'Unavailable',
                      )})`,
                      value: encodedBaseline,
                    },
                  ]
                : []),
            ]}
            value={draft ? encodeUserLlmSelectionValue(draft) : undefined}
          />
        </div>
        {draft && modelIds.length > 0 ? (
          <div className="wa-vnext__settings-field">
            <div className="wa-vnext__settings-field-copy">
              <strong>
                {t(
                  'workflowActivityVNext.settings.defaultModel',
                  'Default model',
                )}
              </strong>
              <span>
                {t(
                  'workflowActivityVNext.settings.defaultModelHelp',
                  'Leave unset to use the service default.',
                )}
              </span>
            </div>
            <Select
              aria-label={t(
                'workflowActivityVNext.settings.defaultModel',
                'Default model',
              )}
              disabled={
                !llm.data?.capabilities.canEditModel ||
                selectedOption?.modelCatalog.certainty === 'unavailable'
              }
              onChange={selectModel}
              options={[
                {
                  label: t(
                    'workflowActivityVNext.settings.providerDefault',
                    'Provider default',
                  ),
                  value: 'provider-default',
                },
                ...modelIds.map((modelId) => ({
                  label: modelId,
                  value: modelId,
                })),
                ...(unavailableSavedModel
                  ? [
                      {
                        disabled: true,
                        label: `${unavailableSavedModel} (${t(
                          'workflowActivityVNext.common.unavailable',
                          'Unavailable',
                        )})`,
                        value: unavailableSavedModel,
                      },
                    ]
                  : []),
              ]}
              value={modelValue}
            />
          </div>
        ) : (
          <div className="wa-vnext__settings-field">
            <div className="wa-vnext__settings-field-copy">
              <strong>
                {t(
                  'workflowActivityVNext.settings.defaultModel',
                  'Default model',
                )}
              </strong>
              <span>
                {!draft
                  ? t(
                      'workflowActivityVNext.settings.systemDefaultModel',
                      'Uses the system-selected service and model.',
                    )
                  : unavailableSavedModel
                    ? t(
                        'workflowActivityVNext.settings.savedModelUnavailable',
                        'The saved model is unavailable. Your saved value remains unchanged.',
                      )
                    : t(
                        'workflowActivityVNext.settings.serviceDefaultModel',
                        'Uses the service default model.',
                      )}
              </span>
            </div>
          </div>
        )}
      </div>
      {savePhase === 'observed' ||
      savePhase === 'delayed' ||
      savePhase === 'failed' ? (
        <Alert
          message={
            savePhase === 'observed'
              ? t('workflowActivityVNext.settings.observed', 'Changes saved')
              : savePhase === 'delayed'
                ? t(
                    'workflowActivityVNext.settings.delayed',
                    'Changes are taking longer to appear',
                  )
                : t(
                    'workflowActivityVNext.settings.failed',
                    "Changes couldn't be saved",
                  )
          }
          description={
            savePhase === 'failed' && saveMessage ? (
              <TechnicalDetails>{saveMessage}</TechnicalDetails>
            ) : undefined
          }
          showIcon
          type={
            savePhase === 'failed'
              ? 'error'
              : savePhase === 'delayed'
                ? 'warning'
                : 'success'
          }
        />
      ) : null}
    </div>
  );

  const accountPanel = auth.isPending ? (
    <SettingsLoadingState
      message={t(
        'workflowActivityVNext.settings.accountLoading',
        'Loading account session',
      )}
    />
  ) : auth.isError && isStudioApiStatus(auth.error, 403) ? (
    <div className="wa-vnext__state wa-vnext__state--compact" role="alert">
      <div>
        <h3>
          {t('workflowActivityVNext.settings.unauthorized', 'Unauthorized')}
        </h3>
        <p>
          {t(
            'workflowActivityVNext.settings.unauthorizedDescription',
            'Your current session cannot view account or capability details.',
          )}
        </p>
      </div>
    </div>
  ) : auth.isError ? (
    <SettingsErrorState
      error={auth.error}
      onRetry={() => void auth.refetch()}
      title={t('workflowActivityVNext.settings.notLoaded', 'Not loaded')}
    />
  ) : resolvedAuth && accountIdentity ? (
    <AccountPanel
      identity={accountIdentity}
      returnTo={`${location.pathname}?section=account`}
    />
  ) : null;

  const runtimePanel = runtime.isPending ? (
    <SettingsLoadingState
      message={t(
        'workflowActivityVNext.settings.runtimeLoading',
        'Loading effective runtime',
      )}
    />
  ) : runtime.isError ? (
    <SettingsErrorState
      error={runtime.error}
      onRetry={() => void runtime.refetch()}
      title={t(
        'workflowActivityVNext.settings.runtimeUnavailable',
        'Effective runtime unavailable',
      )}
    />
  ) : (
    <section className="wa-vnext__settings-facts">
      <h3 className="wa-vnext__settings-facts-heading">
        {t(
          'workflowActivityVNext.settings.workflowExecution',
          'Workflow execution',
        )}
      </h3>
      <Descriptions
        bordered
        column={1}
        items={[
          {
            key: 'mode',
            label: t(
              'workflowActivityVNext.settings.executionTarget',
              'Execution target',
            ),
            children:
              runtime.data?.runtimeMode === 'remote'
                ? t(
                    'workflowActivityVNext.settings.remoteRuntimeLabel',
                    'Remote runtime',
                  )
                : t(
                    'workflowActivityVNext.settings.localRuntimeLabel',
                    'Local runtime',
                  ),
          },
          {
            key: 'active',
            label: t(
              'workflowActivityVNext.settings.runtimeUrl',
              'Runtime URL',
            ),
            children: (
              <Typography.Text
                className="wa-vnext__mono"
                copyable={{ text: runtime.data?.activeRuntimeBaseUrl }}
                translate="no"
              >
                {runtime.data?.activeRuntimeBaseUrl}
              </Typography.Text>
            ),
          },
        ]}
      />
    </section>
  );

  const sections = [
    {
      key: 'ai' as const,
      label: t('workflowActivityVNext.settings.ai', 'AI defaults'),
      description: t(
        'workflowActivityVNext.settings.aiDescription',
        'Choose the service and model used by new Chat, Studio, and global tool sessions without an override.',
      ),
      panel: aiPanel,
    },
    {
      key: 'account' as const,
      label: t('workflowActivityVNext.settings.account', 'Account'),
      description: t(
        'workflowActivityVNext.settings.accountDescription',
        'Your profile and access.',
      ),
      panel: accountPanel,
    },
    {
      key: 'advanced' as const,
      label: t('workflowActivityVNext.settings.advanced', 'Advanced'),
      description: t(
        'workflowActivityVNext.settings.advancedDescription',
        'Read-only execution details used when starting workflows.',
      ),
      panel: runtimePanel,
    },
  ];
  const active = sections.find((item) => item.key === section) ?? sections[0];
  const settingsFooter = (
    <>
      {dirty ? (
        <div className="wa-vnext__settings-footer">
          <section
            aria-label={t(
              'workflowActivityVNext.settings.unsavedActionsAria',
              'Unsaved settings actions',
            )}
            className="wa-vnext__settings-savebar"
          >
            <div aria-live="polite" role="status">
              <strong>
                {t('workflowActivityVNext.settings.unsaved', 'Unsaved changes')}
              </strong>
              <span>
                {t(
                  'workflowActivityVNext.settings.unsavedDescription',
                  'Your AI defaults have not been saved.',
                )}
              </span>
            </div>
            <Space className="wa-vnext__settings-actions" wrap>
              <Button
                disabled={savePhase === 'saving' || savePhase === 'accepted'}
                onClick={discard}
              >
                {t(
                  'workflowActivityVNext.settings.discard',
                  'Restore saved settings',
                )}
              </Button>
              <Button
                disabled={
                  !llm.data?.capabilities.canSave || savePhase === 'accepted'
                }
                icon={<SaveOutlined />}
                loading={savePhase === 'saving'}
                onClick={() => void save()}
                type="primary"
              >
                {t('workflowActivityVNext.settings.save', 'Save changes')}
              </Button>
            </Space>
          </section>
        </div>
      ) : null}
    </>
  );

  return (
    <WorkflowActivityVNextShell
      activeSection="settings"
      description={t(
        'workflowActivityVNext.settings.description',
        'Personal defaults and access.',
      )}
      footer={settingsFooter}
      scopeId={scopeId}
      title={t('workflowActivityVNext.settings.title', 'Settings')}
      onNavigate={requestNavigation}
    >
      <div className="wa-vnext__settings-layout">
        <nav
          aria-label={t(
            'workflowActivityVNext.settings.sectionsAria',
            'Settings sections',
          )}
          className="wa-vnext__settings-nav"
        >
          {sections.map((item) => (
            <a
              aria-current={item.key === section ? 'page' : undefined}
              className="wa-vnext__settings-nav-link"
              href={settingsSectionHref(location.pathname, item.key)}
              key={item.key}
              onClick={(event) => {
                if (
                  event.button !== 0 ||
                  event.metaKey ||
                  event.ctrlKey ||
                  event.shiftKey ||
                  event.altKey
                )
                  return;
                event.preventDefault();
                requestNavigation(
                  settingsSectionHref(location.pathname, item.key),
                );
              }}
            >
              {item.label}
            </a>
          ))}
        </nav>
        <section
          aria-labelledby={`wa-vnext-settings-${active.key}`}
          className="wa-vnext__settings-panel"
        >
          <div className="wa-vnext__settings-heading">
            <h2 id={`wa-vnext-settings-${active.key}`}>{active.label}</h2>
            <p>{active.description}</p>
          </div>
          {active.panel}
        </section>
      </div>
      <Modal
        aria-label={t(
          'workflowActivityVNext.settings.unsavedLeaveTitle',
          'Unsaved AI default changes',
        )}
        footer={[
          <Button key="stay" onClick={() => setPendingNavigation('')}>
            {t('workflowActivityVNext.settings.stay', 'Stay')}
          </Button>,
          <Button key="discard" onClick={discardAndLeave}>
            {t(
              'workflowActivityVNext.settings.discardLeave',
              'Discard and leave',
            )}
          </Button>,
          <Button
            disabled={!llm.data?.capabilities.canSave}
            key="save"
            loading={savePhase === 'saving' || savePhase === 'accepted'}
            onClick={() => void saveAndLeave()}
            type="primary"
          >
            {t('workflowActivityVNext.settings.saveLeave', 'Save and leave')}
          </Button>,
        ]}
        onCancel={() => setPendingNavigation('')}
        open={Boolean(pendingNavigation)}
        title={t(
          'workflowActivityVNext.settings.unsavedLeaveTitle',
          'Unsaved AI default changes',
        )}
      >
        <p>
          {t(
            'workflowActivityVNext.settings.unsavedLeaveDescription',
            'Save your changes, discard them, or stay in Settings.',
          )}
        </p>
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default SettingsPage;
