---
title: Dinner Date Workflow Spec Draft
status: history
owner: platform
---

# Dinner Date Workflow Spec Draft

This document is a non-authoritative analysis draft derived from the UC5 "Dinner date" target-state transcript at `https://nyx-chat-wf.surge.sh/?lang=en#uc5`. It is not a canon architecture document. Use it as a working reference for turning the transcript into a workflow contract.

## Goal

Plan a dinner date with Priya this week while preserving these guarantees:

- Show candidate restaurants before any phone hold or reservation action.
- If the user is silent for ten minutes, hold all candidate restaurants by phone because holds cost no money under the stated user policy.
- After the user chooses one restaurant, release every unchosen hold.
- Produce a final artifact with the selected venue, released venues, and key assumptions.

## Flow Summary

The simplified workflow is:

1. Capture the user's plain-text choice if one is already present.
2. Initialize dinner context from the request, not from email or calendar history.
3. Build a three-venue shortlist from restaurant research.
4. Show the shortlist before any restaurant phone action.
5. If the user chooses in time, hold only the selected venue.
6. If the user is silent for ten minutes, hold all three venues, then wait for the later choice.
7. Release every held venue that was not selected.
8. Produce a final artifact with the kept venue, released venues, and assumptions.

External dependency count:

| Category | Count | Dependencies |
|---|---:|---|
| Required core dependency | 1 | `restaurant-phone` / equivalent phone service |
| Recommended shortlist dependencies | 2 | `api-firecrawl`, `tavily-search` |
| Removed dependencies | 1 | `api-google` for Gmail, Calendar read, and Calendar write |

## Core Workflow

### 1. Intake

Trigger: user sends a plain-text request such as:

```text
Plan a dinner date with Priya this week.
```

Actions:

- Create workflow task `task-uc5`.
- Acknowledge that the assistant will research restaurants and return several options.
- State explicitly that no restaurant will be phoned before the options are shown.

Core state created:

```yaml
task_id: task-uc5
revision: 1
participant: Priya
user_choice: null
shortlist: []
holds: []
```

### 2. Research And Shortlist

Purpose: build a shortlist without external side effects.

Steps:

| Step | Source | External effect | Required for core workflow |
|---|---|---:|---:|
| Research relevant date/city context | `tavily-search` NyxID service | No | Recommended |
| Fetch venue pages/photos/menu details | `api-firecrawl` | No | Recommended |
| Build three-venue shortlist | LLM/internal planner | No | Yes |
| Show options card | UI/workflow output | No | Yes |

Notes:

- The workflow can ask the user for missing date/time/party-size assumptions.
- Firecrawl/search improves candidate quality, but the workflow can also use user-provided candidate restaurants.
- The options card is informational only. It must not be the only control surface; the user can choose by ordinary chat text.

### 3. User Gate And Silence Timer

After options are shown, the workflow waits for one of two events:

```yaml
wait_for:
  - user_choice_text
  - silence_timeout: 10m
```

Rules:

- The ten-minute timer must be durable workflow/actor state.
- It must not be implemented as process-local sleep, in-memory state, or a transient callback that can fire twice after restart.
- If the user chooses before timeout, cancel the timer and hold only the selected restaurant.
- If the timer fires first, cancel the pending input row and enter the hold-all branch.

### 4. Hold Branch

There are two legal hold paths.

#### 4A. User Chooses Before Timeout

If the user names one restaurant before the timer fires:

- Call only that restaurant.
- Ask for the requested time, indoor table, and allergy accommodations.
- Save the resulting hold status in workflow state.
- Continue to final artifact.

#### 4B. User Is Silent For Ten Minutes

If the user is silent for ten minutes:

- Hold all three shortlisted restaurants by phone.
- Do not choose for the user.
- Tell each restaurant that a decision is coming tonight.
- Save availability, seating, and allergy answers for each venue.

### 5. Choice Continuation

When the user later chooses a restaurant, start a continuation task such as `task-uc5b`.

Reason: the prior task produced hold context; the later user choice is a new turn and should not be grafted into the old plan revision as if the user had answered earlier.

Actions:

- Save selected restaurant.
- Save selection reason if supplied by the user, such as pine nut allergy.
- Release every held restaurant that was not selected.
- Produce final artifact.

### 6. Release Calls

For every unchosen held venue:

```yaml
release_venue:
  condition:
    - venue.hold_status == confirmed
    - venue.name != selected_venue
  steps:
    - call_restaurant_to_release_hold
    - save_release_status
```

Rules:

- Do not release the selected venue.
- Do not call a venue that was never held unless the workflow has explicit reason to reconcile ambiguous state.

### 7. Final Artifact

The core workflow completes when the selected restaurant is held/confirmed and unchosen holds are released.

Artifact fields:

```yaml
artifact:
  title: Dinner date confirmation
  kept:
    venue: selected_venue
    time: confirmed_time
    party_size: 2
    seating: indoors
    hold_source: phone
  released:
    - venue_name
  rationale:
    - allergy_or_user_supplied_reason
    - availability_reason
  assumptions:
    - date_source
    - time_source
    - party_size_source
  approvals:
    money_spent: false
    user_visible_approval_count: 0
    reason: reservation calls are auto-allowed and no money is spent
```

## NyxID And External Dependency Points

### Required For Core Workflow

| Dependency | Service | Why it is needed | Required count |
|---|---|---|---:|
| Restaurant phone action | `restaurant-phone` / equivalent phone service | Place hold and release calls | 1 |

### Recommended For Better Shortlist

| Dependency | NyxID service | Why it is useful | Current availability observed |
|---|---|---|---|
| Venue page scrape/photos/menu hints | `api-firecrawl` | Fetch venue-owned pages, images, allergy/menu evidence | Connected via ChronoAI org, allowed |
| Search | `tavily-search` NyxID service | Find candidate venues and date-specific city context | Production `web_search` is bound to `tavily-search` |

## Policy And Approval Rules

Core policy from the transcript:

- No money may be spent.
- Restaurant holds are treated as no-cost holds.
- Reservation calls are auto-allowed under the user's preconfigured spending rules.
- Nothing may be held before the user has seen the options.

Required runtime behavior:

- If a restaurant asks for a deposit, credit card, prepaid booking, cancellation fee, or any money commitment, stop that venue path and ask the user.
- The workflow must not silently reinterpret a paid reservation as a no-cost hold.
- Any effect-capable call must produce a clear workflow status.

## Open Questions

1. Should the workflow require `api-firecrawl`, or allow a fallback where the user provides candidate restaurants manually?
2. What exact service should implement web search: an internal `aevatar-web-search-skill`, `tavily-search-chrono-ai`, Firecrawl search, or another NyxID service?
3. Should the ten-minute silence timeout be configurable, or fixed by the published workflow spec?
4. Is the user's stated spending rule enough for auto-holding all three restaurants, or should this require an explicit policy field on the workflow?
5. How should the workflow reconcile ambiguous telephone outcomes, such as a restaurant not answering or not clearly confirming a hold?
6. Should release calls retry automatically if the restaurant does not answer?
7. Can final artifact generation complete with a warning if one release remains unverified?
8. Where should venue phone numbers come from when Firecrawl/search results disagree?
9. Should `task-uc5b` be represented as a child workflow, a continuation run, or a new workflow run linked by correlation id?
10. Is there a need to notify the user before auto-holding all three, or is the prior visible ten-minute warning sufficient?

## Minimal Success Contract

A minimal implementation satisfies the transcript if all of these are true:

- The user sees the shortlist before any restaurant is called.
- The workflow waits for explicit text choice or a durable ten-minute silence timeout.
- The workflow never spends money or gives payment details.
- The selected venue is kept.
- Unselected held venues are released or explicitly reported as release-unverified.
- The final artifact clearly separates confirmed facts from assumptions.
