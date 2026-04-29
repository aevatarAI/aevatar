import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
import { history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import ScriptsWorkbenchPage from './ScriptsWorkbenchPage';

jest.mock('@/shared/studio/scriptsApi', () => ({
  scriptsApi: {
    validateDraft: jest.fn(),
    listScripts: jest.fn(),
    getScript: jest.fn(),
    getScriptCatalog: jest.fn(),
    listRuntimes: jest.fn(),
    getRuntimeReadModel: jest.fn(),
    getEvolutionDecision: jest.fn(),
    saveScript: jest.fn(),
    observeSaveScript: jest.fn(),
    runDraftScript: jest.fn(),
    proposeEvolution: jest.fn(),
    generateScript: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    bindScopeScript: jest.fn(),
  },
}));

jest.mock('@/shared/navigation/history', () => ({
  history: {
    push: jest.fn(),
  },
}));

const mockedScriptsApi = scriptsApi as unknown as {
  validateDraft: jest.Mock;
  listScripts: jest.Mock;
  getScript: jest.Mock;
  getScriptCatalog: jest.Mock;
  listRuntimes: jest.Mock;
  getRuntimeReadModel: jest.Mock;
  getEvolutionDecision: jest.Mock;
  saveScript: jest.Mock;
  observeSaveScript: jest.Mock;
  runDraftScript: jest.Mock;
  proposeEvolution: jest.Mock;
  generateScript: jest.Mock;
};

const mockedStudioApi = studioApi as unknown as {
  bindScopeScript: jest.Mock;
};

const mockedHistory = history as unknown as {
  push: jest.Mock;
};

const appContext = {
  mode: 'proxy',
  scopeId: 'scope-1',
  scopeResolved: true,
  scopeSource: 'workspace',
  workflowStorageMode: 'workspace',
  scriptStorageMode: 'scope',
  features: {
    publishedWorkflows: true,
    scripts: true,
  },
  scriptContract: {
    inputType: 'type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand',
    readModelFields: ['input', 'output'],
  },
} as const;

const validationResult = {
  success: true,
  scriptId: 'script-1',
  scriptRevision: 'rev-1',
  primarySourcePath: 'Behavior.cs',
  errorCount: 0,
  warningCount: 0,
  diagnostics: [],
};

const acceptedSaveResponse = {
  acceptedScript: {
    scopeId: 'scope-1',
    scriptId: 'script-1',
    catalogActorId: 'catalog-1',
    definitionActorId: 'definition-1',
    revisionId: 'rev-1',
    sourceHash: 'hash-1',
    acceptedAt: '2026-03-24T00:00:00Z',
    proposalId: 'scope-1:script-1:rev-1',
    expectedBaseRevision: 'rev-0',
  },
  definitionCommand: {
    actorId: 'definition-1',
    commandId: 'definition-command-1',
    correlationId: 'definition-correlation-1',
  },
  catalogCommand: {
    actorId: 'catalog-1',
    commandId: 'catalog-command-1',
    correlationId: 'catalog-correlation-1',
  },
};

function renderPage(
  overrideContext = {},
  overrideProps: Record<string, unknown> = {},
) {
  return renderWithQueryClient(
    React.createElement(ScriptsWorkbenchPage, {
      appContext: {
        ...appContext,
        ...overrideContext,
      },
      ...overrideProps,
    }),
  );
}

describe('ScriptsWorkbenchPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
    jest.clearAllMocks();

    mockedScriptsApi.validateDraft.mockResolvedValue(validationResult);
    mockedScriptsApi.listScripts.mockResolvedValue([]);
    mockedScriptsApi.listRuntimes.mockResolvedValue([]);
    mockedScriptsApi.getRuntimeReadModel.mockResolvedValue({
      actorId: 'runtime-1',
      scriptId: 'script-1',
      definitionActorId: 'definition-1',
      revision: 'draft-1',
      readModelTypeUrl: 'type.googleapis.com/example.ReadModel',
      readModelPayloadJson: '{"status":"ok"}',
      stateVersion: 1,
      lastEventId: 'event-1',
      updatedAt: '2026-03-24T00:00:00Z',
    });
    mockedScriptsApi.getScriptCatalog.mockResolvedValue({
      scriptId: 'script-1',
      activeRevision: 'rev-1',
      activeDefinitionActorId: 'definition-1',
      activeSourceHash: 'hash-1',
      previousRevision: '',
      revisionHistory: ['rev-1'],
      lastProposalId: '',
      catalogActorId: 'catalog-1',
      scopeId: 'scope-1',
      updatedAt: '2026-03-24T00:00:00Z',
    });
    mockedScriptsApi.getEvolutionDecision.mockResolvedValue({
      accepted: true,
      proposalId: 'proposal-1',
      scriptId: 'script-1',
      baseRevision: 'rev-1',
      candidateRevision: 'rev-2',
      status: 'accepted',
      failureReason: '',
      definitionActorId: 'definition-1',
      catalogActorId: 'catalog-1',
      validationReport: {
        isSuccess: true,
        diagnostics: [],
      },
    });
    mockedScriptsApi.saveScript.mockResolvedValue(acceptedSaveResponse);
    mockedScriptsApi.observeSaveScript.mockResolvedValue({
      scopeId: 'scope-1',
      scriptId: 'script-1',
      status: 'applied',
      message: 'Revision active.',
      currentScript: {
        scopeId: 'scope-1',
        scriptId: 'script-1',
        catalogActorId: 'catalog-1',
        definitionActorId: 'definition-1',
        activeRevision: 'rev-1',
        activeSourceHash: 'hash-1',
        updatedAt: '2026-03-24T00:00:00Z',
      },
      isTerminal: true,
    });
    mockedScriptsApi.getScript.mockResolvedValue({
      available: true,
      scopeId: 'scope-1',
      script: {
        scopeId: 'scope-1',
        scriptId: 'script-1',
        catalogActorId: 'catalog-1',
        definitionActorId: 'definition-1',
        activeRevision: 'rev-1',
        activeSourceHash: 'hash-1',
        updatedAt: '2026-03-24T00:00:00Z',
      },
      source: {
        sourceText: 'public sealed class DemoScript {}',
        definitionActorId: 'definition-1',
        revision: 'rev-1',
        sourceHash: 'hash-1',
      },
    });
    mockedScriptsApi.runDraftScript.mockResolvedValue({
      accepted: true,
      scopeId: 'scope-1',
      scriptId: 'script-1',
      scriptRevision: 'draft-1',
      definitionActorId: 'definition-1',
      runtimeActorId: 'runtime-1',
      runId: 'run-1',
      sourceHash: 'hash-1',
      commandTypeUrl: 'type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand',
      readModelUrl: '/api/app/scripts/runtimes/runtime-1/readmodel',
    });
    mockedScriptsApi.proposeEvolution.mockResolvedValue({
      accepted: true,
      proposalId: 'proposal-1',
      scriptId: 'script-1',
      baseRevision: 'rev-1',
      candidateRevision: 'rev-2',
      status: 'accepted',
      failureReason: '',
      definitionActorId: 'definition-1',
      catalogActorId: 'catalog-1',
      validationReport: {
        isSuccess: true,
        diagnostics: [],
      },
    });
    mockedScriptsApi.generateScript.mockResolvedValue({
      text: 'using System;',
      scriptPackage: null,
      currentFilePath: 'Behavior.cs',
    });
    mockedStudioApi.bindScopeScript.mockResolvedValue({
      scopeId: 'scope-1',
      displayName: 'script-1',
      targetKind: 'script',
      targetName: 'script-1',
      revisionId: 'rev-1',
    });
  });

  afterEach(() => {
    cleanupTestQueryClients();
  });

  it('saves the active draft when Ctrl+S is pressed', async () => {
    renderPage();

    await screen.findByLabelText('Script ID');
    fireEvent.keyDown(window, {
      key: 's',
      ctrlKey: true,
    });

    await waitFor(() => {
      expect(mockedScriptsApi.saveScript).toHaveBeenCalledTimes(1);
    });
  });

  it('uses the latest resolved scope when saving after a scope switch', async () => {
    function Harness() {
      const [scopeId, setScopeId] = React.useState<string>(appContext.scopeId);

      return React.createElement(
        React.Fragment,
        null,
        React.createElement(
          'button',
          {
            type: 'button',
            onClick: () => setScopeId('scope-2'),
          },
          'Switch scope',
        ),
        React.createElement(ScriptsWorkbenchPage, {
          appContext: {
            ...appContext,
            scopeId,
          },
        }),
      );
    }

    renderWithQueryClient(React.createElement(Harness));

    await screen.findByLabelText('Script ID');
    fireEvent.click(screen.getByRole('button', { name: 'Switch scope' }));

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(mockedScriptsApi.saveScript).toHaveBeenCalledWith(
        'scope-2',
        expect.objectContaining({
          scriptId: expect.any(String),
        }),
      );
    });
  });

  it('retries transient save-observation failures before surfacing an error', async () => {
    mockedScriptsApi.observeSaveScript
      .mockRejectedValueOnce(new Error('temporary timeout'))
      .mockResolvedValueOnce({
        scopeId: 'scope-1',
        scriptId: 'script-1',
        status: 'applied',
        message: 'Revision active.',
        currentScript: {
          scopeId: 'scope-1',
          scriptId: 'script-1',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-24T00:00:00Z',
        },
        isTerminal: true,
      });

    renderPage();

    await screen.findByLabelText('Script ID');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(mockedScriptsApi.observeSaveScript).toHaveBeenCalledTimes(2);
    });
    expect(
      await screen.findByText('Saved script-1 into current scope scope-1.'),
    ).toBeTruthy();
  });

  it('boots a fresh draft with the app script starter contract', async () => {
    renderPage();

    await screen.findByLabelText('Script ID');

    expect(
      screen.getByDisplayValue(/Aevatar\.Studio\.Application\.Scripts\.Contracts/),
    ).toBeTruthy();
  });

  it('registers a leave guard that confirms before leaving unsaved scope changes', async () => {
    let registeredLeaveGuard: (() => Promise<boolean>) | null = null;
    window.localStorage.setItem(
      'aevatar:console:scripts-studio:v1',
      JSON.stringify([
        {
          key: 'draft-guard',
          scriptId: 'script-1',
          revision: 'draft-guard',
          baseRevision: 'rev-1',
          input: '',
          package: {
            csharpSources: [
              {
                path: 'Behavior.cs',
                content: 'public sealed class DemoScript {}',
              },
            ],
            protoFiles: [],
            entryBehaviorTypeName: 'DraftBehavior',
            entrySourcePath: 'Behavior.cs',
          },
          selectedFilePath: 'Behavior.cs',
          definitionActorId: 'definition-1',
          runtimeActorId: 'runtime-1',
          updatedAtUtc: '2026-03-24T00:00:00Z',
          lastSourceHash: 'hash-1',
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
              updatedAt: '2026-03-24T00:00:00Z',
            },
            source: {
              sourceText: 'public sealed class DemoScript {}',
              definitionActorId: 'definition-1',
              revision: 'rev-1',
              sourceHash: 'hash-1',
            },
          },
        },
      ]),
    );

    renderPage(
      {},
      {
        onRegisterLeaveGuard: (guard: (() => Promise<boolean>) | null) => {
          registeredLeaveGuard = guard;
        },
      },
    );

    await screen.findByLabelText('Script ID');
    const sourceEditor = screen
      .getAllByRole('textbox', { hidden: true })
      .find((element) => {
        if (element.getAttribute('aria-hidden') === 'true') {
          return false;
        }
        const value = (element as HTMLInputElement | HTMLTextAreaElement).value;
        return value.includes('DemoScript') || value.includes('DraftBehavior');
      });

    expect(sourceEditor).toBeTruthy();

    fireEvent.change(sourceEditor!, {
      target: {
        value: `${(sourceEditor as HTMLTextAreaElement).value}\n// leave-guard`,
      },
    });

    await waitFor(() => {
      expect(registeredLeaveGuard).toBeTruthy();
      expect(
        (sourceEditor as HTMLTextAreaElement).value.includes('// leave-guard'),
      ).toBe(true);
    });

    let blockedLeave: Promise<boolean> | null = null;
    await act(async () => {
      blockedLeave = registeredLeaveGuard?.() || null;
    });

    expect(blockedLeave).not.toBeNull();
    expect(await screen.findByText('Leave Scripts Studio?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Continue editing' }));
    await expect(blockedLeave!).resolves.toBe(false);
    await waitFor(() => {
      expect(screen.queryByText('Leave Scripts Studio?')).toBeNull();
    });

    let allowedLeave: Promise<boolean> | null = null;
    await act(async () => {
      allowedLeave = registeredLeaveGuard?.() || null;
    });

    expect(allowedLeave).not.toBeNull();
    expect(await screen.findByText('Leave Scripts Studio?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Leave page' }));
    await expect(allowedLeave!).resolves.toBe(true);
  });

  it('migrates the broken legacy starter script from local storage', async () => {
    window.localStorage.setItem(
      'aevatar:console:scripts-studio:v1',
      JSON.stringify([
        {
          key: 'draft-legacy',
          scriptId: 'script-legacy',
          revision: 'draft-legacy',
          source: `using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Tools.Cli.Hosting;

public sealed class DraftBehavior : ScriptBehavior<AppScriptReadModel, AppScriptReadModel>
{
    protected override void Configure(IScriptBehaviorBuilder<AppScriptReadModel, AppScriptReadModel> builder)
    {
        builder
            .OnCommand<AppScriptCommand>(HandleAsync)
            .OnEvent<AppScriptUpdated>(
                apply: static (_, evt, _) => evt.Current == null ? new AppScriptReadModel() : evt.Current.Clone())
            .ProjectState(static (state, _) => state == null ? new AppScriptReadModel() : state.Clone());
    }

    private static Task HandleAsync(
        AppScriptCommand input,
        ScriptCommandContext<AppScriptReadModel> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var commandId = context.CommandId ?? input?.CommandId ?? string.Empty;
        var text = input?.Input ?? string.Empty;
        var current = AppScriptProtocol.CreateState(
            text,
            text.Trim().ToUpperInvariant(),
            "ok",
            commandId,
            new[]
            {
                "trimmed",
                "uppercased",
            });

        context.Emit(new AppScriptUpdated
        {
            CommandId = commandId,
            Current = current,
        });
        return Task.CompletedTask;
    }
}
`,
        },
      ]),
    );

    renderPage();

    await screen.findByLabelText('Script ID');

    expect(
      screen.getByDisplayValue(/Aevatar\.Studio\.Application\.Scripts\.Contracts/),
    ).toBeTruthy();
    expect(screen.queryByDisplayValue(/Aevatar\.Tools\.Cli\.Hosting/)).toBeNull();
  });

  it('adds and removes package files through in-app dialogs', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: '添加 C# 文件' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'File path' }), {
      target: {
        value: 'Handlers/EmailValidator.cs',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));

    await waitFor(() => {
      expect(screen.getAllByText('Handlers/EmailValidator.cs').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getByRole('button', { name: '删除 Handlers/EmailValidator.cs' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));

    await waitFor(() => {
      expect(screen.queryByText('Handlers/EmailValidator.cs')).toBeNull();
    });
  });

  it('opens the diagnostic file when a compiler problem is selected', async () => {
    mockedScriptsApi.validateDraft.mockResolvedValue({
      success: false,
      scriptId: 'script-1',
      scriptRevision: 'rev-1',
      primarySourcePath: 'schema.proto',
      errorCount: 1,
      warningCount: 0,
      diagnostics: [
        {
          severity: 'error',
          code: 'PROTO001',
          message: 'Message name is required',
          filePath: 'schema.proto',
          startLine: 3,
          startColumn: 2,
          endLine: 3,
          endColumn: 10,
          origin: 'compiler',
        },
      ],
    });

    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: '添加 Proto 文件' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));

    await waitFor(() => {
      expect(screen.getByText('Message name is required')).toBeTruthy();
    });

    fireEvent.click(
      screen
        .getAllByRole('button', { name: /Behavior\.cs/ })
        .find((element) => element.className === 'console-scripts-package-file-main')!,
    );
    await waitFor(() => {
      expect(
        document.querySelector('.console-scripts-package-file.active .console-scripts-package-file-path')
          ?.textContent,
      ).toBe('Behavior.cs');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Problems 1' }));
    fireEvent.click(screen.getByRole('button', { name: /Message name is required/ }));

    await waitFor(() => {
      expect(
        document.querySelector('.console-scripts-package-file.active .console-scripts-package-file-path')
          ?.textContent,
      ).toBe('schema.proto');
    });
  });

  it('shows the simplified header layout with compact context chips', async () => {
    const { container } = renderPage({
      mode: 'embedded',
      scopeId: '1626c177-917b-4fcc-a5ee-aa74a171b0d6',
    });

    await screen.findByLabelText('Script ID');

    const header = container.querySelector('.console-scripts-header');
    expect(header).toBeTruthy();

    const headerScope = within(header as HTMLElement);
    expect(headerScope.getByText('not saved')).toBeTruthy();
    expect(headerScope.getByText('嵌入式 Host')).toBeTruthy();
    expect(headerScope.getByText('Scope 1626c177…b0d6')).toBeTruthy();
    expect(headerScope.getByRole('button', { name: 'New draft' })).toBeTruthy();
    expect(headerScope.getByRole('button', { name: 'Save' })).toBeTruthy();
    expect(
      headerScope.getByRole('button', { name: 'Update default route' }),
    ).toBeTruthy();
    fireEvent.click(
      headerScope.getByRole('button', { name: 'More script actions' }),
    );
    expect(await screen.findByText('Validate')).toBeTruthy();
    expect(screen.getByText('Promote')).toBeTruthy();
    expect(screen.getByText('Test Run')).toBeTruthy();
  });

  it('creates a new draft directly from the header without opening the panels drawer', async () => {
    const { container } = renderPage({
      mode: 'embedded',
    });

    const scriptIdInput = (await screen.findByLabelText('Script ID')) as HTMLInputElement;
    expect(scriptIdInput.value).toBe('script-1');

    const header = container.querySelector('.console-scripts-header');
    expect(header).toBeTruthy();

    fireEvent.click(
      within(header as HTMLElement).getByRole('button', { name: 'New draft' }),
    );

    await waitFor(() => {
      expect(
        (screen.getByLabelText('Script ID') as HTMLInputElement).value,
      ).toBe('script-2');
    });
    expect(await screen.findByText('Created script-2.')).toBeTruthy();
  });

  it('keeps proxy mode gated for testing and AI actions', async () => {
    renderPage({
      mode: 'proxy',
    });

    await screen.findByLabelText('Script ID');

    const askAiTrigger = screen.getByRole('button', {
      name: 'Ask AI to generate script code.',
    });
    expect(askAiTrigger.hasAttribute('disabled')).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'More script actions' }));
    expect(await screen.findByText('Validate')).toBeTruthy();
    expect(screen.getByText('Promote')).toBeTruthy();
    expect(
      screen.getByText('Test Run').closest('.ant-dropdown-menu-item-disabled'),
    ).toBeTruthy();
  });

  it('cancels Ask AI generation from the floating panel', async () => {
    mockedScriptsApi.generateScript.mockImplementation(
      (_input, options) =>
        new Promise((_resolve, reject) => {
          const abortError = new Error('Aborted');
          abortError.name = 'AbortError';
          options?.signal?.addEventListener('abort', () => reject(abortError));
        }),
    );

    renderPage({
      mode: 'embedded',
    });

    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Ask AI to generate script code.',
      }),
    );
    fireEvent.change(
      screen.getByPlaceholderText(
        'Build a script that validates an email address, normalizes it, and returns a JSON summary.',
      ),
      {
        target: {
          value: 'Generate a validator',
        },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

    await waitFor(() => {
      expect(mockedScriptsApi.generateScript).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => {
      expect(screen.getByText('Cancelled AI generation.')).toBeTruthy();
    });
  });

  it('binds the saved script to the default service for the current scope', async () => {
    renderPage({
      mode: 'embedded',
      scopeId: 'scope-1',
    });

    await screen.findByLabelText('Script ID');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(mockedScriptsApi.saveScript).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(
      screen.getByRole('button', { name: 'Update default route' }),
    );

    await waitFor(() => {
      expect(screen.getByText('Bind saved script')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Bind' }));

    await waitFor(() => {
      expect(mockedStudioApi.bindScopeScript).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        displayName: 'script-1',
        serviceId: 'script-1',
        scriptId: 'script-1',
        scriptRevision: 'rev-1',
      });
    });

    expect(
      await screen.findByText(
        'Review the active binding, revision rollout, and saved script assets from the team views.',
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        /Updated scope scope-1 to serve script script-1 on revision rev-1\./,
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Open Team Assets' }));

    expect(mockedHistory.push).toHaveBeenCalledWith(
      '/scopes/assets?scopeId=scope-1&tab=scripts&scriptId=script-1',
    );
  });

  it('blocks scope binding when the saved script has no applied revision', async () => {
    window.localStorage.setItem(
      'aevatar:console:scripts-studio:v1',
      JSON.stringify([
        {
          key: 'script-without-revision',
          scriptId: 'script-1',
          revision: '',
          baseRevision: '',
          input: '',
          package: {
            csharpSources: [
              {
                path: 'Behavior.cs',
                content: 'public sealed class DemoScript {}',
              },
            ],
            protoFiles: [],
            entryBehaviorTypeName: 'DraftBehavior',
            entrySourcePath: 'Behavior.cs',
          },
          selectedFilePath: 'Behavior.cs',
          definitionActorId: 'definition-1',
          runtimeActorId: 'runtime-1',
          updatedAtUtc: '2026-03-24T00:00:00Z',
          lastSourceHash: 'hash-1',
          scopeDetail: {
            available: true,
            scopeId: 'scope-1',
            script: {
              scopeId: 'scope-1',
              scriptId: 'script-1',
              catalogActorId: 'catalog-1',
              definitionActorId: 'definition-1',
              activeRevision: '',
              activeSourceHash: 'hash-1',
              updatedAt: '2026-03-24T00:00:00Z',
            },
            source: {
              sourceText: 'public sealed class DemoScript {}',
              definitionActorId: 'definition-1',
              revision: '',
              sourceHash: 'hash-1',
            },
          },
        },
      ]),
    );

    renderPage({
      mode: 'embedded',
      scopeId: 'scope-1',
    });

    const bindButton = await screen.findByRole('button', {
      name: 'Update default route',
    });
    expect(bindButton).toBeDisabled();
    expect(mockedStudioApi.bindScopeScript).not.toHaveBeenCalled();
  });

  it('runs the current script draft without rebinding the scope service', async () => {
    renderPage({
      mode: 'embedded',
      scopeId: 'scope-1',
    });

    await screen.findByLabelText('Script ID');
    await waitFor(() => {
      expect(screen.queryAllByText('Checking')).toHaveLength(0);
    });

    fireEvent.click(screen.getByRole('button', { name: 'More script actions' }));
    fireEvent.click(await screen.findByText('Test Run'));
    fireEvent.change(await screen.findByLabelText('Script test run input'), {
      target: {
        value: 'hello from draft run',
      },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Run draft' }));

    await waitFor(() => {
      expect(mockedScriptsApi.runDraftScript).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        scriptId: 'script-1',
        scriptRevision: undefined,
        source: expect.any(String),
        package: expect.objectContaining({
          format: 'aevatar.scripting.package.v1',
          entrySourcePath: 'Behavior.cs',
        }),
        input: 'hello from draft run',
        definitionActorId: undefined,
        runtimeActorId: undefined,
      });
      expect(mockedStudioApi.bindScopeScript).not.toHaveBeenCalled();
      expect(mockedHistory.push).not.toHaveBeenCalled();
    });
    expect(
      await screen.findByText(/Started draft run run-1 on runtime runtime-1\./),
    ).toBeTruthy();
  });
});
