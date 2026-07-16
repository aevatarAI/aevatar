import { Alert, Skeleton } from 'antd';
import { useIntl } from '@umijs/max';
import React from 'react';
import type {
  TeamDetailContext,
  TeamDetailTabDefinition,
} from '@/shared/teams/teamDetailTabs';

type TeamDetailTabErrorBoundaryProps = {
  readonly children: React.ReactNode;
  readonly fallback: React.ReactNode;
};

type TeamDetailTabErrorBoundaryState = {
  readonly failed: boolean;
};

class TeamDetailTabErrorBoundary extends React.Component<
  TeamDetailTabErrorBoundaryProps,
  TeamDetailTabErrorBoundaryState
> {
  public state: TeamDetailTabErrorBoundaryState = { failed: false };

  public static getDerivedStateFromError(): TeamDetailTabErrorBoundaryState {
    return { failed: true };
  }

  public render(): React.ReactNode {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

type TeamDetailTabOutletProps<THostModel> = {
  readonly context: TeamDetailContext;
  readonly definition: TeamDetailTabDefinition<THostModel>;
  readonly hostModel: THostModel;
  readonly label: string;
  readonly pending?: boolean;
};

export function TeamDetailTabOutlet<THostModel>({
  context,
  definition,
  hostModel,
  label,
  pending = false,
}: TeamDetailTabOutletProps<THostModel>): React.ReactElement {
  const intl = useIntl();
  const LazyView = React.useMemo(
    () => React.lazy(definition.load),
    [definition],
  );
  const hostProps = definition.selectHostProps?.(hostModel) ?? {};
  const viewProps = { ...hostProps, context };
  const loadingState = (
    <div
      aria-live="polite"
      aria-label={intl.formatMessage({ id: 'teams.detail.loading' })}
      role="status"
    >
      <Skeleton active paragraph={{ rows: 4 }} title />
    </div>
  );
  const failureState = (
    <Alert
      description={intl.formatMessage(
        {
          defaultMessage:
            'The Team shell is still available. Choose another tab or refresh the page to try again.',
          id: 'teams.detail.tabs.loadFailure.description',
        },
        { tabLabel: label },
      )}
      showIcon
      title={intl.formatMessage(
        {
          defaultMessage: '{tabLabel} could not load',
          id: 'teams.detail.tabs.loadFailure.title',
        },
        { tabLabel: label },
      )}
      type="error"
    />
  );

  return (
    <div
      aria-labelledby={`team-detail-tab-${definition.id}`}
      id={`team-detail-tabpanel-${definition.id}`}
      role="tabpanel"
      style={{ minWidth: 0 }}
    >
      {pending ? (
        loadingState
      ) : (
        <TeamDetailTabErrorBoundary fallback={failureState} key={definition.id}>
          <React.Suspense fallback={loadingState}>
            <LazyView {...viewProps} />
          </React.Suspense>
        </TeamDetailTabErrorBoundary>
      )}
    </div>
  );
}
