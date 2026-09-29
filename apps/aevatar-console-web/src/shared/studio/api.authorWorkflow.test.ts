import { studioApi } from './api';

const originalFetch = global.fetch;
afterEach(() => {
  global.fetch = originalFetch;
});

function serveStream() {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const cancel = jest.fn();
  const body = new ReadableStream<Uint8Array>({
    start(value) {
      controller = value;
    },
    cancel,
  });
  global.fetch = jest.fn().mockResolvedValue({ ok: true, body });
  const frame = (value: unknown) =>
    controller.enqueue(
      new TextEncoder().encode(`data: ${JSON.stringify(value)}\r\n\r\n`),
    );
  return { controller, frame, cancel };
}

it('delivers progress before completion and returns the final validated workflow', async () => {
  const stream = serveStream();
  let progress!: () => void;
  const progressReceived = new Promise<void>((resolve) => {
    progress = resolve;
  });
  const onReasoning = jest.fn(() => progress());
  const onText = jest.fn();
  const generated = studioApi.authorWorkflow(
    { prompt: 'Make a workflow' },
    { onReasoning, onText },
  );
  let finished = false;
  void generated.then(() => {
    finished = true;
  });
  stream.frame({
    type: 'TEXT_MESSAGE_REASONING',
    delta: 'Validating workflow',
  });
  await progressReceived;
  expect(onReasoning).toHaveBeenCalledWith('Validating workflow');
  expect(finished).toBe(false);
  stream.frame({ type: 'TEXT_MESSAGE_CONTENT', delta: 'name: candidate' });
  stream.frame({ type: 'TEXT_MESSAGE_END', message: 'name: validated' });
  stream.controller.close();
  await expect(generated).resolves.toBe('name: validated');
  expect(onText).toHaveBeenLastCalledWith('name: validated');
});

it('rejects a truncated successful HTTP stream instead of returning a partial workflow', async () => {
  const stream = serveStream();
  const generated = studioApi.authorWorkflow({ prompt: 'Make a workflow' });
  stream.frame({ type: 'TEXT_MESSAGE_CONTENT', delta: 'name: unfinished' });
  stream.controller.close();
  await expect(generated).rejects.toThrow('without a completed workflow');
});
