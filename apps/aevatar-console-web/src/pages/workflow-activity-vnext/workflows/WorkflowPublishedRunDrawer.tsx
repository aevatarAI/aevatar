import { Button, Drawer, Input, Space } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import type { WorkflowPublishedInvocationTarget } from '../hooks/useWorkflowEditor';

type WorkflowPublishedRunDrawerProps = {
  readonly children?: React.ReactNode;
  readonly input: string;
  readonly inputDisabled: boolean;
  readonly inputError: string;
  readonly onClose: () => void;
  readonly onInputChange: (value: string) => void;
  readonly onOpenActivity: () => void;
  readonly onStart: () => void;
  readonly open: boolean;
  readonly startDisabled: boolean;
  readonly starting: boolean;
  readonly target: WorkflowPublishedInvocationTarget | null;
};

const WorkflowPublishedRunDrawer: React.FC<WorkflowPublishedRunDrawerProps> = ({
  children,
  input,
  inputDisabled,
  inputError,
  onClose,
  onInputChange,
  onOpenActivity,
  onStart,
  open,
  startDisabled,
  starting,
  target,
}) => {
  const inputRef = React.useRef<React.ElementRef<typeof Input.TextArea>>(null);

  return (
    <Drawer
      afterOpenChange={(drawerOpen) => {
        if (drawerOpen) inputRef.current?.focus();
      }}
      aria-label={t(
        'workflowActivityVNext.editor.publishedRunDrawer',
        'Run published workflow',
      )}
      destroyOnHidden={false}
      onClose={onClose}
      open={open}
      placement="right"
      size="min(480px, 100vw)"
      title={t(
        'workflowActivityVNext.editor.publishedRunDrawer',
        'Run published workflow',
      )}
    >
      <Space
        className="wa-vnext__run-panel-content"
        orientation="vertical"
        size="middle"
        style={{ width: '100%' }}
      >
        {target ? (
          <dl className="wa-vnext__technical-grid">
            <div>
              <dt>
                {t(
                  'workflowActivityVNext.publish.publishedServiceId',
                  'Published service ID',
                )}
              </dt>
              <dd className="wa-vnext__mono">{target.publishedServiceId}</dd>
            </div>
            <div>
              <dt>
                {t('workflowActivityVNext.publish.revisionId', 'Revision ID')}
              </dt>
              <dd className="wa-vnext__mono">{target.revisionId}</dd>
            </div>
          </dl>
        ) : null}
        <div className="wa-vnext__run-input-field">
          <div className="wa-vnext__run-input-heading">
            <label htmlFor="wa-vnext-run-input">
              {t('workflowActivityVNext.editor.runInput', 'Input')}
            </label>
            <span>
              {t(
                'workflowActivityVNext.editor.runInputRequiredTag',
                'Required',
              )}
            </span>
          </div>
          <p id="wa-vnext-run-input-help">
            {t(
              'workflowActivityVNext.editor.runInputHelp',
              'This workflow accepts one text input. For example: Review order 42.',
            )}
          </p>
          <Input.TextArea
            aria-describedby="wa-vnext-run-input-help wa-vnext-run-input-error"
            aria-invalid={Boolean(inputError)}
            aria-label={t('workflowActivityVNext.editor.runInput', 'Input')}
            disabled={inputDisabled}
            id="wa-vnext-run-input"
            onChange={(event) => onInputChange(event.target.value)}
            placeholder={t(
              'workflowActivityVNext.editor.runInputExample',
              'For example: Review order 42',
            )}
            ref={inputRef}
            rows={5}
            value={input}
          />
          {inputError ? (
            <p className="wa-vnext__field-error" id="wa-vnext-run-input-error">
              {inputError}
            </p>
          ) : null}
        </div>
        <Space wrap>
          <Button
            disabled={startDisabled}
            loading={starting}
            onClick={onStart}
            type="primary"
          >
            {t('workflowActivityVNext.editor.submitRun', 'Start run')}
          </Button>
          <Button onClick={onClose}>
            {t('workflowActivityVNext.common.close', 'Close')}
          </Button>
          <Button onClick={onOpenActivity}>
            {t('workflowActivityVNext.editor.openActivity', 'Open Activity')}
          </Button>
        </Space>
        {children}
      </Space>
    </Drawer>
  );
};

export default WorkflowPublishedRunDrawer;
