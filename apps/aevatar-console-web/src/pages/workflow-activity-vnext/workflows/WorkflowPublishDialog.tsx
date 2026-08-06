import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Modal, Select, Space, Typography } from 'antd';
import React from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { t } from '@/shared/i18n/messages';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import type {
  StudioExplicitRequestConfirmation,
  StudioExplicitRequestPreview,
  StudioExplicitRequestRisk,
} from '@/shared/studio/models';

export type WorkflowPublishConfirmationInput = {
  readonly confirmations: readonly StudioExplicitRequestConfirmation[];
  readonly preview: StudioExplicitRequestPreview;
  readonly serviceId: string;
};

type WorkflowPublishDialogProps = {
  readonly onCancel: () => void;
  readonly onPublish: (
    input: WorkflowPublishConfirmationInput,
  ) => Promise<void>;
  readonly onReview: (
    serviceId: string,
  ) => Promise<StudioExplicitRequestPreview>;
  readonly onReturnToSelection: () => void;
  readonly open: boolean;
  readonly scopeId: string;
  readonly workflowName: string;
};

type PublicationStage = 'selecting' | 'preparing' | 'reviewing' | 'submitting';

type PublicationReview = {
  readonly preview: StudioExplicitRequestPreview;
  readonly serviceId: string;
};

function errorStatus(error: unknown): number | undefined {
  if (
    error &&
    typeof error === 'object' &&
    'status' in error &&
    typeof error.status === 'number'
  ) {
    return error.status;
  }
  return undefined;
}

function riskLabel(risk: StudioExplicitRequestRisk): string {
  switch (risk) {
    case 'destructive':
      return t(
        'workflowActivityVNext.publish.risk.destructive',
        'Can make changes',
      );
    case 'write':
      return t('workflowActivityVNext.publish.risk.write', 'Can make changes');
    default:
      return t('workflowActivityVNext.publish.risk.readOnly', 'Read only');
  }
}

function errorCopy(error: unknown): string {
  const status = errorStatus(error);
  if (status === 401) {
    return t('workflowActivityVNext.state.unauthorized', 'Sign in to continue');
  }
  if (status === 403) {
    return t(
      'workflowActivityVNext.state.forbidden',
      "You don't have access to this workspace",
    );
  }
  return t(
    'workflowActivityVNext.publish.reviewUnavailable',
    "We couldn't prepare this workflow for publishing.",
  );
}

const WorkflowPublishDialog: React.FC<WorkflowPublishDialogProps> = ({
  onCancel,
  onPublish,
  onReview,
  onReturnToSelection,
  open,
  scopeId,
  workflowName,
}) => {
  const normalizedScopeId = scopeId.trim();
  const [selectedServiceId, setSelectedServiceId] = React.useState('');
  const [stage, setStage] = React.useState<PublicationStage>('selecting');
  const [review, setReview] = React.useState<PublicationReview | null>(null);
  const toast = useConsoleToast();
  const reviewGenerationRef = React.useRef(0);
  const servicesQuery = useQuery({
    enabled: open && Boolean(normalizedScopeId),
    queryKey: [
      'workflow-activity-vnext',
      'publication-services',
      normalizedScopeId,
    ],
    queryFn: () =>
      scopeRuntimeApi.listServices(normalizedScopeId, { take: 200 }),
    retry: false,
  });
  const services = servicesQuery.data ?? [];
  const selectedService = services.find(
    (service) => service.serviceId === selectedServiceId,
  );
  const selectedServiceIsAvailable = Boolean(selectedService);

  React.useEffect(() => {
    if (open && selectedServiceIsAvailable) return;
    reviewGenerationRef.current += 1;
    setSelectedServiceId('');
    setStage('selecting');
    setReview(null);
  }, [open, selectedServiceIsAvailable]);

  const servicesStatus = errorStatus(servicesQuery.error);
  const hasServiceError = Boolean(servicesQuery.error);
  const hasNoServices =
    !servicesQuery.isPending && !hasServiceError && services.length === 0;
  const canReview =
    stage === 'selecting' &&
    Boolean(selectedServiceId) &&
    selectedServiceIsAvailable &&
    !hasServiceError &&
    !hasNoServices;

  const returnToSelection = React.useCallback(() => {
    reviewGenerationRef.current += 1;
    setStage('selecting');
    setReview(null);
    onReturnToSelection();
  }, [onReturnToSelection]);

  const handleReview = React.useCallback(async () => {
    if (!canReview) return;
    const generation = ++reviewGenerationRef.current;
    setReview(null);
    setStage('preparing');
    try {
      const preview = await onReview(selectedServiceId);
      if (generation !== reviewGenerationRef.current) return;
      if (
        preview.items.some(
          (item) => !item.allowedExecutionModes.includes('interactive'),
        )
      ) {
        throw new Error(
          'Interactive publication is unavailable for an external request.',
        );
      }
      setReview({ preview, serviceId: selectedServiceId });
      setStage('reviewing');
    } catch (error) {
      if (generation !== reviewGenerationRef.current) return;
      toast.error(errorCopy(error));
      setStage('selecting');
    }
  }, [canReview, onReview, selectedServiceId, toast]);

  const handlePublish = React.useCallback(async () => {
    if (!review || stage !== 'reviewing') return;
    setStage('submitting');
    try {
      const confirmations = review.preview.items.map((item) => ({
        workflowId: review.preview.workflowId,
        revisionId: review.preview.revisionId,
        callSiteId: item.callSiteId,
        requestContractDigest: item.requestContractDigest,
        attestedRisk: item.effectiveRisk,
      }));
      await onPublish({
        confirmations,
        preview: review.preview,
        serviceId: review.serviceId,
      });
    } catch (error) {
      toast.error(errorCopy(error));
      setStage('reviewing');
    }
  }, [onPublish, review, stage, toast]);

  const close = React.useCallback(() => {
    if (stage === 'submitting') return;
    if (stage === 'preparing' || stage === 'reviewing') {
      returnToSelection();
      return;
    }
    reviewGenerationRef.current += 1;
    onCancel();
  }, [onCancel, returnToSelection, stage]);

  const serviceErrorAlert = hasServiceError ? (
    <Alert
      action={
        servicesStatus === 401 || servicesStatus === 403 ? undefined : (
          <Button onClick={() => void servicesQuery.refetch()}>
            {t('workflowActivityVNext.common.retry', 'Retry')}
          </Button>
        )
      }
      message={
        servicesStatus === 401
          ? t('workflowActivityVNext.state.unauthorized', 'Sign in to continue')
          : servicesStatus === 403
            ? t(
                'workflowActivityVNext.state.forbidden',
                "You don't have access to this workspace",
              )
            : t(
                'workflowActivityVNext.publish.servicesUnavailable',
                'Services are unavailable',
              )
      }
      showIcon
      type="error"
    />
  ) : null;

  const selectionContent = (
    <>
      <Typography.Paragraph>
        {t(
          'workflowActivityVNext.publish.destinationDescription',
          'Choose the service that will use {workflowName}.',
          { workflowName },
        )}
      </Typography.Paragraph>
      {servicesQuery.isPending ? (
        <Typography.Text>
          {t(
            'workflowActivityVNext.publish.loadingServices',
            'Loading services…',
          )}
        </Typography.Text>
      ) : null}
      {serviceErrorAlert}
      {hasNoServices ? (
        <Alert
          action={
            <Button onClick={() => void servicesQuery.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          }
          message={t(
            'workflowActivityVNext.publish.noServices',
            'No services are available in this workspace',
          )}
          showIcon
          type="info"
        />
      ) : null}
      {!servicesQuery.isPending && !hasServiceError && services.length > 0 ? (
        <Select
          aria-label={t('workflowActivityVNext.publish.service', 'Service')}
          onChange={(value) => {
            reviewGenerationRef.current += 1;
            setSelectedServiceId(value);
            setReview(null);
          }}
          options={services.map((service) => ({
            label: service.displayName,
            value: service.serviceId,
          }))}
          placeholder={t(
            'workflowActivityVNext.publish.selectService',
            'Choose a service',
          )}
          value={selectedServiceId || undefined}
        />
      ) : null}
    </>
  );

  const preparingContent = (
    <div aria-live="polite" role="status">
      <Typography.Text strong>
        {t('workflowActivityVNext.publish.reviewing', 'Reviewing publication…')}
      </Typography.Text>
      <Typography.Paragraph>
        {t(
          'workflowActivityVNext.publish.reviewingDescription',
          'Preparing this workflow for review.',
        )}
      </Typography.Paragraph>
    </div>
  );

  const reviewContent = review ? (
    <>
      <Typography.Paragraph>
        {t(
          'workflowActivityVNext.publish.reviewDescription',
          'Review what this workflow may do when it runs.',
        )}
      </Typography.Paragraph>
      <Typography.Text strong>
        {t(
          'workflowActivityVNext.publish.publishingTo',
          'Publishing to {service}',
          {
            service: selectedService?.displayName || workflowName,
          },
        )}
      </Typography.Text>
      {review.preview.items.length === 0 ? (
        <Alert
          message={t(
            'workflowActivityVNext.publish.noExternalRequests',
            'No external requests need review.',
          )}
          showIcon
          type="info"
        />
      ) : (
        review.preview.items.map((item) => (
          <div className="wa-vnext__publish-review-item" key={item.callSiteId}>
            <Typography.Text strong>
              {item.method.toUpperCase()} {item.pathTemplate}
            </Typography.Text>
            <Typography.Text type="secondary">
              {t('workflowActivityVNext.publish.risk', 'Impact')}:{' '}
              {riskLabel(item.effectiveRisk)}
            </Typography.Text>
            <Typography.Text type="secondary">
              {item.approvalRequired
                ? t(
                    'workflowActivityVNext.publish.approvalRequired',
                    'Approval is required before this request can run.',
                  )
                : t(
                    'workflowActivityVNext.publish.approvalNotRequired',
                    'No additional approval is required.',
                  )}
            </Typography.Text>
          </div>
        ))
      )}
    </>
  ) : null;

  return (
    <Modal
      destroyOnHidden
      footer={
        <Space>
          {stage === 'selecting' ? (
            <>
              <Button onClick={close}>
                {t('workflowActivityVNext.common.cancel', 'Cancel')}
              </Button>
              <Button
                disabled={!canReview}
                onClick={() => void handleReview()}
                type="primary"
              >
                {t(
                  'workflowActivityVNext.publish.reviewAndPublish',
                  'Review and publish',
                )}
              </Button>
            </>
          ) : stage === 'preparing' ? (
            <Button onClick={returnToSelection}>
              {t('workflowActivityVNext.publish.backToService', 'Back')}
            </Button>
          ) : (
            <>
              <Button
                disabled={stage === 'submitting'}
                onClick={returnToSelection}
              >
                {t('workflowActivityVNext.publish.backToService', 'Back')}
              </Button>
              <Button
                disabled={!review}
                loading={stage === 'submitting'}
                onClick={() => void handlePublish()}
                type="primary"
              >
                {t('workflowActivityVNext.editor.publish', 'Publish')}
              </Button>
            </>
          )}
        </Space>
      }
      keyboard={stage !== 'submitting'}
      mask={{ closable: stage !== 'submitting' }}
      onCancel={close}
      open={open}
      title={t('workflowActivityVNext.publish.title', 'Publish workflow')}
    >
      <Space direction="vertical" size="middle" style={{ width: '100%' }}>
        {stage === 'selecting'
          ? selectionContent
          : stage === 'preparing'
            ? preparingContent
            : reviewContent}
      </Space>
    </Modal>
  );
};

export default WorkflowPublishDialog;
