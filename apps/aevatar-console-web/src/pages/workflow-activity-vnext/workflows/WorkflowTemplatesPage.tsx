import { ArrowLeftOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { buildWorkflowActivityNewHref } from '../navigation';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import WorkflowTemplateBrowser from './WorkflowTemplateBrowser';

const WorkflowTemplatesPage: React.FC<{ readonly scopeId: string }> = ({
  scopeId,
}) => (
  <WorkflowActivityVNextShell
    activeSection="workflows"
    description={t(
      'workflowActivityVNext.new.templateBrowser.description',
      'Browse public templates, inspect details, or create a draft directly.',
    )}
    headerActions={
      <Button
        icon={<ArrowLeftOutlined aria-hidden="true" />}
        onClick={() => history.push(buildWorkflowActivityNewHref(scopeId))}
      >
        {t(
          'workflowActivityVNext.new.templateBrowser.changeMethod',
          'Change method',
        )}
      </Button>
    }
    scopeId={scopeId}
    title={t(
      'workflowActivityVNext.new.templateBrowser.title',
      'Start from a template',
    )}
  >
    <WorkflowTemplateBrowser scopeId={scopeId} />
  </WorkflowActivityVNextShell>
);

export default WorkflowTemplatesPage;
