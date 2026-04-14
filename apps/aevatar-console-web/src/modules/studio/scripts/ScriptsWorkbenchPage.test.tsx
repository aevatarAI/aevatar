import { fireEvent, screen, waitFor, within } from '@testing-library/react';
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

const savedScopeDetail = {
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
    sourceText: 'using System;',
    definitionActorId: 'definition-1',
    revision: 'rev-1',
    sourceHash: 'hash-1',
  },
};

function renderPage(overrideContext = {}) {
  return renderWithQueryClient(
    React.createElement(ScriptsWorkbenchPage, {
      appContext: {
        ...appContext,
        ...overrideContext,
      },
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
    mockedScriptsApi.saveScript.mockResolvedValue(savedScopeDetail);
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

  it('boots a fresh draft with the app script starter contract', async () => {
    renderPage();

    await screen.findByLabelText('Script ID');

    expect(
      screen.getByDisplayValue(/Aevatar\.Studio\.Application\.Scripts\.Contracts/),
    ).toBeTruthy();
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
    fireEvent.change(screen.getByRole('textbox', { name: '文件路径' }), {
      target: {
        value: 'Handlers/EmailValidator.cs',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: '添加文件' }));

    await waitFor(() => {
      expect(screen.getAllByText('Handlers/EmailValidator.cs').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getByRole('button', { name: '删除 Handlers/EmailValidator.cs' }));
    fireEvent.click(screen.getByRole('button', { name: '删除' }));

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
    fireEvent.click(screen.getByRole('button', { name: '添加文件' }));

    await waitFor(() => {
      expect(screen.getByText('诊断 1')).toBeTruthy();
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

    fireEvent.click(screen.getByRole('button', { name: /诊断 1/ }));
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
    expect(headerScope.getByText(/^draft-/)).toBeTruthy();
    expect(headerScope.getByText('嵌入式 Host')).toBeTruthy();
    expect(headerScope.getByText('团队 1626c177…b0d6')).toBeTruthy();
    expect(headerScope.getByRole('button', { name: '保存' })).toBeTruthy();
    expect(headerScope.getByRole('button', { name: '校验' })).toBeTruthy();
    expect(headerScope.getByRole('button', { name: '发布' })).toBeTruthy();
    expect(headerScope.getByRole('button', { name: '测试运行' })).toBeTruthy();
  });

  it('keeps proxy mode gated for testing and AI actions', async () => {
    renderPage({
      mode: 'proxy',
    });

    await screen.findByLabelText('Script ID');

    const askAiTrigger = screen.getByRole('button', {
      name: '使用 AI 生成脚本代码。',
    });
    expect(askAiTrigger.hasAttribute('disabled')).toBe(true);
    expect(screen.getByRole('button', { name: '校验' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '发布' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '测试运行' })).toBeDisabled();
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
        name: '使用 AI 生成脚本代码。',
      }),
    );
    fireEvent.change(
      screen.getByPlaceholderText(
        '构建一个脚本：校验邮箱、完成标准化处理，并返回 JSON 摘要。',
      ),
      {
        target: {
          value: 'Generate a validator',
        },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: '生成' }));

    await waitFor(() => {
      expect(mockedScriptsApi.generateScript).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(screen.getByRole('button', { name: '取消' }));

    await waitFor(() => {
      expect(screen.getByText('已取消 AI 生成。')).toBeTruthy();
    });
  });

  it('binds the saved script to the default service for the current scope', async () => {
    renderPage({
      mode: 'embedded',
      scopeId: 'scope-1',
    });

    await screen.findByLabelText('Script ID');
    fireEvent.click(screen.getByRole('button', { name: '保存' }));

    await waitFor(() => {
      expect(mockedScriptsApi.saveScript).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(screen.getByRole('button', { name: '脚本信息' }));
    fireEvent.click(screen.getByRole('button', { name: '绑定到团队' }));

    await waitFor(() => {
      expect(screen.getByText('绑定已保存脚本')).toBeTruthy();
    });

    fireEvent.click(screen.getByRole('button', { name: '确认绑定' }));

    await waitFor(() => {
      expect(mockedStudioApi.bindScopeScript).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        displayName: 'script-1',
        scriptId: 'script-1',
        scriptRevision: 'rev-1',
        revisionId: 'script-1-rev-1',
      });
    });

    expect(
      await screen.findByText(
        '你可以回到团队页查看当前入口、版本发布和已保存脚本。',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '打开团队资产' }));

    expect(mockedHistory.push).toHaveBeenCalledWith(
      '/scopes/assets?scopeId=scope-1&tab=scripts&scriptId=script-1',
    );
  });

  it('runs the current script draft without rebinding the scope service', async () => {
    renderPage({
      mode: 'embedded',
      scopeId: 'scope-1',
    });

    await screen.findByLabelText('Script ID');
    await waitFor(() => {
      expect(screen.queryAllByText('校验中')).toHaveLength(0);
    });

    fireEvent.click(screen.getByRole('button', { name: '测试运行' }));
    fireEvent.change(await screen.findByLabelText('脚本测试输入'), {
      target: {
        value: 'hello from draft run',
      },
    });
    fireEvent.click(await screen.findByRole('button', { name: '开始运行' }));

    await waitFor(() => {
      expect(mockedScriptsApi.runDraftScript).toHaveBeenCalledWith({
        scopeId: 'scope-1',
        scriptId: 'script-1',
        scriptRevision: expect.stringMatching(/^draft-/),
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
    expect(await screen.findByText(/已启动测试运行 run-1，运行实例 runtime-1。/)).toBeTruthy();
  });
});
