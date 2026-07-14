local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local parsers_issue = require("devloop.parsers.issue")
local core, replay_fields = require("core"), require("devloop.replay_fields")
local transition_version = require("contract.transition_version")

local saga = require("workflow.saga")
local conv_reconcile = require("devloop.convergence.reconcile")
local conv_attempts = require("devloop.convergence.attempts")
local entity_lib = require("devloop.entity")

local spec = {
  consumes = { "devloop_reconcile", "devloop_timeout_reconcile" },
  produces = {
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
    "github-proxy.github_pr_comment_request",
  },
  stall_window = "2m",
}

local function emit_effects(proposal_id, effects)
  for _, effect in ipairs(effects or {}) do
    core.log_raise("reconcile", proposal_id, effect.queue, effect.payload)
  end
end

local function emit_blocked_reconcile(proposal_id, state, version, action, reason, comment_request, label_request)
  local add_labels, remove_labels = core.state_label_changes("blocked")
  core.log_cas_decision("reconcile", proposal_id, state, "thinking", "blocked", "applied", reason)
  core.log_apply("reconcile", proposal_id, "blocked", version, { add = add_labels, remove = remove_labels }, {
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
  })
  core.log_raise("reconcile", proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  if label_request ~= nil then
    core.log_raise("reconcile", proposal_id, "github-proxy.github_issue_label_request", label_request)
  end
end

local function strip_timeout_transition_suffixes(version)
  local text = tostring(version or "")
  local previous = nil
  while previous ~= text do
    previous = text
    text = text
      :gsub("/timeout%-reconcile/[%w%-]+/%d+$", "")
      :gsub("%-timeout%-reconcile%-[%w%-]+%-%d+$", "")
      :gsub("/timeout/[%w%-]+/%d+$", "")
      :gsub("%-timeout%-[%w%-]+%-%d+$", "")
  end
  return text
end

local function maybe_adopt_open_implementation_pr(repo, issue_number, reconcile, current, state)
  if reconcile.state ~= "implementing" then
    return false
  end
  local impl_version = strip_timeout_transition_suffixes(state.version)
  local ready = {
    proposal_id = reconcile.proposal_id,
    dedup_key = impl_version,
    source_ref = reconcile.source_ref,
  }
  local issue = {
    repo = repo,
    number = issue_number,
    title = current and current.title,
    proposal_id = reconcile.proposal_id,
    source_ref = reconcile.source_ref,
    comments = current and current.comments or {},
  }
  local child = core.adopt_existing_pr_child(issue, impl_version, core.implementation_retry_attempt(impl_version) or 1)
  if child == nil then
    return false
  end
  local comment_request = core.build_parent_awaiting_pr_comment_request(repo, issue_number, ready, child)
  local label_request = core.build_parent_awaiting_pr_label_request(repo, issue_number, ready, child)
  local add_labels, remove_labels = core.state_label_changes("awaiting-pr")
  core.log_cas_decision("reconcile", reconcile.proposal_id, state, "implementing", "awaiting-pr", "applied(open-pr-ground-truth)", "timeout reconcile found an open implementation PR before terminal write")
  core.log_apply("reconcile", reconcile.proposal_id, "awaiting-pr", impl_version, { add = add_labels, remove = remove_labels }, {
    "github-proxy.github_pr_comment_request",
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
  })
  emit_effects(reconcile.proposal_id, child.effects)
  core.log_raise("reconcile", reconcile.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  core.log_raise("reconcile", reconcile.proposal_id, "github-proxy.github_issue_label_request", label_request)
  return true
end

local function pipeline_thinking(event)
  local reconcile = event.payload or {}
  if not conv_reconcile.is_supported_reconcile(core, reconcile) then
    core.log_entry("reconcile", event, "unknown", core.payload_field(reconcile, "dedup_key"))
    core.log_cas_decision("reconcile", "unknown", { state = nil, version = nil }, "thinking", "blocked", "skip-foreign(proposal_id)", "unsupported event payload")
    return
  end

  core.log_entry("reconcile", event, reconcile.proposal_id, reconcile.dedup_key)
  local repo, issue_number = base_ids.parse_proposal_id(reconcile.proposal_id)
  if repo == nil then
    core.log_cas_decision("reconcile", reconcile.proposal_id, { state = nil, version = nil }, "thinking", "blocked", "skip-foreign(proposal_id)", "proposal_id is outside github-devloop")
    return
  end

  local lock_key = entity_lib.loop_lock_key(reconcile.proposal_id)
  if lock_key == nil then
    core.log_cas_decision("reconcile", reconcile.proposal_id, { state = nil, version = nil }, "thinking", "blocked", "skip-foreign(proposal_id)", "no transition lock key")
    return
  end

  with_lock(lock_key, function()
    devloop_base.assert_trusted_bot_configured()

    local view = core.gh_issue_view_loop(repo, issue_number, 30)
    if view.exit_code ~= 0 then
      error("github-devloop: gh issue reconcile view failed: " .. tostring(view.stderr))
    end

    local current = parsers_issue.parse_issue_view_loop(core, view.stdout)
    core.log_forged_markers("reconcile", reconcile.proposal_id, current.comments)
    local state = core.current_state(current.comments, reconcile.proposal_id)
    if conv_reconcile.has_reconcile_marker(core, current.comments, reconcile.proposal_id, reconcile.base_version, reconcile.round) then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "thinking", "blocked", "skip-idempotent(reconcile marker already visible)", "reconcile result marker for incoming version is already visible")
      return
    end
    if state.state ~= nil and core.stage_rank(state.state) >= core.stage_rank("blocked") then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "thinking", "blocked", "skip-idempotent(already terminal)", "current marker is already terminal at or beyond blocked")
      return
    end
    if state.state == nil then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "thinking", "blocked", "pending", "thinking state marker not yet visible")
      error("github-devloop: thinking state marker not yet visible for reconcile; retrying")
    end
    if state.state ~= "thinking" then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "thinking", "blocked", "skip-stale(state-advanced)", "current marker advanced beyond thinking")
      return
    end

    local version = conv_reconcile.reconcile_terminal_state_version(core, state.version, reconcile.round)
    local transition = core.versioned_transition_status(state, { "thinking" }, "blocked", version)
    if transition == "pending" then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "thinking", "blocked", core.cas_outcome(state, transition, version), "thinking state marker not yet visible")
      error("github-devloop: thinking state marker not yet visible for reconcile; retrying")
    end
    if transition == "idempotent" or transition == "stale" then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "thinking", "blocked", core.cas_outcome(state, transition, version), "current marker cannot be reconciled from thinking")
      return
    end

    -- re-design/re-cluster require a trusted directive fact; current deterministic reconcile drops.
    local action = "drop"
    local reason = "no-actionable-framing-after-" .. tostring(reconcile.round) .. "-rounds"
    local comment_request = core.build_reconcile_comment_request(repo, issue_number, reconcile, action, reason, version)
    local label_request = core.build_reconcile_label_request(repo, issue_number, reconcile)
    emit_blocked_reconcile(reconcile.proposal_id, state, version, action, reason, comment_request, label_request)
  end)
end

local function pipeline_timeout(event)
  local reconcile = event.payload or {}
  if not conv_reconcile.is_supported_timeout_reconcile(core, reconcile) then
    core.log_entry("reconcile", event, "unknown", core.payload_field(reconcile, "dedup_key"))
    core.log_cas_decision("reconcile", "unknown", { state = nil, version = nil }, "timeout", "blocked", "skip-foreign(proposal_id)", "unsupported event payload")
    return
  end

  core.log_entry("reconcile", event, reconcile.proposal_id, reconcile.dedup_key)
  local repo, issue_number = base_ids.parse_proposal_id(reconcile.proposal_id)
  local lock_key = entity_lib.transition_lock_key(reconcile.proposal_id)
  if lock_key == nil then
    core.log_cas_decision("reconcile", reconcile.proposal_id, { state = nil, version = nil }, reconcile.state, "blocked", "skip-foreign(proposal_id)", "no transition lock key")
    return
  end

  with_lock(lock_key, function()
    devloop_base.assert_trusted_bot_configured()

    local view = core.gh_issue_view_loop(repo, issue_number, 30)
    if view.exit_code ~= 0 then
      error("github-devloop: timeout-reconcile-issue-view-failed: " .. tostring(view.stderr))
    end

    local current = parsers_issue.parse_issue_view_loop(core, view.stdout)
    local comments = current.comments or {}
    core.log_forged_markers("reconcile", reconcile.proposal_id, comments)
    local state = require("devloop.entity").current_entity_state(core, comments, reconcile.proposal_id)
    if conv_reconcile.has_timeout_reconcile_marker(core, comments, reconcile.proposal_id, reconcile.issue_version, reconcile.state, reconcile.round) then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", "skip-idempotent(timeout reconcile marker already visible)", "timeout reconcile result marker for incoming version is already visible")
      return
    end
    local live_row = replay_fields.restart_transition_row(core.restart_transition_table(), state.state)
    if state.state ~= nil and live_row ~= nil and live_row.terminal == true then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", "skip-idempotent(already terminal)", "current marker is already terminal")
      return
    end
    if state.state == nil then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", "pending", "state marker not yet visible for timeout reconcile")
      error("github-devloop: state marker not yet visible for timeout reconcile; retrying")
    end
    if state.state ~= reconcile.state then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", "skip-stale(state-advanced)", "current marker advanced beyond timeout reconcile state")
      return
    end
    if transition_version.strip_suffixes(state.version) ~= transition_version.strip_suffixes(reconcile.issue_version) then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", "skip-stale(lineage-mismatch)", "timeout reconcile event does not match canonical state marker lineage")
      return
    end

    local row = replay_fields.restart_transition_row(core.restart_transition_table(), reconcile.state)
    local timeout_facts = {
      proposal_id = reconcile.proposal_id,
      current = current,
      current_issue = current,
      source_ref = reconcile.source_ref,
      fresh_current_state = state,
    }
    local epoch = row and row.actionable_epoch
    if type(epoch) == "table" and epoch.allows_state_entry_if_never_deferred == true then
      timeout_facts.dependency_gate = core.dependency_gate(repo, issue_number, {
        proposal_id = reconcile.proposal_id,
        version = state.version,
        comments = comments,
      })
    end
    local due, age_minutes = core.liveness_timeout_due_with_facts(row, state, timeout_facts, now())
    local decision = core.liveness_timeout_decision_with_facts(row, state, timeout_facts, now())
    if row
      and row.actionable_epoch
      and row.actionable_epoch.source == "live_defer_heartbeat:v1" then
      local signal = core.restart_row_liveness_signal(row, state, timeout_facts, now())
      age_minutes = signal.age_minutes or age_minutes
    end
    local limit = tonumber(row and row.on_timeout and row.on_timeout.escalate_after_attempts) or nil
    if not due or decision.action ~= "escalate" or tonumber(decision.attempt) < tonumber(reconcile.round) then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", "skip-stale(no-longer-over-budget)", "current marker is no longer at timeout escalation threshold")
      return
    end
    if maybe_adopt_open_implementation_pr(repo, issue_number, reconcile, current, state) then
      return
    end
    if reconcile.state == "blocked" then
      if conv_attempts.has_decompose_exhausted_marker(core, comments, reconcile.proposal_id, state.version) then
        core.log_cas_decision("reconcile", reconcile.proposal_id, state, "blocked", "devloop_decompose", "skip-idempotent(decompose-exhausted)", "blocked decompose output obligation already reached terminal stop")
        return
      end
      local comment_request = conv_attempts.build_decompose_exhausted_comment_request(core, {
        kind = "issue",
        repo = repo,
        number = issue_number,
      }, reconcile.proposal_id, state, reconcile.source_ref, decision.attempt)
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, "blocked", "devloop_decompose", "applied(decompose-exhausted)", "blocked decompose output obligation exhausted")
      core.log_apply("reconcile", reconcile.proposal_id, nil, nil, { add = {}, remove = {} }, { "github-proxy.github_issue_comment_request" })
      core.log_raise("reconcile", reconcile.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
      return
    end

    local version = conv_reconcile.timeout_reconcile_state_version(core, state.version, reconcile.state, decision.attempt)
    local transition = core.versioned_transition_status(state, { reconcile.state }, "blocked", version)
    if transition == "pending" then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", core.cas_outcome(state, transition, version), "state marker not yet visible for timeout reconcile")
      error("github-devloop: state marker not yet visible for timeout reconcile; retrying")
    end
    if transition == "idempotent" or transition == "stale" then
      core.log_cas_decision("reconcile", reconcile.proposal_id, state, reconcile.state, "blocked", core.cas_outcome(state, transition, version), "current marker cannot be timeout reconciled")
      return
    end

    local action = "drop"
    local reason_prefix = row and row.on_timeout and row.on_timeout.on_escalate and row.on_timeout.on_escalate.reason
      or "state-output-obligation-timeout"
    local reason = tostring(reason_prefix) .. "-after-" .. tostring(decision.attempt) .. "-attempts"
    local why_fields = {
      from_state = reconcile.state,
      from_version = state.version,
      terminal_version = version,
      age_minutes = age_minutes,
      budget_minutes = row and row.budget and tonumber(row.budget.minutes) or nil,
      attempt = decision.attempt,
      attempt_limit = limit,
      driving_queue = row and row.driving_queue or nil,
      reason_class = "state-output-obligation-timeout",
      source_ref = base_ids.normalize_source_ref(reconcile.source_ref),
    }
    local comment_request = conv_reconcile.build_timeout_reconcile_comment_request(core, repo, issue_number, reconcile, action, reason, version, why_fields)
    local label_request = requests_labels.build_state_label_request(core, repo, issue_number, "blocked", base_ids.dedup_key({
      "timeout-reconcile",
      "label",
      tostring(reconcile.dedup_key),
    }), reconcile.source_ref)
    emit_blocked_reconcile(reconcile.proposal_id, state, version, action, reason, comment_request, label_request)
  end)
end

return saga.department(spec, {
  done = function() return false end,
  act = function(event)
    local schema = core.payload_field(event and event.payload, "schema")
    if schema == "github-devloop.timeout-reconcile.v1" then
      return pipeline_timeout(event)
    end
    return pipeline_thinking(event)
  end,
  wrap = core.wrap_pipeline_failure,
  name = "reconcile",
})
