# Skill service recommendations

Skill-to-service discovery is backend behavior shared by all clients. Channels
consumes the result and owns only display, selection and explicit save. Clients
must not download Skill instructions or recreate dependency inference.

The backend implementation targets `feature/integrate`. The Channels consumer
is delivered separately in [console PR #3679](https://github.com/aevatarAI/aevatar/pull/3679),
targeting `feat/2026-08-04_workflow-activity-vnext`. Deploy this endpoint before
enabling the complete recommendation experience in the console. Until then,
the consumer reports discovery as unavailable and preserves manual selection.

## Endpoint

`GET /api/skills/service-recommendations?skillName=<encoded-name>` requires an
authenticated caller and their NyxID bearer credential. It is served by Mainnet
and returns `Cache-Control: no-store`. The name is the same Skill name stored by
channel registration, limited to 128 characters and excluding dot path segments.

```json
{
  "skillName": "support",
  "suggestions": [{
    "slug": "api-github",
    "label": "GitHub",
    "evidence": "linked",
    "instances": [{
      "id": "user-service-example",
      "slug": "api-github",
      "label": "Team GitHub",
      "active": true,
      "allowed": true,
      "source": "organization",
      "organizationName": "Example team"
    }]
  }]
}
```

`evidence` is `linked`, `catalog`, or `mention`. These are advisory sources,
not required/optional dependency declarations. Empty `instances` means no
matching connection in the caller's account inventory. `allowed` reports
account-level access only; it does not grant access to the current session or
channel. `source` is `personal`, `organization`, or `unknown`. Existing
channel authorization still uses explicitly selected exact UserService IDs.
Catalog IDs and Skill association IDs must never become authorization IDs.
Inventory availability is not credential-validity evidence.

Successful empty discovery returns an empty `suggestions` array. Missing
authentication is 401; malformed names return 400 with
`{"code":"invalid_skill_name"}`; inaccessible, malformed, mismatched or
unavailable upstream data returns 502 with
`{"code":"skill_service_discovery_unavailable"}`. Failures never become
successful empty or partial recommendations and never expose upstream errors,
private instructions, file contents, or credentials. Cancellation propagates.

## Ownership and source contracts

- `Aevatar.AI.Abstractions.Skills`: protobuf input/output, evidence and instance
  contracts; caller credential parameters remain outside serialized data.
- `Aevatar.AI.Core.Skills.SkillServiceRecommendationService`: stateless discovery
  policy behind `ISkillServiceDiscoverySource`. No Host/HTTP dependency.
- `Aevatar.AI.ToolProviders.Ornn.OrnnSkillServiceDiscoverySource`: external
  adaptation through existing NyxID/Ornn clients. JSON is decoded at this boundary.
- `Aevatar.Mainnet.Host.Api.Skills.SkillServiceRecommendationEndpoints`:
  authentication, HTTP result/error mapping, and response DTOs only.
- Console `channelSkillServicesApi`: response validation only; the UI cannot
  invent recommendations or evidence categories.

The adapter resolves the selected name using Ornn `GET /api/v1/skills/:name`,
verifies identity, then loads `GET /api/v1/skills/:guid/json`. It reads the
description, root `SKILL.md`, and optional `nyxidServiceSlug`. It reads NyxID
`GET /api/v1/catalog?include_all=true` and `GET /api/v1/user-services` using
the same invocation's caller credential. Existing source contracts were
compared with `feature/integrate`; no external-product changes are required.
The adapter uses the configured Ornn per-call timeout as the discovery budget,
and rejects root instructions above 200,000 characters.

Evidence priority is explicit Skill association, then exact catalog
`recommended_skills` association, then whole service-name/slug mention in the
description or root instructions. Names shorter than three characters are
excluded from text inference. Catalog and inventory candidates are combined;
all exact same-slug instances remain distinct. Skill instructions are data,
never executed. No LLM inference, recursive Skill loading, grant mutation,
credential creation, or channel update occurs during discovery.

These APIs provide no exhaustive mandatory-dependency contract. Literal matching
can miss aliases or include incidental mentions. Every result is advisory and
may be incomplete. Adding authoritative required dependencies later must extend
the typed contract and source evidence, rather than infer a requirement from
text. Platform-required services retain their current separate contract.

All collections are invocation-local. There is no shared credential/result
cache, actor state, persisted derived dependency list, event replay, projection
priming or query-time materialization. This is transient discovery over external
caller-visible catalog facts, not a new authority for channel configuration.

## Verification

Provider integration tests exercise real policy and adapters against an HTTP
boundary: evidence precedence, bounded name matching, distinct instance IDs,
sanitized failures, identity mismatches, cancellation and caller isolation.
Host tests cover response mapping, authentication, validation, cancellation
forwarding and retryable errors. Console tests cover API decoding, stale-response
isolation, manual selection, exact save IDs and recovery.
