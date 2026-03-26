import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
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

const mockedScriptsApi = scriptsApi as {
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
    inputType: 'type.googleapis.com/example.Command',
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
      scriptRevision: 'rev-1',
      definitionActorId: 'definition-1',
      runtimeActorId: 'runtime-1',
      runId: 'run-1',
      sourceHash: 'hash-1',
      commandTypeUrl: 'type.googleapis.com/example.Command',
      readModelUrl: 'type.googleapis.com/example.ReadModel',
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

  it('adds and removes package files through in-app dialogs', async () => {
    renderPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Add C# file' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'File path' }), {
      target: {
        value: 'Handlers/EmailValidator.cs',
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));

    await waitFor(() => {
      expect(screen.getAllByText('Handlers/EmailValidator.cs').length).toBeGreaterThan(0);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Remove Handlers/EmailValidator.cs' }));
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

    fireEvent.click(await screen.findByRole('button', { name: 'Add proto file' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add file' }));

    await waitFor(() => {
      expect(screen.getByText('Problems 1')).toBeTruthy();
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

    fireEvent.click(screen.getByRole('button', { name: /Problems 1/ }));
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
    expect(headerScope.getByText('Embedded')).toBeTruthy();
    expect(headerScope.getByText('Scope 1626c177…b0d6')).toBeTruthy();
    expect(headerScope.getByRole('button', { name: 'Save' })).toBeTruthy();
    expect(headerScope.getByRole('button', { name: 'More script actions' })).toBeTruthy();

    fireEvent.click(headerScope.getByRole('button', { name: 'More script actions' }));

    expect(await screen.findByText('Validate')).toBeTruthy();
    expect(screen.getByText('Promote')).toBeTruthy();
    expect(screen.getByText('Draft Run')).toBeTruthy();
  });

  it('gates embedded-only actions when running in proxy mode', async () => {
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
    expect(screen.queryByText('Draft Run')).toBeNull();
    expect(
      screen.getByText('Draft Run and Ask AI require an embedded host.'),
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
});
