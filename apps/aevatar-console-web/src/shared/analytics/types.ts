export type ConsoleEventProperties = {
  workflow_started: { workflowId: string; nodeCount: number };
  workflow_finished: {
    workflowId: string;
    status: 'success' | 'failed';
    /** Elapsed execution time in milliseconds. */
    duration: number;
  };
  workflow_timeout: { workflowId: string; timeoutMs: number };
  sse_disconnect: {
    workflowId: string;
    reason: 'unexpected_eof' | 'stream_error';
  };
  sse_reconnect: { workflowId: string; retryCount: number };
};
