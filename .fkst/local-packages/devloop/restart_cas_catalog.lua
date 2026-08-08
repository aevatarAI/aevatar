local transition_version = require("contract.transition_version")
local pending_projection = require("devloop.restart_pending_projection")
local devloop_state = require("devloop.state")

local M = {}

M.assumptions = {
  versions_equivalent = "ASSUMED-UNVERIFIED",
}

local function result(status, reason_code, cas_outcome)
  return {
    status = status,
    reason_code = reason_code,
    cas_outcome = cas_outcome,
  }
end

local function illegal(reason_code)
  return result("illegal", reason_code, "illegal(" .. tostring(reason_code) .. ")")
end

local function is_string_or_nil(value)
  return value == nil or type(value) == "string"
end

local function valid_current(current)
  return current == nil
    or (type(current) == "table"
      and is_string_or_nil(current.state)
      and is_string_or_nil(current.version))
end

local function valid_state_list(states)
  if type(states) ~= "table" or #states == 0 then
    return false
  end
  for _, state_name in ipairs(states) do
    if type(state_name) ~= "string" or state_name == "" then
      return false
    end
  end
  return true
end

local function valid_common_evidence(evidence)
  return type(evidence) == "table"
    and valid_current(evidence.current)
    and is_string_or_nil(evidence.incoming_version)
    and is_string_or_nil(evidence.target_version)
    and is_string_or_nil(evidence.overlay_version)
end

local function normalize_state(state_name)
  if state_name == nil then
    return "unmanaged"
  end
  return state_name
end

local function state_is_one_of(state_name, states)
  local normalized = normalize_state(state_name)
  for _, expected in ipairs(states or {}) do
    if normalized == normalize_state(expected) then
      return true
    end
  end
  return false
end

local function current_fields(current)
  if type(current) == "table" then
    return current.state, current.version
  end
  return current, nil
end

local function plain_status(current, source_states, target_state, projection)
  if projection == nil then
    error("restart_cas_catalog: pending projection required for reachability")
  end
  local current_state = current_fields(current)
  if current_state == target_state then
    return "idempotent"
  end
  if state_is_one_of(current_state, source_states) then
    return "apply"
  end
  for _, source_state in ipairs(source_states) do
    if pending_projection.can_reach(projection, current_state, normalize_state(source_state)) then
      return "pending"
    end
  end
  return "stale"
end

local function versioned_status(current, source_states, target_state, incoming_version, projection)
  if type(current) == "table"
    and current.version ~= nil
    and incoming_version ~= nil
    and transition_version.compare(incoming_version, current.version) < 0 then
    return "stale"
  end
  return plain_status(current, source_states, target_state, projection)
end

local function versions_equivalent(left, right)
  if left == nil or right == nil then
    return left == right
  end
  if tostring(left) == tostring(right) then
    return true
  end
  return transition_version.safe_version_segment(left)
    == transition_version.safe_version_segment(right)
end

local function cyclic_status(current, source_states, target_state, incoming_version, target_version, projection)
  local current_state, current_version = current_fields(current)
  if incoming_version == nil then
    return plain_status(current, source_states, target_state, projection)
  end
  if target_version ~= nil
    and current_state == target_state
    and versions_equivalent(current_version, target_version) then
    return "idempotent"
  end

  local order = transition_version.compare(incoming_version, current_version)
  if order > 0 then
    return "pending"
  end
  if order < 0 then
    return "stale"
  end
  if current_state == target_state then
    return "idempotent"
  end
  if state_is_one_of(current_state, source_states) then
    return "apply"
  end
  if devloop_state.stage_rank(target_state) > devloop_state.stage_rank(current_state) then
    return "apply"
  end
  return "stale"
end

local function status_result(current, status, incoming_version)
  if status == "apply" then
    return result("apply", "apply", "applied")
  end
  if status == "idempotent" then
    return result("idempotent", "already-at-target", "skip-idempotent(already at to_state)")
  end
  if status == "pending" then
    return result("pending", "source-marker-not-visible", "retry-pending(from-state marker not yet visible)")
  end
  if status == "stale" then
    if type(current) == "table"
      and current.version ~= nil
      and incoming_version ~= nil
      and transition_version.compare(incoming_version, current.version) < 0 then
      return result("stale", "incoming-version-older", "skip-stale(incoming version < current marker version)")
    end
    return result("stale", "advanced-or-diverged", "skip-advanced-or-diverged")
  end
  return illegal("invalid-base-result")
end

local function base_result(base, current, source_states, target_state, incoming_version, target_version, projection)
  if base == "plain" then
    return status_result(current, plain_status(current, source_states, target_state, projection), incoming_version)
  end
  if base == "versioned" then
    return status_result(
      current,
      versioned_status(current, source_states, target_state, incoming_version, projection),
      incoming_version
    )
  end
  if base == "cyclic" then
    return status_result(
      current,
      cyclic_status(current, source_states, target_state, incoming_version, target_version, projection),
      incoming_version
    )
  end
  return illegal("invalid-base-policy")
end

local function resolve_base(evidence, base, projection)
  if not valid_common_evidence(evidence)
    or not valid_state_list(evidence.source_states)
    or type(evidence.target_state) ~= "string"
    or evidence.target_state == "" then
    return illegal("invalid-evidence")
  end
  local incoming_version = evidence.incoming_version
  if base == "plain" then
    -- The abstract cas.base_plain_legacy_v1 (the only resolve_base plain policy) models
    -- production's versionless transition_status admission primitive, so its stale
    -- cas_outcome must NOT version-branch. Concrete plain profiles that DO carry a
    -- cas_outcome version (e.g. loop_plain's dedup_key) resolve via resolve_profile, which
    -- passes incoming_version to base_result directly and is unaffected by this nil.
    incoming_version = nil
  end
  return base_result(
    base,
    evidence.current,
    evidence.source_states,
    evidence.target_state,
    incoming_version,
    evidence.target_version,
    projection
  )
end

local function resolve_variant(definition, evidence)
  local variant = definition.variants[evidence.variant]
  if variant == nil then
    return nil, illegal("invalid-evidence")
  end
  return variant, nil
end

local function version_matches(form, left, right)
  if form == "raw" then
    return tostring(left or "") == tostring(right or "")
  end
  if form == "safe" then
    return transition_version.safe_version_segment(left)
      == transition_version.safe_version_segment(right)
  end
  return false
end

local function apply_overlay(definition, variant, evidence, resolved)
  local overlay = definition.overlay
  if overlay == nil or overlay.kind == "none" or overlay.statuses[resolved.status] ~= true then
    return resolved
  end

  local current = evidence.current or {}
  if overlay.only_states ~= nil and not state_is_one_of(current.state, overlay.only_states) then
    return resolved
  end
  -- enforce_source_states defaults to true; a policy whose production CAS has no
  -- from-state guard (the cyclic base's stage-rank forward-progress rule already
  -- owns state admission, e.g. review_result) opts out so the overlay only enforces
  -- version equality and does not override the base's admission.
  if overlay.enforce_source_states ~= false then
    local source_states = overlay.source_states or variant.source_states
    if not state_is_one_of(current.state, source_states) then
      return result("stale", "from-state-mismatch", "skip-stale(from-state-mismatch)")
    end
  end
  local compared_version = evidence.overlay_version
  if compared_version == nil then
    compared_version = evidence.incoming_version
  end
  if not version_matches(overlay.version_form, current.version, compared_version) then
    return result("stale", "version-mismatch", "skip-stale(version-mismatch)")
  end
  return resolved
end

local function resolve_profile(definition, evidence, projection)
  if not valid_common_evidence(evidence) then
    return illegal("invalid-evidence")
  end
  local variant, invalid = resolve_variant(definition, evidence)
  if invalid ~= nil then
    return invalid
  end

  local current_for_base = evidence.current
  if definition.base_current_version_form == "safe" and type(current_for_base) == "table" then
    current_for_base = {
      state = current_for_base.state,
      version = transition_version.safe_version_segment(current_for_base.version),
    }
  end
  local target_version = evidence.target_version or variant.target_version
  -- legacy_probe_target_state is a shared pre-decision cyclic-probe target, not an edge target.
  local probe_target_state = variant.legacy_probe_target_state or variant.target_state
  local resolved = base_result(
    definition.base,
    current_for_base,
    variant.source_states,
    probe_target_state,
    evidence.incoming_version,
    target_version,
    projection
  )
  return apply_overlay(definition, variant, evidence, resolved)
end

local function valid_handoff(evidence)
  return type(evidence.handoff) == "table" and evidence.handoff.status == "valid"
end

local function resolve_review_loop(evidence)
  if type(evidence) ~= "table"
    or not valid_current(evidence.current)
    or type(evidence.review_version) ~= "string" then
    return illegal("invalid-evidence")
  end
  local current = evidence.current or {}
  if state_is_one_of(current.state, { "reviewing" })
    and transition_version.safe_version_segment(current.version) == evidence.review_version then
    return result("apply", "apply", "applied")
  end
  if current.state ~= nil
    and devloop_state.stage_rank(current.state) > devloop_state.stage_rank("reviewing") then
    return result("stale", "reviewing-version", "skip-stale(reviewing-version)")
  end
  if state_is_one_of(current.state, { "reviewing" }) then
    return result("stale", "reviewing-version", "skip-stale(reviewing-version)")
  end
  return result("pending", "source-marker-not-visible", "retry-pending(from-state marker not yet visible)")
end

local function resolve_review_activation(evidence)
  if type(evidence) ~= "table"
    or not valid_current(evidence.current)
    or type(evidence.reviewing_version) ~= "string"
    or (evidence.handoff ~= nil
      and (type(evidence.handoff) ~= "table"
        or (evidence.handoff.status ~= "valid" and evidence.handoff.status ~= "invalid"))) then
    return illegal("invalid-evidence")
  end
  local current = evidence.current
  local preliminary
  if current == nil or current.version == nil then
    preliminary = "pending"
  else
    local current_base = transition_version.strip_suffixes(current.version)
    local reviewing_base = transition_version.strip_suffixes(evidence.reviewing_version)
    if state_is_one_of(current.state, { "reviewing" }) then
      preliminary = tostring(current_base) == tostring(reviewing_base) and "apply" or "version-mismatch"
    else
      local order = devloop_state.compare_state_marker_order({
        state = current.state,
        version = current_base,
      }, "reviewing", reviewing_base)
      preliminary = order < 0 and "pending" or "stale"
    end
  end

  if (preliminary == "pending" or preliminary == "version-mismatch") and valid_handoff(evidence) then
    return result("apply", "verified-own-reviewing-hand-off", "apply(verified-own-reviewing-hand-off)")
  end
  if preliminary == "version-mismatch" then
    return result("stale", "version-mismatch", "skip-stale(version-mismatch)")
  end
  if preliminary == "pending" then
    return result("pending", "reviewing-marker-not-visible", "retry-pending(reviewing marker not yet visible)")
  end
  if preliminary == "stale" then
    return result("stale", "advanced-or-diverged", "skip-stale/diverged")
  end
  return result("apply", "apply", "applied")
end

local function resolve_implement(definition, evidence, projection)
  if not valid_common_evidence(evidence)
    or (evidence.phase ~= "initial" and evidence.phase ~= "recheck")
    or type(evidence.retry) ~= "boolean"
    or (evidence.accepted_handoff ~= nil and type(evidence.accepted_handoff) ~= "boolean") then
    return illegal("invalid-evidence")
  end
  local variant, invalid = resolve_variant(definition, evidence)
  if invalid ~= nil then
    return invalid
  end
  if evidence.phase == "initial"
    and not evidence.retry
    and state_is_one_of(evidence.current and evidence.current.state, { "implementing", "impl-failed" }) then
    return result("idempotent", "already-at-target", "skip-idempotent(already at to_state)")
  end
  if evidence.phase == "recheck" and state_is_one_of(evidence.current and evidence.current.state, variant.source_states) then
    return result("apply", "structural-source-recheck", "applied")
  end
  local resolved = base_result(
    variant.base or "versioned",
    evidence.current,
    variant.source_states,
    variant.target_state,
    evidence.incoming_version,
    evidence.target_version,
    projection
  )
  if resolved.status ~= "pending" or evidence.retry then
    return resolved
  end
  if evidence.phase == "initial" and valid_handoff(evidence) then
    return result("apply", "verified-own-ready-hand-off", "apply(verified-own-ready-hand-off)")
  end
  if evidence.phase == "recheck" and evidence.accepted_handoff and valid_handoff(evidence) then
    return result("apply", "own-ready-hand-off", "apply(own-ready-hand-off)")
  end
  return resolved
end

-- Production merge admission (merge_executor.lua) applies an explicit admissible-state
-- guard (~line 350) that fires BEFORE the cyclic transition-outcome branches: any current
-- outside {merge-ready, merging, merged} is skip-stale(from-state-mismatch) regardless of
-- the probe result (pending/stale/idempotent/apply), overriding the base's stage-rank
-- admission. That override cannot be expressed as a from-state overlay (overlays only reject
-- apply/idempotent, after the status filter — they cannot downgrade a pending/stale base),
-- so it lives in a policy-private resolver, the same shape as resolve_implement's pre-checks.
-- An admissible current (incl. merged, which is admissible but not a cyclic source) then
-- follows the standard cyclic base + raw version-equality overlay. The pre-CAS merged-marker
-- idempotency (state=merged + trusted merged marker -> skip-idempotent) is downstream effect
-- idempotency, out of the admission-only catalog and not modeled here.
local function resolve_merge(definition, evidence, projection)
  if not valid_common_evidence(evidence) then
    return illegal("invalid-evidence")
  end
  if not state_is_one_of(evidence.current and evidence.current.state, { "merge-ready", "merging", "merged" }) then
    return result("stale", "from-state-mismatch", "skip-stale(from-state-mismatch)")
  end
  return resolve_profile(definition, evidence, projection)
end

local function variant(source_states, target_state, target_version)
  return {
    source_states = source_states,
    target_state = target_state,
    target_version = target_version,
  }
end

local APPLY_ONLY_RAW = { kind = "version", version_form = "raw", statuses = { apply = true } }
local APPLY_ONLY_SAFE = { kind = "version", version_form = "safe", statuses = { apply = true } }
-- review_result production (main.lua) has NO from-state guard: the cyclic base's
-- stage-rank forward-progress rule owns state admission (it admits any reachable
-- predecessor whose stage_rank < target, incl. pr-open in the observe_pr->reviewing
-- race window), then a safe-version equality check follows. So this policy-private
-- overlay opts out of the from-state downgrade and enforces only safe-version equality,
-- keeping catalog.resolve legacy-exact with the department. APPLY_ONLY_SAFE (used by
-- pr_fix_reconcile) is left unchanged.
local REVIEW_RESULT_SAFE = { kind = "version", version_form = "safe", statuses = { apply = true }, enforce_source_states = false }

local policy_order = {
  "cas.base_plain_legacy_v1",
  "cas.base_versioned_legacy_v1",
  "cas.base_cyclic_legacy_v1",
  "cas.legacy_loop_plain_v1",
  "cas.legacy_consensus_result_v1",
  "cas.legacy_issue_reconcile_v1",
  "cas.legacy_timeout_reconcile_v1",
  "cas.legacy_observe_issue_entry_v1",
  "cas.legacy_awaiting_pr_v1",
  "cas.legacy_observe_pr_v1",
  "cas.legacy_review_result_v1",
  "cas.legacy_fix_v1",
  "cas.legacy_review_meta_v1",
  "cas.legacy_merge_v1",
  "cas.legacy_pr_fix_reconcile_v1",
  "cas.legacy_review_loop_safe_v1",
  "cas.legacy_review_activation_handoff_v1",
  "cas.legacy_implement_activation_handoff_v1",
}

local policies = {
  ["cas.base_plain_legacy_v1"] = {
    evidence_type = "cas_base_evidence_v1",
    production = { function_name = "transition_status", source = "libraries/devloop/state.lua:505" },
    resolve = function(evidence, projection) return resolve_base(evidence, "plain", projection) end,
  },
  ["cas.base_versioned_legacy_v1"] = {
    evidence_type = "cas_base_evidence_v1",
    production = { function_name = "versioned_transition_status", source = "libraries/devloop/state.lua:527" },
    resolve = function(evidence, projection) return resolve_base(evidence, "versioned", projection) end,
  },
  ["cas.base_cyclic_legacy_v1"] = {
    evidence_type = "cas_base_evidence_v1",
    production = { function_name = "cyclic_transition_status", source = "libraries/devloop/state.lua:538" },
    resolve = function(evidence, projection) return resolve_base(evidence, "cyclic", projection) end,
  },
  ["cas.legacy_loop_plain_v1"] = {
    evidence_type = "loop_cas_evidence_v1",
    production = { function_name = "transition_status", overlay = "none", source = "packages/github-devloop/departments/loop/main.lua:75" },
    base = "plain",
    variants = { thinking_to_blocked = variant({ "thinking" }, "blocked") },
    overlay = { kind = "none", statuses = {} },
  },
  ["cas.legacy_consensus_result_v1"] = {
    evidence_type = "consensus_result_cas_evidence_v1",
    -- Admission-only: the versioned base fully models production's version-concurrency CAS
    -- (consensus_result/main.lua:175 versioned_transition_status). Production's exact-version
    -- idempotent effect-completeness REPAIR (idempotent probe + effects incomplete → re-raise
    -- the result effects at the SAME version) is a POST-admission effect-idempotency
    -- disposition, not a version-CAS admission, so it is NOT folded into the catalog status
    -- (composed downstream, like merge's re-merge). catalog.resolve returns idempotent for the
    -- idempotent probe regardless of effect completeness; the re-apply is a separate axis
    -- verified by the non-circular consensus_result parity harness (post_admission_disposition).
    production = { function_name = "versioned_transition_status", overlay = "admission-only versioned base (effect-completeness repair is a downstream disposition)", source = "packages/github-devloop/departments/consensus_result/main.lua:175" },
    base = "versioned",
    variants = {
      thinking_to_ready = variant({ "thinking" }, "ready"),
      thinking_to_dependency_wait = variant({ "thinking" }, "dependency_wait"),
    },
    overlay = { kind = "none", statuses = {} },
  },
  ["cas.legacy_issue_reconcile_v1"] = {
    evidence_type = "reconcile_cas_evidence_v1",
    production = { function_name = "versioned_transition_status", overlay = "state and terminal checks", source = "packages/github-devloop/departments/reconcile/main.lua:136" },
    base = "versioned",
    variants = {
      thinking_to_blocked = variant({ "thinking" }, "blocked"),
      reviewing_to_blocked = variant({ "reviewing" }, "blocked"),
    },
    overlay = { kind = "none", statuses = {} },
  },
  ["cas.legacy_timeout_reconcile_v1"] = {
    evidence_type = "timeout_reconcile_cas_evidence_v1",
    production = { function_name = "versioned_transition_status", overlay = "fresh-instance due attempt and lineage checks", source = "packages/github-devloop/departments/reconcile/main.lua:254" },
    base = "versioned",
    variants = {
      thinking_to_blocked = variant({ "thinking" }, "blocked"),
      ready_to_blocked = variant({ "ready" }, "blocked"),
      implementing_to_blocked = variant({ "implementing" }, "blocked"),
      reviewing_to_blocked = variant({ "reviewing" }, "blocked"),
      fixing_to_blocked = variant({ "fixing" }, "blocked"),
      merge_ready_to_blocked = variant({ "merge-ready" }, "blocked"),
      merging_to_blocked = variant({ "merging" }, "blocked"),
    },
    overlay = { kind = "none", statuses = {} },
  },
  ["cas.legacy_observe_issue_entry_v1"] = {
    evidence_type = "observe_issue_entry_cas_evidence_v1",
    production = { function_name = "versioned_transition_status", overlay = "current exact branch order", source = "packages/github-devloop/departments/observe_issue/main.lua:744" },
    base = "versioned",
    variants = { unmanaged_to_thinking = variant({ "unmanaged" }, "thinking") },
    overlay = { kind = "none", statuses = {} },
  },
  ["cas.legacy_awaiting_pr_v1"] = {
    evidence_type = "awaiting_pr_cas_evidence_v1",
    production = { function_name = "versioned_transition_status", overlay = "parent and child fact checks", source = "packages/github-devloop/core/awaiting_pr_replayer.lua:174" },
    base = "versioned",
    variants = {
      implementing_to_awaiting_pr = variant({ "implementing" }, "awaiting-pr"),
      awaiting_pr_to_merged = variant({ "awaiting-pr" }, "merged"),
      awaiting_pr_to_ready = variant({ "awaiting-pr" }, "ready"),
      awaiting_pr_to_blocked = variant({ "awaiting-pr" }, "blocked"),
    },
    overlay = { kind = "none", statuses = {} },
  },
  ["cas.legacy_observe_pr_v1"] = {
    evidence_type = "observe_pr_cas_evidence_v1",
    production = { function_name = "versioned_transition_status", overlay = "raw pr-open version equality", source = "packages/github-devloop-pr/departments/observe_pr/main.lua:586" },
    base = "versioned",
    variants = { pr_open_to_reviewing = variant({ "pr-open", "unmanaged" }, "reviewing") },
    overlay = { kind = "version", version_form = "raw", statuses = { apply = true }, only_states = { "pr-open" } },
  },
  ["cas.legacy_review_result_v1"] = {
    evidence_type = "review_result_cas_evidence_v1",
    production = { function_name = "cyclic_transition_status", overlay = "safe-segment equality", source = "packages/github-devloop-pr/departments/review_result/main.lua:185" },
    base = "cyclic",
    base_current_version_form = "safe",
    variants = {
      reviewing_to_merge_ready = variant({ "reviewing" }, "merge-ready"),
      reviewing_to_fixing = variant({ "reviewing" }, "fixing"),
      reviewing_to_review_meta = variant({ "reviewing" }, "review-meta"),
    },
    overlay = REVIEW_RESULT_SAFE,
  },
  ["cas.legacy_fix_v1"] = {
    evidence_type = "fix_cas_evidence_v1",
    production = { function_name = "cyclic_transition_status", overlay = "raw fixing version equality", source = "packages/github-devloop-pr/departments/fix/main.lua:516" },
    base = "cyclic",
    variants = { fixing_to_reviewing = variant({ "fixing" }, "reviewing") },
    overlay = APPLY_ONLY_RAW,
  },
  ["cas.legacy_review_meta_v1"] = {
    evidence_type = "review_meta_cas_evidence_v1",
    production = { function_name = "cyclic_transition_status", overlay = "raw review-meta version equality", source = "packages/github-devloop-pr/departments/review_meta/main.lua:201" },
    base = "cyclic",
    variants = {
      predecision_eligibility = {
        source_states = { "review-meta" },
        legacy_probe_target_state = "fixing",
      },
    },
    overlay = APPLY_ONLY_RAW,
  },
  ["cas.legacy_merge_v1"] = {
    evidence_type = "merge_cas_evidence_v1",
    production = { function_name = "cyclic_transition_status", overlay = "admissible state and raw version equality", source = "packages/github-devloop-pr/core/merge_executor.lua:349" },
    base = "cyclic",
    variants = {
      merge_ready_to_merging = variant({ "merge-ready" }, "merging"),
      merge_ready_or_merging_to_merging = variant({ "merge-ready", "merging" }, "merging"),
    },
    -- resolve_merge applies production's admissible-state guard (current ∈ {merge-ready,
    -- merging, merged}) before delegating an admissible current to the standard cyclic
    -- base + this raw version-equality overlay; see resolve_merge for the rationale.
    overlay = { kind = "version", version_form = "raw", statuses = { apply = true, idempotent = true } },
    resolve_profile = resolve_merge,
  },
  ["cas.legacy_pr_fix_reconcile_v1"] = {
    evidence_type = "pr_fix_reconcile_cas_evidence_v1",
    production = { function_name = "versioned_transition_status", overlay = "safe-segment source equality", source = "packages/github-devloop-pr/departments/reconcile/main.lua:293" },
    base = "versioned",
    variants = {
      review_reject_to_blocked = variant({ "reviewing", "fixing", "merge-ready", "merging" }, "blocked"),
      bounded_fix_to_blocked = variant({ "fixing", "merge-ready", "merging" }, "blocked"),
    },
    overlay = APPLY_ONLY_SAFE,
  },
  ["cas.legacy_review_loop_safe_v1"] = {
    evidence_type = "review_loop_safe_cas_evidence_v1",
    production = { function_name = "reviewing_segment_transition_status", overlay = "dedicated safe equality without ordering", source = "packages/github-devloop-pr/departments/review_loop/main.lua:71" },
    resolve = resolve_review_loop,
  },
  ["cas.legacy_review_activation_handoff_v1"] = {
    evidence_type = "review_activation_handoff_cas_evidence_v1",
    production = { function_name = "reviewing_transition_status", overlay = "direct-ID handoff", source = "packages/github-devloop-pr/departments/review_pr/main.lua:29" },
    resolve = resolve_review_activation,
  },
  ["cas.legacy_implement_activation_handoff_v1"] = {
    evidence_type = "implement_activation_handoff_cas_evidence_v1",
    production = { function_name = "implementation_transition_status", overlay = "initial direct-ID once and structural rechecks", source = "packages/github-devloop/departments/implement/transitions.lua:50" },
    variants = {
      ready_to_implementing = variant({ "ready" }, "implementing"),
      impl_failed_to_implementing = variant({ "impl-failed" }, "implementing"),
      blocked_to_implementing = {
        source_states = { "blocked" },
        target_state = "implementing",
        base = "cyclic",
      },
    },
    resolve_profile = resolve_implement,
  },
}

for id, definition in pairs(policies) do
  definition.id = id
end

local function deep_copy(value)
  if type(value) ~= "table" then
    return value
  end
  local out = {}
  for key, nested in pairs(value) do
    out[key] = deep_copy(nested)
  end
  return out
end

function M.policy_ids()
  local out = {}
  for _, id in ipairs(policy_order) do
    table.insert(out, id)
  end
  return out
end

function M.definition(policy_id)
  local definition = policies[policy_id]
  if definition == nil then
    return nil
  end
  local out = deep_copy(definition)
  out.resolve = nil
  out.resolve_profile = nil
  return out
end

function M.resolve(policy_id, evidence, projection)
  local definition = policies[policy_id]
  if definition == nil then
    return illegal("unknown-cas-policy")
  end
  if definition.resolve ~= nil then
    return definition.resolve(evidence, projection)
  end
  local resolver = definition.resolve_profile or resolve_profile
  return resolver(definition, evidence, projection)
end

return M
