import { Modal, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import type {
  StudioExplicitRequestConfirmation,
  StudioExplicitRequestPreview,
} from '@/shared/studio/models';

export function createWorkflowRevisionIdentityCandidate(): string {
  const random = globalThis.crypto?.randomUUID?.();
  if (random) {
    return `rev-${random}`;
  }

  return `rev-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

export async function confirmInteractiveExplicitRequestPreview(
  preview: StudioExplicitRequestPreview,
): Promise<readonly StudioExplicitRequestConfirmation[] | null> {
  const previewItems = preview.items;
  if (previewItems.length === 0) {
    return [];
  }

  if (
    previewItems.some(
      (item) => !item.allowedExecutionModes.includes('interactive'),
    )
  ) {
    throw new Error(
      t(
        'teamMemberWorkflowStudio.explicitRequest.interactiveUnavailable',
        'An external request is not available for interactive publication.',
      ),
    );
  }

  return new Promise((resolve) => {
    Modal.confirm({
      autoFocusButton: 'cancel',
      cancelText: t(
        'teamMemberWorkflowStudio.explicitRequest.cancel',
        'Cancel',
      ),
      centered: true,
      content: React.createElement(
        'div',
        { style: { display: 'grid', gap: 12 } },
        React.createElement(
          Typography.Text,
          null,
          t(
            'teamMemberWorkflowStudio.explicitRequest.description',
            'Review each external request before publishing this workflow.',
          ),
        ),
        ...previewItems.map((item) =>
          React.createElement(
            'div',
            { key: item.callSiteId, style: { display: 'grid', gap: 4 } },
            React.createElement(
              Typography.Text,
              { strong: true },
              `${t('teamMemberWorkflowStudio.explicitRequest.service', 'Service')}: ${item.userServiceId}`,
            ),
            React.createElement(
              Typography.Text,
              null,
              `${t('teamMemberWorkflowStudio.explicitRequest.methodPath', 'Method and path')}: ${item.method.toUpperCase()} ${item.pathTemplate}`,
            ),
            React.createElement(
              Typography.Text,
              null,
              `${t('teamMemberWorkflowStudio.explicitRequest.risk', 'Risk')}: ${item.effectiveRisk}`,
            ),
            React.createElement(
              Typography.Text,
              null,
              `${t('teamMemberWorkflowStudio.explicitRequest.approval', 'Approval')}: ${
                item.approvalRequired
                  ? t(
                      'teamMemberWorkflowStudio.explicitRequest.required',
                      'Required',
                    )
                  : t(
                      'teamMemberWorkflowStudio.explicitRequest.notRequired',
                      'Not required',
                    )
              }`,
            ),
            React.createElement(
              Typography.Text,
              null,
              `${t('teamMemberWorkflowStudio.explicitRequest.body', 'Request body')}: ${item.bodyMode} (${
                item.bodyRequired
                  ? t(
                      'teamMemberWorkflowStudio.explicitRequest.required',
                      'Required',
                    )
                  : t(
                      'teamMemberWorkflowStudio.explicitRequest.notRequired',
                      'Not required',
                    )
              })`,
            ),
            React.createElement(
              Typography.Text,
              null,
              `${t('teamMemberWorkflowStudio.explicitRequest.response', 'Response')}: ${item.responseMode}`,
            ),
            React.createElement(
              Typography.Text,
              null,
              `${t('teamMemberWorkflowStudio.explicitRequest.executionModes', 'Allowed execution modes')}: ${item.allowedExecutionModes.join(', ')}`,
            ),
          ),
        ),
      ),
      okText: t(
        'teamMemberWorkflowStudio.explicitRequest.confirm',
        'Confirm and publish',
      ),
      onCancel: () => resolve(null),
      onOk: () =>
        resolve(
          previewItems.map((item) => ({
            workflowId: preview.workflowId,
            revisionId: preview.revisionId,
            callSiteId: item.callSiteId,
            requestContractDigest: item.requestContractDigest,
            attestedRisk: item.effectiveRisk,
          })),
        ),
      title: t(
        'teamMemberWorkflowStudio.explicitRequest.title',
        'Review external requests',
      ),
    });
  });
}
