import { screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import type { StudioAppContext } from '@/shared/studio/models';
import type { ScriptDraft } from '@/shared/studio/scriptsModels';
import ScriptInspectorPanel from './ScriptInspectorPanel';

const appContext: StudioAppContext = {
  mode: 'embedded',
  scopeId: 'scope-1',
  scopeResolved: true,
  scopeSource: 'claim:scope_id',
  workflowStorageMode: 'scope',
  scriptStorageMode: 'scope',
  features: {
    publishedWorkflows: true,
    scripts: true,
  },
  scriptContract: {
    inputType: 'type.googleapis.com/example.Command',
    readModelFields: ['input', 'output', 'status'],
  },
};

const draft: ScriptDraft = {
  key: 'draft-1',
  scriptId: 'script-1',
  revision: 'rev-2',
  baseRevision: 'rev-1',
  reason: '',
  input: '',
  package: {
    format: 'aevatar.scripting.package.v1',
    csharpSources: [{ path: 'Behavior.cs', content: 'public sealed class DraftBehavior {}' }],
    protoFiles: [{ path: 'schema.proto', content: 'syntax = "proto3";' }],
    entryBehaviorTypeName: 'DraftBehavior',
    entrySourcePath: 'Behavior.cs',
  },
  selectedFilePath: 'Behavior.cs',
  definitionActorId: 'definition-1',
  runtimeActorId: 'runtime-1',
  updatedAtUtc: '2026-03-23T00:00:00Z',
  lastSourceHash: 'hash-1',
  lastRun: null,
  lastSnapshot: null,
  lastPromotion: null,
  scopeDetail: {
    available: true,
    scopeId: 'scope-1',
    script: {
      scopeId: 'scope-1',
      scriptId: 'script-1',
      catalogActorId: 'catalog-1',
      definitionActorId: 'definition-1',
      activeRevision: 'rev-1',
      activeSourceHash: 'hash-1',
      updatedAt: '2026-03-23T00:00:00Z',
    },
    source: null,
  },
};

describe('ScriptInspectorPanel', () => {
  it('renders draft, contract, actor, and scope details', () => {
    renderWithQueryClient(
      <ScriptInspectorPanel
        appContext={appContext}
        scopeBacked
        selectedDraft={draft}
      />,
    );

    expect(screen.getByText('script-1')).toBeTruthy();
    expect(screen.getByText('rev-2')).toBeTruthy();
    expect(screen.getByText('当前团队 · scope-1')).toBeTruthy();
    expect(screen.getByText('type.googleapis.com/example.Command')).toBeTruthy();
    expect(screen.getByText('嵌入式 Host')).toBeTruthy();
    expect(screen.getByText('校验, 保存, 发布, 测试运行, AI 辅助')).toBeTruthy();
    expect(screen.getByText('无')).toBeTruthy();
    expect(screen.getByText('definition-1')).toBeTruthy();
    expect(screen.getByText('runtime-1')).toBeTruthy();
    expect(screen.getByText('catalog-1')).toBeTruthy();
    expect(screen.getAllByText('DraftBehavior').length).toBeGreaterThanOrEqual(2);
  });

  it('explains proxy host capability limits', () => {
    renderWithQueryClient(
      <ScriptInspectorPanel
        appContext={{
          ...appContext,
          mode: 'proxy',
        }}
        scopeBacked
        selectedDraft={draft}
      />,
    );

    expect(screen.getByText('代理 Host')).toBeTruthy();
    expect(screen.getByText('校验, 保存, 发布')).toBeTruthy();
    expect(
      screen.getByText(
        '测试运行（需要嵌入式 Host）, AI 辅助（需要嵌入式 Host）',
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        '当前 Studio 会话运行在代理 Host 中。这里可以继续校验、保存和发布，但测试运行与 AI 辅助需要切换到嵌入式 Host。',
      ),
    ).toBeTruthy();
  });

  it('renders empty state when no draft is selected', () => {
    renderWithQueryClient(
      <ScriptInspectorPanel
        appContext={appContext}
        scopeBacked={false}
        selectedDraft={null}
      />,
    );

    expect(screen.getByText('还没有选中脚本。')).toBeTruthy();
  });
});
