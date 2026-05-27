import type { StudioBindContract } from './bindContract';
import { translate } from '@/shared/i18n/localization';

function escapeForDoubleQuotes(value: string): string {
  return value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

function escapeForTemplateLiteral(value: string): string {
  return value.replace(/\\/g, '\\\\').replace(/`/g, '\\`').replace(/\$/g, '\\$');
}

function buildRequestBody(sampleInput: string): string {
  const normalizedInput = sampleInput.trim() || translate('studio.snippets.defaultSmoke');
  return JSON.stringify(
    {
      prompt: normalizedInput,
    },
    null,
    2,
  );
}

function buildAuthSnippetComment(contract: StudioBindContract): string {
  if (!contract.authEnabled) {
    return `// ${translate('studio.snippets.authNoSession')}`;
  }

  if (!contract.authAuthenticated) {
    return `// ${translate('studio.snippets.authRequired')}`;
  }

  return `// ${translate('studio.snippets.authBrowser')}`;
}

export function createDefaultBindSampleInput(
  contract: StudioBindContract | null,
): string {
  if (!contract) {
    return '';
  }

  return contract.streaming.sse
    ? translate('studio.snippets.defaultStreaming')
    : translate('studio.snippets.defaultSmoke');
}

export function buildCurlSnippet(
  contract: StudioBindContract,
  sampleInput: string,
): string {
  const body = buildRequestBody(sampleInput);
  const acceptHeader = contract.streaming.sse
    ? '  -H "Accept: text/event-stream" \\\n'
    : '  -H "Accept: application/json" \\\n';

  return [
    buildAuthSnippetComment(contract),
    `curl -X ${contract.method} "${contract.invokeUrl}" \\`,
    '  -H "Authorization: Bearer <bearer-access-token>" \\',
    '  -H "Content-Type: application/json" \\',
    acceptHeader.trimEnd(),
    `  -d '${body.replace(/\n/g, '\n  ')}'`,
  ].join('\n');
}

export function buildFetchSnippet(
  contract: StudioBindContract,
  sampleInput: string,
): string {
  const body = buildRequestBody(sampleInput);
  const acceptValue = contract.streaming.sse
    ? 'text/event-stream'
    : 'application/json';
  const responseHandling = contract.streaming.sse
    ? [
        `// ${translate('studio.snippets.parseStreaming')}`,
        'const text = await response.text();',
        'console.log(text);',
      ].join('\n')
    : [
        'const payload = await response.json();',
        'console.log(payload);',
      ].join('\n');

  return [
    buildAuthSnippetComment(contract),
    '',
    `const response = await fetch("${escapeForDoubleQuotes(contract.invokeUrl)}", {`,
    `  method: "${contract.method}",`,
    '  headers: {',
    '    "Authorization": "Bearer <bearer-access-token>",',
    '    "Content-Type": "application/json",',
    `    "Accept": "${acceptValue}",`,
    '  },',
    `  body: JSON.stringify(${body}),`,
    '});',
    '',
    'if (!response.ok) {',
    '  throw new Error(await response.text());',
    '}',
    '',
    responseHandling,
  ].join('\n');
}

export function buildSdkSnippet(
  contract: StudioBindContract,
  sampleInput: string,
): string {
  return [
    `// ${translate('studio.snippets.sdkUnavailable')}`,
    `// ${translate('studio.snippets.sdkUseFetch')}`,
    `// ${translate('studio.snippets.service', {
      service: escapeForTemplateLiteral(contract.serviceId),
    })}`,
    `// ${translate('studio.snippets.endpoint', {
      endpoint: escapeForTemplateLiteral(contract.endpointId),
    })}`,
    `// ${translate('studio.snippets.sampleInput', {
      input: escapeForTemplateLiteral(sampleInput.trim() || createDefaultBindSampleInput(contract)),
    })}`,
  ].join('\n');
}
