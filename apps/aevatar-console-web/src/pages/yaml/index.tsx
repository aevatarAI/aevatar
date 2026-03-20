import { PageContainer, ProCard } from '@ant-design/pro-components';
import { history } from '@umijs/max';
import { Alert, Button, Space } from 'antd';
import React, { useEffect, useMemo } from 'react';
import { loadPlaygroundDraft } from '@/shared/playground/playgroundDraft';
import { buildStudioRoute } from '@/shared/studio/navigation';
import {
  cardStackStyle,
  fillCardStyle,
  moduleCardProps,
} from '@/shared/ui/proComponents';

type LegacyYamlSource = 'workflow' | 'playground';

function trimOptional(value: string | null): string {
  return value?.trim() ?? '';
}

function parseLegacyYamlSource(value: string | null): LegacyYamlSource {
  return value === 'playground' ? 'playground' : 'workflow';
}

function buildLegacyYamlRedirectTarget(): string {
  if (typeof window === 'undefined') {
    return buildStudioRoute({ draftMode: 'new', tab: 'studio' });
  }

  const draft = loadPlaygroundDraft();
  const params = new URLSearchParams(window.location.search);
  const source = parseLegacyYamlSource(params.get('source'));
  const workflow = trimOptional(params.get('workflow'));
  const prompt = draft.prompt;

  if (source === 'playground' && draft.yaml.trim()) {
    return buildStudioRoute({
      draftMode: 'new',
      tab: 'studio',
      prompt,
      legacySource: 'playground',
    });
  }

  if (workflow) {
    return buildStudioRoute({
      template: workflow,
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

  return buildStudioRoute({ draftMode: 'new', tab: 'studio', prompt });
}

const YamlPage: React.FC = () => {
  const redirectTarget = useMemo(buildLegacyYamlRedirectTarget, []);

  useEffect(() => {
    history.replace(redirectTarget);
  }, [redirectTarget]);

  return (
    <PageContainer
      title="YAML"
      content="This entry forwards directly to Studio."
    >
      <ProCard {...moduleCardProps} style={fillCardStyle}>
        <div style={cardStackStyle}>
          <Alert
            showIcon
            type="info"
            title="Opening Studio"
            description="YAML inspection and editing now happen in Studio."
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

export default YamlPage;
