import { PageContainer, ProCard } from '@ant-design/pro-components';
import { Alert, Button, Space } from 'antd';
import React, { useEffect, useMemo } from 'react';
import { history } from '@/shared/navigation/history';
import { loadPlaygroundDraft } from '@/shared/playground/playgroundDraft';
import { buildStudioRoute } from '@/shared/studio/navigation';
import {
  cardStackStyle,
  fillCardStyle,
  moduleCardProps,
} from '@/shared/ui/proComponents';

function trimOptional(value: string | null): string {
  return value?.trim() ?? '';
}

function buildLegacyPlaygroundRedirectTarget(): string {
  if (typeof window === 'undefined') {
    return buildStudioRoute({ draftMode: 'new', tab: 'studio' });
  }

  const draft = loadPlaygroundDraft();
  const params = new URLSearchParams(window.location.search);
  const template = trimOptional(params.get('template'));
  const prompt = trimOptional(params.get('prompt')) || draft.prompt;

  if (template) {
    return buildStudioRoute({
      template,
      tab: 'studio',
      prompt,
    });
  }

  if (draft.yaml.trim()) {
    return buildStudioRoute({
      draftMode: 'new',
      tab: 'studio',
      prompt,
      legacySource: 'playground',
    });
  }

  if (draft.sourceWorkflow) {
    return buildStudioRoute({
      template: draft.sourceWorkflow,
      tab: 'studio',
      prompt,
    });
  }

  return buildStudioRoute({
    draftMode: 'new',
    tab: 'studio',
    prompt,
  });
}

const PlaygroundPage: React.FC = () => {
  const redirectTarget = useMemo(buildLegacyPlaygroundRedirectTarget, []);

  useEffect(() => {
    history.replace(redirectTarget);
  }, [redirectTarget]);

  return (
    <PageContainer
      title="Playground"
      content="This entry forwards directly to Studio."
    >
      <ProCard {...moduleCardProps} style={fillCardStyle}>
        <div style={cardStackStyle}>
          <Alert
            showIcon
            type="info"
            title="Opening Studio"
            description="Playground now forwards to Studio for workflow editing and runs."
          />
          <Space wrap size={[8, 8]}>
            <Button type="primary" onClick={() => history.replace(redirectTarget)}>
              Continue to Studio
            </Button>
            <Button
              onClick={() =>
                history.replace(buildStudioRoute({ draftMode: 'new', tab: 'studio' }))
              }
            >
              Open blank Studio draft
            </Button>
          </Space>
        </div>
      </ProCard>
    </PageContainer>
  );
};

export default PlaygroundPage;
