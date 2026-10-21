import { Alert, Button, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';

type NodeConfigurationCatalogStatusProps = {
  readonly cached: boolean;
  readonly fallback: boolean;
  readonly failed: boolean;
  readonly loading: boolean;
  readonly missing: boolean;
  readonly onRetry: () => void;
  readonly retrying: boolean;
};

const NodeConfigurationCatalogStatus: React.FC<
  NodeConfigurationCatalogStatusProps
> = ({ cached, fallback, failed, loading, missing, onRetry, retrying }) => {
  if (loading) {
    return (
      <AevatarContentSkeleton
        ariaLabel={t(
          'shared.studio.nodeConfiguration.catalogLoading',
          'Loading settings',
        )}
        rows={2}
        variant="list"
      />
    );
  }

  if (failed) {
    return (
      <Alert
        action={
          <Button loading={retrying} onClick={onRetry} size="small">
            {t('shared.studio.nodeConfiguration.retry', 'Retry')}
          </Button>
        }
        message={
          fallback
            ? t(
                'shared.studio.nodeConfiguration.catalogFallbackUnavailable',
                "Couldn't load the latest settings. Showing built-in settings.",
              )
            : cached
              ? t(
                  'shared.studio.nodeConfiguration.catalogRefreshUnavailable',
                  "Couldn't refresh the latest settings. Showing the last loaded settings.",
                )
              : t(
                  'shared.studio.nodeConfiguration.catalogUnavailable',
                  "Couldn't load guided settings. You can still edit advanced JSON.",
                )
        }
        role="alert"
        showIcon
        type="warning"
      />
    );
  }

  return missing ? (
    <Typography.Text type="secondary">
      {fallback
        ? t(
            'shared.studio.nodeConfiguration.catalogFallback',
            'Showing built-in settings for this step.',
          )
        : t(
            'shared.studio.nodeConfiguration.catalogMissing',
            'Guided settings are unavailable for this step. Use advanced JSON.',
          )}
    </Typography.Text>
  ) : null;
};

export default NodeConfigurationCatalogStatus;
