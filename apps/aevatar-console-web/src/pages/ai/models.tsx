import {
  CloudServerOutlined,
  DatabaseOutlined,
  ReloadOutlined,
  SettingOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Empty, Space, Tag, Typography } from 'antd';
import React from 'react';
import {
  type AIModelSourceError,
  type AIPersonalModelSettings,
  type AIScopeModelSource,
  aiModelsApi,
} from '@/shared/api/aiModelsApi';
import { t } from '@/shared/i18n/messages';
import { AI_MODELS_ROUTE } from '@/shared/navigation/aiRoutes';
import { history } from '@/shared/navigation/history';
import { aiWorkspaceQueryKeys } from '@/shared/query/aiWorkspaceQueryKeys';
import { describeError } from '@/shared/ui/errorText';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import AIWorkspaceShell, {
  useAIWorkspaceContext,
} from './components/AIWorkspaceShell';
import './models.less';

function formatObservedAt(value: string | null): string | null {
  if (!value) {
    return null;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function SourceFailure({
  error,
}: {
  error: AIModelSourceError;
}): React.ReactElement {
  return (
    <Alert
      description={error.message}
      message={error.code}
      showIcon
      type="warning"
    />
  );
}

function selectedModel(settings: AIPersonalModelSettings): string {
  const selection = settings.savedSelection?.modelSelection;
  if (!selection) {
    return t('pages.ai.models.personal.systemDefault', 'System default');
  }
  if (selection?.kind === 'explicit_model') {
    return (
      selection.modelId?.trim() ||
      t('pages.ai.models.value.unavailable', 'Unavailable')
    );
  }
  if (selection?.kind === 'provider_default') {
    return t('pages.ai.models.personal.providerDefault', 'Provider default');
  }
  if (selection.kind === 'unsupported') {
    return t(
      'pages.ai.models.personal.unsupportedSelection',
      'Unsupported selection',
    );
  }
  return t('pages.ai.models.personal.unspecifiedSelection', 'Unspecified');
}

function availabilityLabel(availability: 'available' | 'unavailable'): string {
  return availability === 'available'
    ? t('pages.ai.models.availability.available', 'Available')
    : t('pages.ai.models.availability.unavailable', 'Unavailable');
}

function selectionStatusLabel(
  status: AIPersonalModelSettings['selectionStatus'],
): string {
  const labels: Record<AIPersonalModelSettings['selectionStatus'], string> = {
    legacy_repair_required: t(
      'pages.ai.models.selectionStatus.legacyRepairRequired',
      'Migration required',
    ),
    needs_repair: t(
      'pages.ai.models.selectionStatus.needsRepair',
      'Needs repair',
    ),
    ready: t('pages.ai.models.selectionStatus.ready', 'Ready'),
    system_default: t(
      'pages.ai.models.selectionStatus.systemDefault',
      'System default',
    ),
    unspecified: t(
      'pages.ai.models.selectionStatus.unspecified',
      'Unspecified',
    ),
    verification_unavailable: t(
      'pages.ai.models.selectionStatus.verificationUnavailable',
      'Verification unavailable',
    ),
  };
  return labels[status];
}

function remediationLabel(
  remediation: AIPersonalModelSettings['remediation'],
): string {
  const labels: Record<AIPersonalModelSettings['remediation'], string> = {
    choose_replacement: t(
      'pages.ai.models.remediation.chooseReplacement',
      'Choose a replacement',
    ),
    connect_provider: t(
      'pages.ai.models.remediation.connectProvider',
      'Connect provider',
    ),
    none: t('pages.ai.models.remediation.none', 'No action needed'),
    reselect: t('pages.ai.models.remediation.reselect', 'Select again'),
    retry_catalog: t(
      'pages.ai.models.remediation.retryCatalog',
      'Retry catalog',
    ),
    unspecified: t(
      'pages.ai.models.remediation.unspecified',
      'Needs attention',
    ),
  };
  return labels[remediation];
}

function policyModeLabel(mode: 'inherit_platform' | 'custom_replace'): string {
  return mode === 'inherit_platform'
    ? t('pages.ai.models.policy.inheritPlatform', 'Inherits platform catalog')
    : t('pages.ai.models.policy.customReplace', 'Scope catalog override');
}

function PersonalDefaultSection({
  settings,
}: {
  settings: AIPersonalModelSettings;
}): React.ReactElement {
  const statusColor =
    settings.selectionStatus === 'ready' ? 'success' : 'warning';
  const routeLabel =
    settings.savedRouteLabel.trim() ||
    t('pages.ai.models.personal.systemRoute', 'System route');

  return (
    <div className="ai-models-section-body">
      <div className="ai-models-primary-value">
        <Typography.Text className="ai-models-value-label">
          {t('pages.ai.models.personal.current', 'Current selection')}
        </Typography.Text>
        <Typography.Title
          ellipsis={{ tooltip: selectedModel(settings) }}
          level={3}
        >
          {selectedModel(settings)}
        </Typography.Title>
        <Space size={[6, 6]} wrap>
          <Tag color="blue">{routeLabel}</Tag>
          <Tag color={statusColor}>
            {selectionStatusLabel(settings.selectionStatus)}
          </Tag>
        </Space>
      </div>

      <div className="ai-models-inline-actions">
        <Button
          icon={<SettingOutlined />}
          onClick={() => history.push('/settings')}
          type="primary"
        >
          {t('pages.ai.models.personal.manage', 'Manage personal default')}
        </Button>
      </div>

      {settings.remediation !== 'none' &&
      settings.remediation !== 'unspecified' ? (
        <Alert
          description={t(
            'pages.ai.models.personal.remediation.description',
            'The saved selection needs attention before it can be used reliably.',
          )}
          message={remediationLabel(settings.remediation)}
          showIcon
          type="warning"
        />
      ) : null}
    </div>
  );
}

function modelSourceIdentity(source: AIScopeModelSource): string | null {
  return (
    source.userServiceId?.trim() || source.catalogServiceId?.trim() || null
  );
}

function ScopeSourceRow({
  source,
}: {
  source: AIScopeModelSource;
}): React.ReactElement {
  const serviceSlug =
    source.serviceSlugSnapshot?.trim() ||
    t(
      'pages.ai.models.scopeCatalog.source.slugUnavailable',
      'Service unavailable',
    );
  const sourceIdentity = modelSourceIdentity(source);
  const sourceIdentityLabel =
    sourceIdentity ??
    t('pages.ai.models.scopeCatalog.source.identityUnknown', 'Unknown source');

  return (
    <article className="ai-models-source-row">
      <div className="ai-models-source-copy">
        <Typography.Text ellipsis={{ tooltip: serviceSlug }} strong>
          {serviceSlug}
        </Typography.Text>
        <Typography.Text
          className="ai-models-source-identity"
          copyable={sourceIdentity ? { text: sourceIdentity } : false}
          ellipsis={{ tooltip: sourceIdentityLabel }}
        >
          {sourceIdentityLabel}
        </Typography.Text>
      </div>
      <div className="ai-models-model-list">
        {source.modelIds.map((modelId) => (
          <Tag key={`${source.sourceId}:${modelId}`}>{modelId}</Tag>
        ))}
      </div>
    </article>
  );
}

const AIModelsContent: React.FC = () => {
  const { context, queryAuthority, scopeId } = useAIWorkspaceContext();
  const modelsEndpoint = context.apis.models;
  const modelsDeclared =
    context.pages.models === AI_MODELS_ROUTE &&
    modelsEndpoint === '/api/ai/models' &&
    context.features.models?.availability === 'available' &&
    context.features.models.page === AI_MODELS_ROUTE &&
    context.features.models.api === modelsEndpoint;
  const modelsQuery = useQuery({
    enabled: modelsDeclared,
    queryFn: ({ signal }) =>
      aiModelsApi.getModels(modelsEndpoint ?? '', signal),
    queryKey: aiWorkspaceQueryKeys.models({
      ...queryAuthority,
      scopeId,
    }),
    retry: false,
  });
  const scopeMismatch = Boolean(
    modelsQuery.data && modelsQuery.data.scopeCatalog.scopeId !== scopeId,
  );

  if (!modelsDeclared) {
    return (
      <div className="ai-models-page">
        <InventoryReadinessState
          description={t(
            'pages.ai.models.notAvailable.description',
            'Models are not enabled for this AI workspace.',
          )}
          kind="empty"
          title={t(
            'pages.ai.models.notAvailable.title',
            'Models not available',
          )}
        />
      </div>
    );
  }

  if (modelsQuery.isLoading) {
    return (
      <div className="ai-models-page">
        <InventoryReadinessState
          description={t(
            'pages.ai.models.loading.description',
            'Reading personal preferences and the scope model catalog',
          )}
          kind="loading"
          title={t('pages.ai.models.loading.title', 'Loading models')}
        />
      </div>
    );
  }

  if (modelsQuery.isError || !modelsQuery.data || scopeMismatch) {
    return (
      <div className="ai-models-page">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.models.retry', 'Retry'),
            onClick: () => void modelsQuery.refetch(),
          }}
          description={
            scopeMismatch
              ? t(
                  'pages.ai.models.scopeMismatch.description',
                  'The model catalog did not match the authenticated workspace scope.',
                )
              : describeError(
                  modelsQuery.error,
                  t(
                    'pages.ai.models.error.description',
                    'The model authorities could not be read.',
                  ),
                )
          }
          kind="error"
          title={
            scopeMismatch
              ? t(
                  'pages.ai.models.scopeMismatch.title',
                  'Model catalog scope mismatch',
                )
              : t('pages.ai.models.error.title', 'Models unavailable')
          }
        />
      </div>
    );
  }

  const { personalDefault, scopeCatalog } = modelsQuery.data;
  const observedAt = formatObservedAt(scopeCatalog.updatedAtUtc);
  const effectiveSources = scopeCatalog.policy?.effectiveSources ?? [];

  return (
    <div className="ai-models-page">
      <header className="ai-models-page-header">
        <div>
          <Typography.Title level={1}>
            {t('pages.ai.models.title', 'Models')}
          </Typography.Title>
          <Typography.Text type="secondary">
            {t('pages.ai.models.scope', 'Scope {scopeId}', { scopeId })}
          </Typography.Text>
        </div>
        <Button
          aria-label={t('pages.ai.models.refresh', 'Refresh models')}
          icon={<ReloadOutlined />}
          loading={modelsQuery.isFetching}
          onClick={() => void modelsQuery.refetch()}
        />
      </header>

      <section
        className="ai-models-section"
        aria-labelledby="ai-personal-model-heading"
      >
        <div className="ai-models-section-heading">
          <span
            aria-hidden="true"
            className="ai-models-section-icon ai-models-section-icon-personal"
          >
            <UserOutlined />
          </span>
          <div>
            <Typography.Title id="ai-personal-model-heading" level={2}>
              {t('pages.ai.models.personal.title', 'My default model')}
            </Typography.Title>
            <Typography.Text type="secondary">
              {t('pages.ai.models.personal.authority', 'Personal preference')}
            </Typography.Text>
          </div>
          <Tag
            color={
              personalDefault.availability === 'available'
                ? 'success'
                : 'warning'
            }
          >
            {availabilityLabel(personalDefault.availability)}
          </Tag>
        </div>
        {personalDefault.settings ? (
          <PersonalDefaultSection settings={personalDefault.settings} />
        ) : personalDefault.error ? (
          <SourceFailure error={personalDefault.error} />
        ) : null}
      </section>

      <section
        className="ai-models-section"
        aria-labelledby="ai-scope-model-heading"
      >
        <div className="ai-models-section-heading">
          <span
            aria-hidden="true"
            className="ai-models-section-icon ai-models-section-icon-scope"
          >
            <CloudServerOutlined />
          </span>
          <div>
            <Typography.Title id="ai-scope-model-heading" level={2}>
              {t('pages.ai.models.scopeCatalog.title', 'Available models')}
            </Typography.Title>
            <Typography.Text type="secondary">
              {scopeCatalog.authorityStateVersion === null
                ? t('pages.ai.models.scopeCatalog.authority', 'Scope catalog')
                : t(
                    'pages.ai.models.scopeCatalog.version',
                    'Scope catalog observed at v{version}',
                    { version: scopeCatalog.authorityStateVersion },
                  )}
            </Typography.Text>
          </div>
          <Space size={6} wrap>
            {scopeCatalog.policy ? (
              <Tag icon={<DatabaseOutlined />}>
                {policyModeLabel(scopeCatalog.policy.mode)}
              </Tag>
            ) : null}
            {observedAt ? <Tag>{observedAt}</Tag> : null}
          </Space>
        </div>

        {scopeCatalog.error ? (
          <SourceFailure error={scopeCatalog.error} />
        ) : effectiveSources.length > 0 ? (
          <div className="ai-models-source-list">
            {effectiveSources.map((source) => (
              <ScopeSourceRow key={source.sourceId} source={source} />
            ))}
          </div>
        ) : (
          <Empty
            description={t(
              'pages.ai.models.scopeCatalog.empty',
              'No models are currently available for this scope.',
            )}
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        )}
      </section>
    </div>
  );
};

const AIModelsPage: React.FC = () => (
  <AIWorkspaceShell>
    <AIModelsContent />
  </AIWorkspaceShell>
);

export default AIModelsPage;
