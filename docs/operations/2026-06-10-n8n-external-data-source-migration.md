# n8n External Data Source Migration

Date: 2026-06-10
Scope: GitHub issue #1930.

## Decisions

- Twitter-like API-key sources use the generic HTTP connector with `auth.type="secret_ref_header"`.
- RSS and Atom feeds use HTTP fetch plus the deterministic transform op `rss_extract_items`.
- GitHub sources stay on existing public HTTP or existing NyxID GitHub surfaces where already configured. No GitHub-specific connector is added here.
- pdfco is out of implementation scope for this issue. Migration record: `replace_with_lark_docx_create`.
- No NyxID, RSS, Twitter, pdfco, or GitHub external repository changes are required or allowed for this migration.

## HTTP Secret Header Auth

Use this shape for secret-bearing API-key headers:

```json
{
  "name": "twitterapi",
  "type": "http",
  "http": {
    "baseUrl": "https://api.twitterapi.io",
    "allowedMethods": ["GET"],
    "allowedPaths": ["/twitter/*"],
    "auth": {
      "type": "secret_ref_header",
      "secretRef": "secrets://connectors/twitterapi",
      "headerName": "X-API-Key",
      "headerValuePrefix": ""
    }
  }
}
```

The raw secret is resolved through `ICredentialProvider` only at the HTTP request edge. It must not be stored in `defaultHeaders`, workflow parameters, annotations, read models, docs, logs, or generic bags.

Fail-closed cases:

- missing `secretRef`
- missing `headerName`
- unavailable credential provider
- unresolved or empty secret
- invalid header name
- duplicate configured header

## RSS / Atom Extraction

Use only `rss_extract_items`; there is no `rss_extract` alias.

Expected output is a JSON array. Each item has exactly:

- `source_id`
- `source_url`
- `id`
- `title`
- `link`
- `published_at`
- `summary`

Example:

```yaml
steps:
  - id: fetch_feed
    type: connector_call
    parameters:
      connector: rss_feed
      operation: /feed.xml
      method: GET
    next: extract_items
  - id: extract_items
    type: transform
    parameters:
      op: rss_extract_items
      source_id: vendor-feed
      source_url: https://example.com/feed.xml
```

## Migration Matrix

| n8n source pattern | Aevatar path | Notes |
|---|---|---|
| API-key HTTP header | `HttpConnector` + `secret_ref_header` | No raw secret in `defaultHeaders`. |
| RSS 2.0 feed | `HttpConnector` + `rss_extract_items` | Deterministic XML transform. |
| Atom feed | `HttpConnector` + `rss_extract_items` | Same output fields as RSS. |
| GitHub public API | Existing HTTP connector path | No source-specific connector in this issue. |
| Existing NyxID GitHub proxy | Existing NyxID surface | Read-only dependency; no NyxID changes. |
| pdfco PDF generation | `replace_with_lark_docx_create` | Documentation-only migration decision. |

## Known Gaps

- Real API endpoints, credential refs, and feed URLs must come from deployment configuration.
- This document does not prove live external data freshness by itself; it records the supported migration path and the fail-closed secret boundary.
