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
- Produce a proof artifact backed by call records and transcripts.
- Treat calendar creation as an optional post-action, not a core success condition.

## Core Workflow

### 1. Intake

Trigger: user sends a plain-text request such as:

```text
Plan a dinner date with Priya this week.
```

Actions:

- Create workflow task `task-uc5`.
- Acknowledge that the assistant will read existing context, check availability, and return several options.
- State explicitly that no restaurant will be phoned before the options are shown.

Core state created:

```yaml
task_id: task-uc5
revision: 1
participant: Priya
user_choice: null
shortlist: []
holds: []
proofs: []
```

### 2. Context And Research

Purpose: build a shortlist without external side effects.

Steps:

| Step | Source | External effect | Required for core workflow |
|---|---|---:|---:|
| Read past dinner confirmations | `api-google` Gmail | No | No |
| Read Friday calendar availability | `api-google` Calendar | No | No |
| Research relevant date/city context | web search or internal search skill | No | Recommended |
| Fetch venue pages/photos/menu details | `api-firecrawl` | No | Recommended |
| Build three-venue shortlist | LLM/internal planner | No | Yes |
| Show options card | UI/workflow output | No | Yes |

Notes:

- Google context is useful, but the workflow can continue if Google is unavailable by asking the user for missing date/time/party-size assumptions.
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
- Verify the call record and transcript.
- Continue to final artifact.

#### 4B. User Is Silent For Ten Minutes

If the user is silent for ten minutes:

- Hold all three shortlisted restaurants by phone.
- Do not choose for the user.
- Tell each restaurant that a decision is coming tonight.
- Record availability, seating, and allergy answers for each venue.

For each venue, run the reusable `call-and-verify` step.

```yaml
call_and_verify:
  - open_elevenlabs_channel
  - create_twilio_call
  - verify_twilio_call_record
  - verify_elevenlabs_transcript
```

Required proof per hold:

- Twilio call record exists and is completed.
- ElevenLabs transcript exists and contains the restaurant answer.
- Hold status is extracted from the transcript, not assumed from call success alone.

### 5. Choice Continuation

When the user later chooses a restaurant, start a continuation task such as `task-uc5b`.

Reason: the prior task produced hold context; the later user choice is a new turn and should not be grafted into the old plan revision as if the user had answered earlier.

Actions:

- Record selected restaurant.
- Record selection rationale if supplied by the user, such as pine nut allergy.
- Release every held restaurant that was not selected.
- Verify release calls with call records and transcripts.
- Produce final artifact.

### 6. Release Calls

For every unchosen held venue:

```yaml
release_venue:
  condition:
    - venue.hold_status == confirmed
    - venue.name != selected_venue
  steps:
    - open_elevenlabs_channel
    - create_twilio_release_call
    - verify_twilio_call_record
    - verify_elevenlabs_transcript
```

Rules:

- Do not release the selected venue.
- Do not call a venue that was never held unless the workflow has explicit reason to reconcile ambiguous state.
- Release calls are external effects and must be proven by transcript or call record evidence.

### 7. Final Artifact

The core workflow completes when the selected restaurant is held/confirmed, unchosen holds are released, and proof exists.

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
    - allergy_or_preference_reason
    - availability_reason
  assumptions:
    - date_source
    - time_source
    - party_size_source
  proof:
    call_records: required
    transcripts: required
    calendar_readback: optional
  approvals:
    money_spent: false
    user_visible_approval_count: 0
    reason: reservation calls are auto-allowed and no money is spent
```

## Optional Post-Action: Calendar

Calendar creation is not required for workflow success.

```yaml
optional_post_actions:
  - id: create-calendar-event
    service: api-google
    required_for_workflow_success: false
    condition:
      - selected_venue_confirmed == true
      - api-google.connected == true
      - api-google.scopes includes calendar.write
      - user_calendar_write_enabled == true
    steps:
      - create_calendar_event
      - verify_calendar_event_by_readback
    failure_handling:
      calendar_scope_missing:
        action: skip_optional_post_action
        workflow_success_unchanged: true
      calendar_write_failed:
        action: report_optional_post_action_failed
        workflow_success_unchanged: true
```

Calendar can improve user convenience, but the dinner date workflow is already successful if the reservation hold/release state and proof artifact are complete.

## NyxID And External Dependency Points

### Required For Core Workflow

| Dependency | NyxID service | Why it is needed | Current availability observed |
|---|---|---|---|
| Outbound restaurant calls | `api-twilio` | Place hold and release calls | Catalog exists; not observed as connected in current service list |
| Call transcript/proof | `api-elevenlabs` | Open call channel and read conversation transcript | Catalog exists; not observed as connected in current service list |
| Restaurant phone endpoint | Not a NyxID service | Actual human-side hold/release happens by telephone | External real-world dependency |

### Recommended For Better Shortlist

| Dependency | NyxID service | Why it is useful | Current availability observed |
|---|---|---|---|
| Venue page scrape/photos/menu hints | `api-firecrawl` | Fetch venue-owned pages, images, allergy/menu evidence | Connected via ChronoAI org, allowed |
| Search | custom/search service such as `tavily-search-chrono-ai` or internal search skill | Find candidate venues and date-specific city context | `tavily-search-chrono-ai` connected via ChronoAI org, allowed |

### Optional Context Or Post-Action

| Dependency | NyxID service | Why it is useful | Required? |
|---|---|---|---:|
| Gmail history | `api-google` | Infer usual dinner time, party size, preference history | No |
| Calendar read | `api-google` | Check whether Friday evening is free | No |
| Calendar write | `api-google` | Add final dinner plan to user's calendar | No |

Important Google scope note:

The NyxID catalog entry for `api-google` exists, but its default scopes were observed as only:

```text
openid
email
profile
```

Those scopes are not enough for Gmail or Calendar. A real workflow needs explicit Gmail read and Calendar read/write scopes if those optional features are enabled.

## Policy And Approval Rules

Core policy from the transcript:

- No money may be spent.
- Restaurant holds are treated as no-cost holds.
- Reservation calls are auto-allowed under the user's preconfigured spending rules.
- Nothing may be held before the user has seen the options.

Required runtime behavior:

- If a restaurant asks for a deposit, credit card, prepaid booking, cancellation fee, or any money commitment, stop that venue path and ask the user.
- The workflow must not silently reinterpret a paid reservation as a no-cost hold.
- Any effect-capable call must have a postcondition readback.

## Open Questions

1. Should the workflow require `api-firecrawl`, or allow a fallback where the user provides candidate restaurants manually?
2. What exact service should implement web search: an internal `aevatar-web-search-skill`, `tavily-search-chrono-ai`, Firecrawl search, or another NyxID service?
3. What exact Google OAuth scopes should be requested if optional Gmail/Calendar features are enabled?
4. Should the ten-minute silence timeout be configurable, or fixed by the published workflow spec?
5. Does auto-holding all three restaurants need an organization-level approval policy record, or is the user's stated spending rule enough?
6. How should the workflow reconcile ambiguous telephone outcomes, such as a completed call with missing transcript or a transcript that does not clearly confirm the hold?
7. Should release calls retry automatically if Twilio succeeds but the restaurant does not answer?
8. Should final artifact generation require all releases to be proven, or can it complete with a warning if one release remains unverified?
9. Where should venue phone numbers come from when Firecrawl/search results disagree?
10. Should `task-uc5b` be represented as a child workflow, a continuation run, or a new workflow run linked by correlation id?
11. Is there a need to notify the user before auto-holding all three, or is the prior visible ten-minute warning sufficient?
12. Should calendar creation be offered as a user-visible optional action after the artifact, or run automatically when Google Calendar write is connected?

## Minimal Success Contract

A minimal implementation satisfies the transcript if all of these are true:

- The user sees the shortlist before any restaurant is called.
- The workflow waits for explicit text choice or a durable ten-minute silence timeout.
- The workflow never spends money or gives payment details.
- Every hold/release call has Twilio and transcript evidence, or is reported as unverified.
- The selected venue is kept.
- Unselected held venues are released or explicitly reported as release-unverified.
- The final artifact clearly separates confirmed facts from assumptions.
- Calendar creation is optional and cannot change core workflow success.
