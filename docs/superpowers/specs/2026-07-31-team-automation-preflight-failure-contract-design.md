# Team Automation Preflight Failure Contract

## Problem

`POST /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/preflight`
currently returns HTTP 200 whenever the authorization planner returns a
`StudioMemberWorkflowAuthorizationResult`, including results whose `Success` is
false. The console then treats most of those results as an untyped JavaScript
error and shows the generic “Authorization could not continue” message. The
backend request log also records only the response CLR type, so the stable
planner failure code is lost during production diagnosis.

## Design

Keep the application result contract unchanged. At the HTTP endpoint boundary:

- Return HTTP 200 only for `Success=true`.
- Convert every `Success=false` result into the existing Team automation error
  envelope: `code`, sanitized `message`, and `retryable`.
- Map request/target/enum failures to 400, ownership and permission failures to
  403, authorization-plan conflicts to 409, and catalog/durable availability
  failures to 503.
- Include the planner enum as a stable upper-case Team automation error code;
  never expose the planner `Detail` because it can contain resource identity.
- Log one warning containing only scope/team/member route identities and the
  enum failure code. Do not log bearer tokens, external bindings, catalog
  contents, permission digests, or planner detail.

The frontend keeps using `TeamAutomationApiError`, which already decodes this
envelope. Retry behavior continues to require an explicit retryable error code;
non-retryable failures surface the backend’s sanitized message instead of being
collapsed into an untyped decoder error.

## Alternatives Rejected

1. Add another planner special case. This leaves every other `Success=false`
   branch able to return HTTP 200.
2. Change only the toast. This improves wording but preserves the dishonest
   backend status and loses typed retry semantics.
3. Return 503 for every failure. This would misclassify access denial and
   malformed targets as transient infrastructure failures.

## Verification

- Endpoint tests cover at least one permission failure and one unavailable
  failure, including status, stable code, retryability, and secret redaction.
- Existing success and catalog-refresh exception tests remain green.
- Frontend API tests prove typed non-2xx decoding; the page test proves a
  specific sanitized message is shown instead of the generic fallback.
- Run the affected .NET and Jest tests, TypeScript checking, test stability
  guard, solution build/test, and architecture guards before push.
