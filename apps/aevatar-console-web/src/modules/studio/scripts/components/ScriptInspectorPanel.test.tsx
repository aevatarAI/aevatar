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

    expect(screen.getAllByText('script-1').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('rev-2')).toBeTruthy();
    expect(screen.getByText('Resolved scope · scope-1')).toBeTruthy();
    expect(screen.getByText('type.googleapis.com/example.Command')).toBeTruthy();
    expect(screen.getByText('Embedded')).toBeTruthy();
    expect(
      screen.getByText('Validate, Save, Promote, Draft Run, Ask AI'),
    ).toBeTruthy();
    expect(screen.getByText('None')).toBeTruthy();
    expect(screen.getByText('definition-1')).toBeTruthy();
    expect(screen.getByText('runtime-1')).toBeTruthy();
    expect(screen.getByText('catalog-1')).toBeTruthy();
    expect(screen.getByText('DraftBehavior')).toBeTruthy();
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

    expect(screen.getByText('Proxy')).toBeTruthy();
    expect(screen.getByText('Validate, Save, Promote')).toBeTruthy();
    expect(
      screen.getByText(
        'Draft Run (requires embedded host), Ask AI (requires embedded host)',
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        'Proxy host. Validate is available here, and Save or Promote still work after Studio resolves the current scope. Draft Run and Ask AI require an embedded host.',
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

    expect(screen.getByText('No draft selected.')).toBeTruthy();
  });
});
