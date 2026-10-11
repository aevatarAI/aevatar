import { Alert, Button } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';

type PanelErrorBoundaryProps = {
  readonly children: React.ReactNode;
  readonly fallback: React.ReactNode;
};

class PanelErrorBoundary extends React.Component<
  PanelErrorBoundaryProps,
  { failed: boolean }
> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

type DeferredWorkflowPanelProps<Props extends object> = {
  readonly active: boolean;
  readonly children: (
    Panel: React.LazyExoticComponent<React.ComponentType<Props>>,
  ) => React.ReactNode;
  readonly load: () => Promise<{ default: React.ComponentType<Props> }>;
  readonly renderFallback: (retry?: () => void) => React.ReactNode;
};

/** Load on first use, then let the panel's own visibility contract retain state. */
export function DeferredWorkflowPanel<Props extends object>({
  active,
  children,
  load,
  renderFallback,
}: DeferredWorkflowPanelProps<Props>) {
  const [activated, setActivated] = React.useState(active);
  const [attempt, setAttempt] = React.useState(() => ({
    key: 0,
    Panel: React.lazy(load),
  }));

  React.useEffect(() => {
    if (active) setActivated(true);
  }, [active]);

  if (!active && !activated) return null;

  const retry = () => {
    // React.lazy caches a rejected import. A fresh type retries only this panel;
    // reloading the page would discard the workflow's unsaved document.
    setAttempt((current) => ({
      key: current.key + 1,
      Panel: React.lazy(load),
    }));
  };

  return (
    <PanelErrorBoundary
      fallback={active ? renderFallback(retry) : null}
      key={attempt.key}
    >
      <React.Suspense fallback={active ? renderFallback() : null}>
        {children(attempt.Panel)}
      </React.Suspense>
    </PanelErrorBoundary>
  );
}

export function WorkflowPanelLoadState({
  retry,
}: {
  readonly retry?: () => void;
}) {
  return retry ? (
    <Alert
      action={
        <Button onClick={retry}>
          {t('workflowActivityVNext.common.retry', 'Retry')}
        </Button>
      }
      role="alert"
      showIcon
      title={t(
        'shared.workflowPanel.loadFailed',
        "This panel couldn't be loaded",
      )}
      type="error"
    />
  ) : (
    <AevatarContentSkeleton
      ariaLabel={t('shared.workflowPanel.loading', 'Loading panel…')}
      rows={3}
      variant="list"
    />
  );
}
