import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import WorkflowPublishDialog from './WorkflowPublishDialog';

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    listServices: jest.fn(),
  },
}));

const mockListServices = scopeRuntimeApi.listServices as jest.Mock;

const service = {
  activeServingRevisionId: 'rev-existing',
  appId: 'app-alpha',
  defaultServingRevisionId: 'rev-existing',
  deploymentId: 'deployment-alpha',
  deploymentStatus: 'Active',
  displayName: 'Service alpha',
  endpoints: [],
  namespace: 'scope-alpha',
  policyIds: [],
  primaryActorId: 'actor-service-alpha',
  serviceId: 'svc-alpha',
  serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
  tenantId: 'tenant-alpha',
  updatedAt: '2026-08-06T10:00:00Z',
} as const;

function createPreview(callSiteId: string) {
  return {
    items: [
      {
        allowedExecutionModes: ['interactive'],
        approvalRequired: false,
        bodyMode: 'json',
        bodyRequired: true,
        callSiteId,
        effectiveRisk: 'write',
        method: 'post',
        pathTemplate: '/external/notifications',
        requestContractDigest: `digest-${callSiteId}`,
        responseMode: 'text',
        userServiceId: service.serviceId,
      },
    ],
    revisionId: 'rev-preview-alpha',
    workflowId: 'wf-alpha',
  } as const;
}

function createDeferred<T>() {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });

  return { promise, resolve };
}

function renderDialog({
  onPublish,
  onReview,
  onReturnToSelection,
}: {
  readonly onPublish: () => Promise<void>;
  readonly onReview: (
    serviceId: string,
  ) => Promise<ReturnType<typeof createPreview>>;
  readonly onReturnToSelection: () => void;
}) {
  return renderWithQueryClient(
    <WorkflowPublishDialog
      onCancel={() => undefined}
      onPublish={onPublish}
      onReview={onReview}
      onReturnToSelection={onReturnToSelection}
      open
      scopeId="scope-alpha"
      workflowName="Workflow alpha"
    />,
  );
}

async function selectService(dialog: HTMLElement): Promise<void> {
  const serviceSelect = await within(dialog).findByRole('combobox', {
    name: 'Service',
  });
  fireEvent.mouseDown(serviceSelect);
  fireEvent.click(await screen.findByText(service.displayName));
  await waitFor(() =>
    expect(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    ).toBeEnabled(),
  );
}

function startReview(dialog: HTMLElement): void {
  fireEvent.click(
    within(dialog).getByRole('button', { name: 'Review and publish' }),
  );
}

describe('WorkflowPublishDialog', () => {
  beforeEach(() => {
    mockListServices.mockResolvedValue([service]);
  });

  it('keeps the final publish action unavailable while preparing the review', async () => {
    const preview = createDeferred<ReturnType<typeof createPreview>>();
    const onPublish = jest.fn(async () => undefined);
    const onReview = jest.fn((_serviceId: string) => preview.promise);

    renderDialog({
      onPublish,
      onReview,
      onReturnToSelection: jest.fn(),
    });

    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    await selectService(dialog);
    startReview(dialog);

    await waitFor(() => expect(onReview).toHaveBeenCalledWith('svc-alpha'));
    expect(
      await within(dialog).findByText('Reviewing publication…'),
    ).toBeInTheDocument();
    expect(
      within(dialog).queryByRole('button', { name: 'Publish' }),
    ).not.toBeInTheDocument();
    expect(onPublish).not.toHaveBeenCalled();

    await act(async () => {
      preview.resolve(createPreview('request-alpha'));
    });

    expect(
      await within(dialog).findByText('POST /external/notifications'),
    ).toBeInTheDocument();
    expect(
      within(dialog).getByRole('button', { name: 'Publish' }),
    ).toBeEnabled();
  });

  it('ignores a preview that resolves after returning and starting a newer review', async () => {
    const firstPreview = createDeferred<ReturnType<typeof createPreview>>();
    const secondPreview = createDeferred<ReturnType<typeof createPreview>>();
    const firstResult = createPreview('request-first');
    const secondResult = createPreview('request-second');
    const onPublish = jest.fn(async () => undefined);
    const onReview = jest
      .fn<Promise<ReturnType<typeof createPreview>>, [string]>()
      .mockReturnValueOnce(firstPreview.promise)
      .mockReturnValueOnce(secondPreview.promise);
    const onReturnToSelection = jest.fn();

    renderDialog({ onPublish, onReview, onReturnToSelection });

    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    await selectService(dialog);
    startReview(dialog);
    await waitFor(() => expect(onReview).toHaveBeenCalledTimes(1));

    fireEvent.click(within(dialog).getByRole('button', { name: 'Back' }));
    expect(onReturnToSelection).toHaveBeenCalledTimes(1);
    startReview(dialog);
    await waitFor(() => expect(onReview).toHaveBeenCalledTimes(2));

    await act(async () => {
      firstPreview.resolve(firstResult);
    });

    expect(
      within(dialog).queryByRole('button', { name: 'Publish' }),
    ).not.toBeInTheDocument();
    expect(onPublish).not.toHaveBeenCalled();

    await act(async () => {
      secondPreview.resolve(secondResult);
    });

    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Publish' }),
    );
    await waitFor(() => expect(onPublish).toHaveBeenCalledTimes(1));
    expect(onPublish).toHaveBeenCalledWith(
      expect.objectContaining({
        preview: secondResult,
        serviceId: service.serviceId,
      }),
    );
  });
});
