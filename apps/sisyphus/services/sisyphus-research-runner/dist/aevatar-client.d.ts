export interface StartResult {
    executionId: string;
    status: string;
}
/**
 * Fetch compiled YAML from sisyphus-workflow service.
 */
export declare function fetchCompiledWorkflow(workflowId: string): Promise<{
    workflowYaml: string;
    connectorJson: Record<string, unknown>[];
}>;
/**
 * Submit compiled workflow to Aevatar Engine for execution.
 * Returns an SSE stream of AG-UI events.
 */
export declare function startExecution(workflowYaml: string, prompt: string, signal?: AbortSignal): Promise<{
    executionId: string;
    stream: ReadableStream<Uint8Array> | null;
}>;
/**
 * Stream AG-UI events from Aevatar Engine via SSE.
 */
export declare function streamEvents(executionId: string, authorization?: string, signal?: AbortSignal): Promise<ReadableStream<Uint8Array> | null>;
/**
 * Fetch deployment status from sisyphus-workflow service.
 */
export declare function fetchWorkflowDeploymentStatus(workflowId: string): Promise<{
    deploymentState: {
        status: string;
        deployError?: string;
    };
}>;
/**
 * Start execution for a deployed workflow via scope chat:stream endpoint.
 * Returns the SSE stream directly — caller should consume events from it.
 */
export declare function startDeployedExecution(workflowName: string, prompt: string, authorization?: string, signal?: AbortSignal): Promise<{
    executionId: string;
    stream: ReadableStream<Uint8Array> | null;
}>;
/**
 * Terminate an execution on Aevatar Engine.
 */
export declare function terminateExecution(executionId: string): Promise<void>;
//# sourceMappingURL=aevatar-client.d.ts.map