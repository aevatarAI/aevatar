const AEVATAR_API = process.env.AEVATAR_API_URL ?? "http://aevatar-mainnet.aismart-app-testnet:8080";
const SCOPE_ID = process.env.SCOPE_ID ?? "sisyphus";
const WORKFLOW_ID = process.env.WORKFLOW_ID ?? "shared-research-iteration";
const MAX_ITERATIONS = parseInt(process.env.MAX_ITERATIONS ?? "50000", 10);
const DELAY_BETWEEN_ITERATIONS_MS = parseInt(process.env.DELAY_BETWEEN_ITERATIONS_MS ?? "5000", 10);
export class ResearchLoop {
    logger;
    abortController = null;
    iteration = 0;
    lastError = null;
    lastIterationAt = null;
    constructor(logger) {
        this.logger = logger;
    }
    get running() {
        return this.abortController !== null;
    }
    status() {
        return {
            running: this.running,
            iteration: this.iteration,
            maxIterations: MAX_ITERATIONS,
            lastError: this.lastError,
            lastIterationAt: this.lastIterationAt,
            config: {
                aevatarApi: AEVATAR_API,
                scopeId: SCOPE_ID,
                workflowId: WORKFLOW_ID,
                delayMs: DELAY_BETWEEN_ITERATIONS_MS,
            },
        };
    }
    start() {
        if (this.abortController)
            return;
        this.abortController = new AbortController();
        this.iteration = 0;
        this.lastError = null;
        this.run(this.abortController.signal);
    }
    stop() {
        this.abortController?.abort();
        this.abortController = null;
    }
    async run(signal) {
        this.logger.info({ maxIterations: MAX_ITERATIONS, workflowId: WORKFLOW_ID }, "Research loop starting");
        for (let i = 0; i < MAX_ITERATIONS && !signal.aborted; i++) {
            this.iteration = i + 1;
            this.logger.info({ iteration: this.iteration }, "Starting iteration");
            try {
                const result = await this.runIteration(signal);
                this.lastIterationAt = new Date().toISOString();
                this.lastError = null;
                this.logger.info({ iteration: this.iteration, resultLength: result.length }, "Iteration completed");
            }
            catch (err) {
                if (signal.aborted)
                    break;
                this.lastError = err.message ?? String(err);
                this.logger.error({ iteration: this.iteration, error: this.lastError }, "Iteration failed");
                // Back off on error
                await sleep(10000, signal);
            }
            if (!signal.aborted && DELAY_BETWEEN_ITERATIONS_MS > 0) {
                await sleep(DELAY_BETWEEN_ITERATIONS_MS, signal);
            }
        }
        this.logger.info({ iteration: this.iteration, aborted: signal.aborted }, "Research loop ended");
        this.abortController = null;
    }
    async runIteration(signal) {
        const url = `${AEVATAR_API}/api/scopes/${SCOPE_ID}/workflows/${WORKFLOW_ID}/runs:stream`;
        const resp = await fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ Prompt: `iteration ${this.iteration}` }),
            signal,
        });
        if (!resp.ok) {
            const body = await resp.text();
            throw new Error(`Workflow run failed: ${resp.status} ${body}`);
        }
        // Consume the SSE stream until it closes
        const reader = resp.body?.getReader();
        if (!reader)
            throw new Error("No response body");
        const decoder = new TextDecoder();
        let fullOutput = "";
        while (true) {
            const { done, value } = await reader.read();
            if (done)
                break;
            fullOutput += decoder.decode(value, { stream: true });
        }
        return fullOutput;
    }
}
function sleep(ms, signal) {
    return new Promise((resolve) => {
        const timer = setTimeout(resolve, ms);
        signal?.addEventListener("abort", () => {
            clearTimeout(timer);
            resolve();
        }, { once: true });
    });
}
