---
name: aevatar-prod-verify
description: Use when verifying, reproducing, smoke-testing, or invoking Aevatar mainnet/prod APIs, workflows, chat, Responses, skills, schedules, or approval flows with a real user identity; also use before attempting Aevatar production validation through a browser, curl, copied bearer, or direct backend URL.
---

# Aevatar Production Verification

Use the signed-in local `nyxid` CLI as the only user-authenticated production ingress. Browser login state is for manual UI acceptance only, never API verification.

## Iron Rule

```text
Aevatar production API call = nyxid proxy request aevatar ...
```

Do not use browser automation, `curl` against the Aevatar host, copied bearer tokens, cookies, local storage, Kubernetes `exec`, or service credentials as substitutes. Do not ask the user to sign into the browser when `nyxid whoami` already succeeds.

## Workflow

1. Confirm identity without exposing credentials:

   ```bash
   nyxid whoami
   ```

2. Inspect the exact CLI contract before using an unfamiliar option:

   ```bash
   nyxid proxy request --help
   ```

3. Call the Aevatar service by its canonical NyxID slug. Pass JSON through stdin or a temporary file; never place secrets in arguments.
4. For SSE endpoints, add `Accept:text/event-stream` and `--stream`. Capture the run ID and typed receipts from the stream.
5. Use `aevatar-prod-logs` for read-only Kubernetes correlation after the call. User-facing LLM prose is not success evidence.
6. Report the exact command shape, run/correlation ID, terminal state, and redacted evidence. Never print tokens or bearer headers.

## Canonical Workflow Example

```bash
printf '%s' '{"prompt":"Create a reviewable workflow and stop for approval","workflow":"auto_review"}' |
  nyxid proxy request aevatar /api/chat \
    --method POST \
    --header 'Content-Type:application/json' \
    --header 'Accept:text/event-stream' \
    --data - \
    --stream
```

When the run reaches `human_approval`, stop. The user may then open the Backend Console for manual UI acceptance. Do not click approve/reject unless explicitly asked.

## Decision Table

| Need | Surface |
|---|---|
| Authenticated Aevatar API call or workflow run | `nyxid proxy request aevatar ...` |
| Confirm local identity | `nyxid whoami` |
| Inspect backend evidence | `aevatar-prod-logs` |
| Manually inspect or click UI | Browser, only when explicitly requested |
| NyxID connection or TLS failure | Diagnose `nyxid`; do not switch to browser/curl |

## Red Flags

- Opening Aevatar or NyxID login to obtain an API session
- Saying browser login is required before a production canary
- Calling `aevatar-console-backend-api.aevatar.ai` directly
- Reading or forwarding browser cookies, tokens, or storage
- Falling back from a CLI transport failure to another authentication path

Any red flag means stop and return to `nyxid whoami` / `nyxid doctor`.

## Common Rationalizations

| Excuse | Reality |
|---|---|
| "The console is already open." | Ambient UI state does not make it the authenticated API surface. |
| "Browser login is easier." | It validates a different transport and hides the CLI contract under test. |
| "curl is only for one quick check." | Direct host calls bypass the required NyxID proxy identity and audit path. |
| "The CLI network failed, so try OAuth." | Diagnose the CLI transport; changing ingress invalidates the verification. |

## Failure Handling

- `nyxid whoami` fails: report that the CLI profile needs login; use `nyxid login` only with the user's approval if it starts an interactive flow.
- `nyxid proxy request` fails before an HTTP response: run `nyxid doctor`, retry only according to the global circuit breaker, then report the transport blocker.
- Aevatar returns a typed failure: preserve the code and run ID, then correlate with `aevatar-prod-logs`. Do not retry a mutation blindly.
- A long-running approval workflow may outlive a presentation token: keep the verification window short and report token expiry honestly.
