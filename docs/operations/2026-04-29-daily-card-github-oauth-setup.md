# /daily Card — GitHub OAuth Setup & Troubleshooting Runbook

This runbook covers how to make `/daily` work end-to-end for a given Lark user
by connecting their NyxID account to GitHub via OAuth, plus the playbook for
the most common failure mode (`pending_auth` that never flips to `active`).

It is the operations-side companion to
[`docs/canon/daily-command-pipeline.md`](../canon/daily-command-pipeline.md),
which describes the runtime pipeline. This file describes the steps a human
operator (admin or end user) takes before the pipeline can ever run, and the
diagnostic sequence to follow when those steps appear to have succeeded but
the proxied call still fails with credential errors.

## Goal

After this runbook, a Lark user can type `/daily` (or `/daily <gh-username>`)
in the bot's private chat and the agent will successfully:

1. Resolve a NyxID-issued proxy API key for `api-github`.
2. Preflight `GET /api/v1/proxy/s/api-github/rate_limit` — receive 200.
3. Run the LLM with `nyxid_proxy` tool calls hitting GitHub through NyxID.

Up to step 3, every failure mode here is **outside aevatar's process** — it
lives in NyxID provider config, the user's GitHub OAuth App, or the
`UserApiKey` / `UserProviderToken` rows that NyxID maintains.

## Audience

- **Admin / operator**: doing the one-time NyxID provider config + GitHub
  OAuth App registration.
- **End user**: connecting their own GitHub once the provider exists.
- **On-call**: diagnosing "the user typed `/daily` and got
  `Connect GitHub in NyxID, then run /daily again.`" or similar.

## Preconditions

Before any user can connect GitHub, the following must already be true:

- NyxID has a `provider_config` for GitHub (slug `api-github`,
  `provider_type: oauth2`, `credential_mode: user`). Verify with:
  ```
  nyxid catalog show api-github
  ```
  Expected: `provider_config_id` present, `authorization_url` and `token_url`
  point to `https://github.com/login/oauth/...`.
- A GitHub OAuth App exists (created at
  `https://github.com/settings/developers` or under an organization). The
  **Authorization callback URL** on that app must equal NyxID's callback URL
  exactly:
  ```
  https://<your-nyx-api-host>/api/v1/providers/callback
  ```
  Note the host is the **API/backend host**, not the dashboard host. In our
  production environment this is `nyx-api.chrono-ai.fun`, while the dashboard
  is served from `nyx.chrono-ai.fun` — both reach the same backend, but the
  redirect URI registered on GitHub must use the API host.
- The aevatar host is configured with the matching NyxID Authority
  (`appsettings.json` → `Aevatar:NyxId:Authority`) and the Lark relay path is
  live. See [`lark-nyx-cutover-runbook.md`](2026-04-22-lark-nyx-cutover-runbook.md)
  for that side of the cutover.

## Per-user setup

There are two equivalent paths. Both end with the user's `api-github` service
in NyxID at `status: active`.

### Path A — CLI (recommended for power users / scripting)

```bash
# 1. Register your GitHub OAuth App credentials with NyxID. Read both
#    values from environment variables so the secret never lands in shell
#    history.
export GH_CID=<client id from your GitHub OAuth App>
export GH_CS=<client secret from your GitHub OAuth App>
nyxid service credentials api-github \
  --client-id-env GH_CID --client-secret-env GH_CS

# 2. Create the service and walk through OAuth. Opens the browser to
#    GitHub's authorize page; on success the CLI flips the unified key
#    to status=active and prints the proxy URL.
nyxid service add api-github --oauth
```

### Path B — Dashboard

1. Sign in at `https://<nyx-host>/`.
2. **AI Services** → **Add** → pick **GitHub API** from the catalog.
3. Provide your GitHub OAuth App `client_id` + `client_secret` when prompted
   (Credential Mode: `user`).
4. Click **Connect with GitHub** → authorize on github.com → land back on the
   service page; status should flip from `pending_auth` to `active`.

### Verify

After either path, the service should be usable end-to-end:

```bash
# Resolve the unified-key (service) id
SERVICE_ID=$(nyxid service list --output json \
  | jq -r '.services[] | select(.slug=="api-github") | .id')

# Hit GitHub through the proxy with your NyxID bearer token
curl -sS -H "Authorization: Bearer $(cat ~/.nyxid/access_token)" \
  https://<nyx-host>/api/v1/proxy/s/api-github/user | jq .login
```

A 200 with the GitHub login username means `/daily` will work for this user.

## Troubleshooting playbook

**Symptom**: dashboard shows `GitHub API` with status `pending_auth` and the
copy `The service record is enabled, but its credential is pending_auth.
Real requests will fail until the credential is restored.` Or `/daily` in Lark
returns `Connect GitHub in NyxID, then run /daily again.`

The OAuth flow can fail at any of three places. Diagnose in this order — each
step rules out a layer:

### Step 1 — Did the OAuth callback ever produce a provider token?

```bash
curl -sS -H "Authorization: Bearer $(cat ~/.nyxid/access_token)" \
  https://<nyx-host>/api/v1/providers/my-tokens | jq .
```

- `tokens: []` (or no entry with the GitHub `provider_config_id`) →
  **token exchange never succeeded**. The user either never clicked
  Authorize, OR NyxID's POST to `https://github.com/login/oauth/access_token`
  was rejected. Continue to step 2.
- `tokens: [{...}]` with a github entry → token exchange did succeed; the
  problem is in the unified-key sync step. Skip to step 4.

### Step 2 — Is the OAuth state still valid?

OAuth states have a 10-minute TTL and are single-use (atomic
find-and-delete). If the user clicked the authorize link more than 10
minutes after it was generated, the callback will reject the state.

Re-issue a fresh authorize URL:

```bash
PROVIDER_ID=$(nyxid catalog show api-github --output json | jq -r .provider_config_id)
KEY_ID=$(nyxid external-key list --output json \
  | jq -r '.api_keys[] | select(.label=="GitHub API") | .id')
curl -sS -H "Authorization: Bearer $(cat ~/.nyxid/access_token)" \
  "https://<nyx-host>/api/v1/providers/$PROVIDER_ID/connect/oauth?redirect_path=/keys/$SERVICE_ID" \
  | jq -r .authorization_url
```

Open that URL in a browser and complete authorization within ~2 minutes.
If `my-tokens` is still empty after that, the state is not the issue — go
to step 3.

> **Note**: `redirect_path` must use the **unified-key (service) id**
> (`2e54...`), **not** the credential id from `external-key list` (`8089...`).
> The dashboard's `/keys/:id` page resolves against the unified key. Passing
> the credential id lands you on a "Key Not Found" page even though the
> OAuth flow itself succeeded.

### Step 3 — Is the stored client_secret still valid?

This is the most common cause when the service worked before but stopped.
The token exchange step (`POST https://github.com/login/oauth/access_token`)
fails with 401 if either:

- The GitHub OAuth App's `client_secret` was rotated and the new one wasn't
  pushed to NyxID, or
- NyxID's encryption key changed (e.g. redeploy with a different
  `ENCRYPTION_KEYS` env) and the previously-stored secret can no longer be
  decrypted.

Both look identical from outside: callback succeeds at the state-validation
step, but `my-tokens` stays empty and the unified key stays `pending_auth`.

**Fix**: rotate and re-upload.

1. On GitHub: OAuth App settings → **Generate a new client secret** → copy.
2. Push it to NyxID:
   ```bash
   export GH_CID=<existing client id>
   export GH_CS=<the new secret>
   nyxid service credentials api-github \
     --client-id-env GH_CID --client-secret-env GH_CS
   ```
3. Re-run the OAuth flow (Path A step 2 of "Per-user setup", or step 2's
   curl above to get a fresh authorize URL).
4. Verify with `my-tokens` then `/api/v1/keys/$SERVICE_ID`.

### Step 4 — Token exists but unified key still `pending_auth`

This is rare. The OAuth callback writes `UserProviderToken` first, then
calls `sync_provider_token_to_api_keys` to flip every matching `UserApiKey`
row to `status: active`. The sync only matches on `(user_id,
provider_config_id)`, and silently no-ops if no key matches. Things to
check:

- The `UserApiKey` row's `provider_config_id` matches what the
  `UserProviderToken` row was written under. Compare:
  ```bash
  nyxid external-key list --output json \
    | jq '.api_keys[] | select(.label=="GitHub API") | .provider_config_id'
  curl -sS -H "Authorization: Bearer $(cat ~/.nyxid/access_token)" \
    https://<nyx-host>/api/v1/providers/my-tokens \
    | jq '.tokens[] | .provider_config_id'
  ```
  They must match. If not, the catalog has multiple GitHub provider configs
  and the service was created against a different one than the OAuth flow
  used — delete and re-add the service.
- The token isn't org-scoped while the service is personal, or vice versa.
  Org-targeted OAuth must include `target_org_id` in the initiate call so
  the token lands on the org's user_id.

### Catch-all

If none of the above explain it, capture:

- Output of `nyxid service show api-github` (status, credential_source).
- Output of `nyxid external-key list` (filter for GitHub).
- Output of `/api/v1/providers/my-tokens` (filter for the GitHub
  `provider_config_id`).
- Approximate timestamp the user clicked Authorize.

…and pull NyxID backend audit logs for that user_id around that time:

```
event ∈ {
  provider_token_connected,
  provider_oauth_callback_failed
}
```

`provider_oauth_callback_failed` carries a `reason` field that pinpoints
which step (`invalid_state`, `expired_state`, `failed_to_sync_unified_keys`,
or upstream HTTP code from GitHub).

## Known UX gaps (open improvement candidates)

These all surfaced during the 2026-04-29 setup session and would each have
shortened diagnosis from ~30 minutes to seconds. Track separately as NyxID
or aevatar issues; this section is the source list.

1. **Surface token-exchange failure on the unified key.** Today
   `pending_auth` is opaque — the dashboard says the credential is pending
   but doesn't expose the underlying GitHub HTTP status. The backend already
   knows the upstream returned 401 (or the encryption_keys decrypt failed);
   it should attach `last_oauth_error: { code, source, occurred_at }` to the
   key detail so the user can tell "I never authorized" apart from
   "client_secret is wrong."
2. **`nyxid service reauth <slug>`** as a first-class CLI subcommand.
   Today the only built-in path for an existing `pending_auth` service is
   delete-and-re-add (destructive — kills the unified-key id and any
   downstream references) or hand-crafting a `/providers/{pid}/connect/oauth`
   call. A reauth subcommand that takes a slug and walks through OAuth
   against the existing key would eliminate that.
3. **`/api/v1/keys/{id}` 404 should distinguish credential id vs
   unified-key id.** Both are commonly called "key" in conversation; the
   error message should say which one the path expects so callers don't
   waste time on the wrong UUID.
4. **Self-serve audit log endpoint.** A user looking at their own
   `pending_auth` service has no way to retrieve their own
   `provider_oauth_callback_failed` events without operator help. A
   read-only `GET /api/v1/audit/me?event=provider_oauth_*` would close
   that gap.
5. **`/daily` auth prompt should deep-link to the OAuth start URL.** Today
   the response is plain text: `Connect GitHub in NyxID, then run /daily
   again.` A Lark interactive card with a button that opens the dashboard's
   GitHub service page (or directly the OAuth initiate endpoint) would
   collapse a multi-step "go find the right page" task into one click.
   This is in scope for `fix/2026-04-29_daily-card-auth-prompt`.
6. **`/daily` should distinguish "GitHub not connected" from "connected but
   pending_auth / 4xx."** `BuildGitHubAuthorizationResponseAsync` only
   checks the existence of a binding. When the binding exists but the
   credential is `pending_auth`, the proxy call returns 4xx and the
   `nyxid_proxy` tool currently returns the error JSON inline — the LLM
   may interpret it as "no activity" and emit an empty daily report
   (silent failure tracked as issue #439). The pre-flight should fail
   fast with a credential-state error instead.

## Cross-references

- Runtime pipeline & failure modes: [`../canon/daily-command-pipeline.md`](../canon/daily-command-pipeline.md)
- LLM provider setup (api_key flow, distinct from this OAuth flow):
  [`../canon/nyxid-llm-integration.md`](../canon/nyxid-llm-integration.md)
- Lark webhook ingress & cutover:
  [`2026-04-22-lark-nyx-cutover-runbook.md`](2026-04-22-lark-nyx-cutover-runbook.md)
- NyxID source of truth for the OAuth callback handler:
  `~/Code/NyxID/backend/src/handlers/user_tokens.rs::generic_oauth_callback_impl`
  and the sync function at `src/services/user_api_key_service.rs::sync_provider_token_to_api_keys`.
