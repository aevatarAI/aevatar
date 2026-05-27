# ChatGPT Web Product Channel

Use this reference when the user wants ChatGPT product reasoning through the logged-in ChatGPT website instead of an API key.

## Connection Model

- Prefer `https://chatgpt.com/` in the Codex in-app browser when the user does not want to configure `OPENAI_API_KEY`.
- Treat the channel as connected only after a live smoke test succeeds: Codex can see the ChatGPT input box, send a short message, and read the assistant reply.
- Do not enter passwords, passkeys, recovery codes, 2FA codes, or account-authorization confirmations for the user.
- If login is required, open the page, let the user complete the sensitive login/authorization steps, then resume after the user confirms completion.
- Do not claim ChatGPT was used unless the prompt was actually sent and the reply was actually read.

## Product Brief Workflow

1. Build the product brief from `references/chatgpt-product-brief.md`.
2. Open or reuse a logged-in `chatgpt.com` tab in the in-app browser.
3. Prefer a temporary chat if it is visible and safe to enable; otherwise use a normal new chat.
4. Send one complete prompt containing the role, repo-grounded context, observed problem, constraints, and requested output sections.
5. Wait for completion, then read the response from the page.
6. Use the response as advisory product input; Codex still owns final implementation decisions and verification.

## Prompt Wrapper

Use this wrapper around the filled brief:

```text
你是一个资深产品设计师和产品思考者。请基于下面真实仓库上下文，审视这个 scoped 产品逻辑问题。不要发明仓库不存在的后端能力，不要给泛泛建议。

请返回这些小节：
1. Recommended direction
2. Why it is better
3. Proposed flow
4. Key UI/content changes
5. Edge cases and states
6. Implementation notes for engineering
7. What to defer until later

[PASTE FILLED PRODUCT BRIEF HERE]
```

## Failure Handling

If the browser cannot interact with ChatGPT:

- report the blocker precisely, such as not logged in, missing input box, challenge page, page automation blocked, or unable to read the reply
- keep the filled brief as a handoff artifact
- continue only in degraded local fallback mode if the user still wants progress
