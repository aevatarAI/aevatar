import { describe, expect, it } from 'vitest';
import { buildExplorerContentModel } from './contentFormatting';

describe('buildExplorerContentModel', () => {
  it('renders chat history jsonl as structured messages', () => {
    const raw = [
      JSON.stringify({
        id: 'msg-1',
        role: 'user',
        content: 'hello',
        timestamp: 1710000000000,
        status: 'complete',
      }),
      JSON.stringify({
        id: 'msg-2',
        role: 'assistant',
        content: 'line 1\nline 2',
        timestamp: 1710000001000,
        status: 'complete',
        thinking: 'step by step',
      }),
    ].join('\n');

    const model = buildExplorerContentModel('chat-history', raw);

    expect(model.kind).toBe('chat-history');
    if (model.kind !== 'chat-history') {
      throw new Error('Expected chat-history model.');
    }

    expect(model.messages).toHaveLength(2);
    expect(model.messages[1].content).toBe('line 1\nline 2');
    expect(model.messages[1].thinking).toBe('step by step');
  });

  it('renders script package json as decoded package files', () => {
    const raw = JSON.stringify({
      format: 'aevatar.scripting.package.v1',
      cSharpSources: [
        {
          path: 'Behavior.cs',
          content: 'using System;\npublic sealed class Behavior {}',
        },
      ],
      protoFiles: [
        {
          path: 'schema.proto',
          content: 'syntax = "proto3";',
        },
      ],
      entrySourcePath: 'Behavior.cs',
      entryBehaviorTypeName: 'Behavior',
    });

    const model = buildExplorerContentModel('script', raw);

    expect(model.kind).toBe('script-package');
    if (model.kind !== 'script-package') {
      throw new Error('Expected script-package model.');
    }

    expect(model.package.csharpSources[0].content).toContain('\npublic sealed class Behavior {}');
    expect(model.package.entrySourcePath).toBe('Behavior.cs');
    expect(model.package.protoFiles[0].path).toBe('schema.proto');
  });

  it('pretty prints regular json files', () => {
    const model = buildExplorerContentModel('config', '{"enabled":true,"nested":{"count":2}}');

    expect(model.kind).toBe('json');
    if (model.kind !== 'json') {
      throw new Error('Expected json model.');
    }

    expect(model.formattedText).toBe('{\n  "enabled": true,\n  "nested": {\n    "count": 2\n  }\n}');
  });

  it('keeps plain script source as text', () => {
    const model = buildExplorerContentModel('script', 'using System;\npublic sealed class Behavior {}');

    expect(model.kind).toBe('text');
    if (model.kind !== 'text') {
      throw new Error('Expected text model.');
    }

    expect(model.formattedText).toContain('public sealed class Behavior');
  });
});
