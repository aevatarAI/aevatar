# Console home scope resolution

The default home, login fallback, overview redirect and 404 return action use
`/scopes`. This is a technical entry that reads the existing authenticated
`GET /api/auth/me` contract and opens
`/scopes/{scopeId}/workflow-activity-vnext/workflows` using the returned scope.

Never embed a personal scope in the default route or infer it from user
`subject`, browser storage, or a previously cached account. A fresh successful
session response with `authenticated: true` and a nonempty `scopeId` is required
before navigation. Loading does not open a workspace; failed or incomplete
authentication offers manual retry without a fallback scope. No background
polling, focus refresh, or reconnect refresh is introduced.

Existing explicit deep links and server authorization remain authoritative for
resource access. This change fixes the default destination; a URL or scope ID
is not an authorization grant. Tests cover different accounts, cached previous
identity, missing/expired authentication and manual recovery. Full frontend
validation is delegated to GitHub CI.
