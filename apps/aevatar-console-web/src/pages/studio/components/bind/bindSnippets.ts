import type { StudioBindContract } from './bindContract';

function escapeForDoubleQuotes(value: string): string {
  return value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

function escapeForTemplateLiteral(value: string): string {
  return value.replace(/\\/g, '\\\\').replace(/`/g, '\\`').replace(/\$/g, '\\$');
}

function buildRequestBody(sampleInput: string): string {
  const normalizedInput = sampleInput.trim() || 'Smoke test this member.';
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
    return "// This Studio environment does not expose an auth session for invoke requests.";
  }

  if (!contract.authAuthenticated) {
    return "// Sign in first, then supply a bearer access token when calling this endpoint outside Studio.";
  }

  return "// Studio browser requests reuse your authenticated session. External callers still need their own bearer access token.";
}

export function createDefaultBindSampleInput(
  contract: StudioBindContract | null,
): string {
  if (!contract) {
    return '';
  }

  return contract.streaming.sse
    ? 'Give me a quick summary of what this member can do.'
    : 'Smoke test this member.';
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
        '// Parse the streaming response in Invoke or your SSE client.',
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
    '// SDK snippet is not available yet for this Studio bind contract.',
    '// Use the Fetch tab below or Continue to Invoke for a live request.',
    `// Service: ${escapeForTemplateLiteral(contract.serviceId)}`,
    `// Endpoint: ${escapeForTemplateLiteral(contract.endpointId)}`,
    `// Sample input: ${escapeForTemplateLiteral(sampleInput.trim() || createDefaultBindSampleInput(contract))}`,
  ].join('\n');
}
