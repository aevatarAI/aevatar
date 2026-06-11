---
name: setup-browser-auth
description: Inject NyxID auth session into the test browser's localStorage via refresh token. Solves automated browser testing login for apps using OAuth2/PKCE with localStorage-based session storage.
argument-hint: [target-url]
---

# Setup Browser Auth

Inject a valid NyxID auth session into the gstack test browser, bypassing the Google OAuth login UI entirely.

**Use when:** "setup browser auth", "login to test browser", "inject auth", "browser testing login", or before any `/browse`, `/qa`, `/design-review` that requires an authenticated session.

**Runtime:** Claude Code + gstack browse tool.

## How It Works

1. Obtain a `refreshToken` (from env, user input, or localStorage export)
2. Call NyxID `/oauth/token` to refresh the `accessToken`
3. Fetch `/oauth/userinfo` for the latest user profile
4. Inject the full session JSON into `localStorage` key `aevatar-console:nyxid:session`
5. Navigate to the target URL — app reads localStorage and enters authenticated state

## Phase 1: Obtain Refresh Token

Check sources in order:

```bash
# 1. Environment variable
REFRESH_TOKEN="${NYXID_REFRESH_TOKEN:-}"

# 2. .env.local in the frontend app
if [ -z "$REFRESH_TOKEN" ] && [ -f apps/aevatar-console-web/.env.local ]; then
  REFRESH_TOKEN=$(grep -oP '(?<=NYXID_REFRESH_TOKEN=).*' apps/aevatar-console-web/.env.local || true)
fi
```

If no refresh token is found, **ask the user** to provide one. Tell them:

> Open your logged-in browser console and run:
> ```js
> JSON.parse(localStorage.getItem('aevatar-console:nyxid:session')).tokens.refreshToken
> ```
> Copy the output and paste it here.

If the user provides a full session JSON instead of just the token, extract `tokens.refreshToken` from it.

## Phase 2: Refresh Token via NyxID

```bash
NYXID_BASE_URL="${NYXID_BASE_URL:-https://nyx.chrono-ai.fun}"

TOKEN_RESPONSE=$(curl -s -X POST "${NYXID_BASE_URL}/oauth/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=refresh_token&refresh_token=${REFRESH_TOKEN}")
```

Parse the response:

```bash
ACCESS_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.access_token')
NEW_REFRESH_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.refresh_token // empty')
EXPIRES_IN=$(echo "$TOKEN_RESPONSE" | jq -r '.expires_in')
ID_TOKEN=$(echo "$TOKEN_RESPONSE" | jq -r '.id_token // empty')
SCOPE=$(echo "$TOKEN_RESPONSE" | jq -r '.scope // empty')
```

**If the request fails** (401/400), the refresh token is expired. Ask the user to log in manually once and export a fresh token.

Compute expiry:

```bash
EXPIRES_AT=$(( $(date +%s) * 1000 + EXPIRES_IN * 1000 ))
```

## Phase 3: Fetch User Info

```bash
USER_INFO=$(curl -s "${NYXID_BASE_URL}/oauth/userinfo" \
  -H "Authorization: Bearer ${ACCESS_TOKEN}")
```

## Phase 4: Inject into Browser

Use gstack `/browse` to open the target URL first:

```
/browse <target-url>
```

Default target: `http://127.0.0.1:5173` (or `$ARGUMENTS` if provided).

Then execute JavaScript in the browser to set localStorage:

```javascript
const session = {
  tokens: {
    accessToken: "<ACCESS_TOKEN>",
    tokenType: "Bearer",
    expiresIn: <EXPIRES_IN>,
    expiresAt: <EXPIRES_AT>,
    refreshToken: "<NEW_REFRESH_TOKEN or original REFRESH_TOKEN>",
    idToken: "<ID_TOKEN>",
    scope: "<SCOPE>"
  },
  user: <USER_INFO_JSON>
};
localStorage.setItem('aevatar-console:nyxid:session', JSON.stringify(session));
```

Then navigate to the target URL to trigger the app's auth bootstrap:

```
/browse <target-url>
```

## Phase 5: Verify

After navigation, check that the app entered authenticated state:

1. The URL should NOT redirect to `/login` or NyxID's authorize page
2. The page should show the authenticated UI (dashboard, settings, etc.)
3. Optionally, verify localStorage is set:

```javascript
const s = JSON.parse(localStorage.getItem('aevatar-console:nyxid:session'));
console.log('Auth:', s?.user?.email, 'expires:', new Date(s?.tokens?.expiresAt).toISOString());
```

## Quick Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `NYXID_BASE_URL` | `https://nyx.chrono-ai.fun` | NyxID OAuth server |
| `NYXID_REFRESH_TOKEN` | (none) | Refresh token for auto-injection |
| target URL arg | `http://127.0.0.1:5173` | App URL to open after auth |

## Troubleshooting

- **"Token refresh failed: invalid_grant"** — Refresh token expired. User must log in once manually.
- **Redirected to login after injection** — Token may be malformed. Check `expiresAt` is a valid future timestamp in milliseconds.
- **CORS errors** — Ensure the browse tool URL matches the app's origin (same host + port).
- **User info fetch fails** — The access token may have a different scope. Session will still work with the last known user info.
