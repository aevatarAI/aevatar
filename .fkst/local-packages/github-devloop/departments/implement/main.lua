local git_mechanics = require("devloop.git_mechanics")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local requests_labels = require("devloop.requests.labels")
local requests_lifecycle = require("devloop.requests.lifecycle")
local parsers_issue = require("devloop.parsers.issue")
local core = require("core")
local git_adapter = require("forge.git")
local queue = require("devloop.queue")
local saga = require("workflow.saga")
local pr_child_handoff = require("departments.implement.pr_child_handoff")
local forks = require("devloop.forks")
local slice_gate = require("departments.implement.slice_gate")
local substrate_pin = require("departments.implement.substrate_pin")
local transitions = require("departments.implement.transitions")
local worktree_lifecycle = require("departments.implement.worktree")
local dispatch_live_run = require("devloop.dispatch_live_run")
local context_bundle = require("devloop.context_bundle")
local config = require("devloop.config")
local fork_gate = require("departments.implement.fork_gate")
local m_mq = require("devloop.merge_queue")

local payloads_builders = require("devloop.payloads.builders")
local payloads_predicates = require("devloop.payloads.predicates")
local v_ready = require("devloop.validators.ready")
local m_facts = require("devloop.markers.facts")
local entity_lib = require("devloop.entity")
local MAX_IMPLEMENT_ATTEMPTS = 2
local MAX_VERSION_MISMATCH_DELIVERIES = 3
local implemented_branch_head
local spec = {
  consumes = { "devloop_ready" },
  produces = {
    "github-proxy.github_issue_label_request",
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_pr_comment_request",
  },
  stall_window = "10m",
  retry = { max_attempts = 12, base = "5s", cap = "30s" },
}

local git = git_adapter.production_handle

local function implement_done(_event)
  return false
end

local function raise_impl_failed(repo, issue_number, ready, reason, detail, attempt)
  local comment_request = requests_lifecycle.build_impl_failure_comment_request(core, repo, issue_number, ready, reason, detail, attempt)
  local label_request = requests_labels.build_impl_failed_label_request(core, repo, issue_number, ready, reason)
  local add_labels, remove_labels = core.state_label_changes("impl-failed")
  core.log_apply("implement", ready.proposal_id, "impl-failed", ready.dedup_key, { add = add_labels, remove = remove_labels }, {
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
  })
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_label_request", label_request)
end

local function raise_implementing_state(repo, issue_number, ready, worktree, branch, base_branch, base_sha, attempt, started_at, exec_ref)
  local comment_request = requests_lifecycle.build_implementing_state_comment_request(core, repo, issue_number, ready, worktree, branch, base_branch, base_sha, attempt, started_at, exec_ref)
  local label_request = requests_labels.build_implementing_label_request(core, repo, issue_number, ready)
  local add_labels, remove_labels = core.state_label_changes("implementing")
  core.log_apply("implement", ready.proposal_id, "implementing", ready.dedup_key, { add = add_labels, remove = remove_labels }, {
    "github-proxy.github_issue_comment_request",
    "github-proxy.github_issue_label_request",
  })
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_label_request", label_request)
end

local function raise_implementing(repo, issue_number, ready, worktree, branch, head_sha, base_branch, base_sha, attempt, started_at, exec_ref)
  local comment_request = requests_lifecycle.build_implementing_comment_request(core, repo, issue_number, ready, worktree, branch, head_sha, base_branch, base_sha, attempt, started_at, exec_ref)
  core.log_apply("implement", ready.proposal_id, "implementing", ready.dedup_key, { add = {}, remove = {} }, {
    "github-proxy.github_issue_comment_request",
  })
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
end

local function raise_implement_attempt(repo, issue_number, ready, attempt, started_at, exec_ref)
  local request = requests_lifecycle.build_implement_attempt_comment_request(core, repo, issue_number, ready, attempt, started_at, exec_ref)
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request", request)
end

local function publish_implementation_branch(repo, issue_number, ready, worktree, branch)
  if config.write_mode(core) ~= "real" then
    core.log_line("info", "implement", ready.proposal_id, "OUTBOUND", {
      "mode=dry-run",
      "repo=" .. tostring(repo),
      "issue=" .. tostring(issue_number),
      "branch=" .. tostring(branch),
      "reason=would push implementation branch requires FKST_GITHUB_WRITE=1",
    })
    return
  end
  local push = git_mechanics.git_push_worktree_branch_update(core.git, worktree, branch, 120)
  if push.exit_code ~= 0 then
    error("github-devloop: IMPLEMENT_BRANCH_PUSH_FAILED: git implementation branch push failed: " .. tostring(push.stderr))
  end
end

local function remote_branch_fact(branch, base_branch, source_fact)
  local fetch_result = core.git_fetch_branch("origin", branch, 60)
  if fetch_result.exit_code ~= 0 then
    return nil
  end
  local head_result = core.git_remote_branch_head("origin", branch, 30)
  if head_result.exit_code ~= 0 then
    if head_result.exit_code == 1 then
      return nil
    end
    error("github-devloop: git implementing remote branch head failed: " .. tostring(head_result.stderr))
  end
  local head_sha = tostring(head_result.stdout or ""):gsub("%s+$", "")
  if not require("devloop.pr_safety").is_safe_head_sha(head_sha) then
    error("github-devloop: unsafe implementing remote branch head")
  end
  if source_fact ~= nil and source_fact.head_sha ~= nil and head_sha ~= source_fact.head_sha then
    local ancestry = git_mechanics.git_is_ancestor(core.git, source_fact.head_sha, head_sha, 30)
    if ancestry.exit_code ~= 0 then
      return nil
    end
  end
  return {
    proposal_id = source_fact and source_fact.proposal_id,
    dedup_key = source_fact and source_fact.dedup_key,
    branch = branch,
    head_sha = head_sha,
    base_branch = (source_fact and source_fact.base_branch) or base_branch,
    base_sha = source_fact and source_fact.base_sha,
  }
end

local function local_branch_fact(base_head, branch, base_branch, dedup_key)
  local branch_ref = core.git_show_ref_branch(branch, 30)
  if branch_ref.exit_code ~= 0 then
    if branch_ref.exit_code == 1 then
      return nil
    end
    error("github-devloop: git branch ref check failed: " .. tostring(branch_ref.stderr))
  end
  local head_sha = implemented_branch_head(base_head, branch)
  if head_sha == nil or substrate_pin.is_only_pin_delta(base_head, branch) then
    return nil
  end
  return {
    dedup_key = dedup_key,
    branch = branch,
    head_sha = head_sha,
    base_branch = base_branch,
    base_sha = base_head,
  }
end

local function handoff_existing_pr_link(repo, issue_number, ready, current, link, reason)
  pr_child_handoff.raise_awaiting_pr_from_fact("implement", repo, issue_number, ready, current, {
    proposal_id = ready.proposal_id,
    dedup_key = ready.dedup_key,
    branch = link.branch,
    head_sha = nil,
    base_branch = link.base_branch,
  }, reason)
end

local function ready_for_implementation_version(ready, version)
  local copy = {}
  for key, value in pairs(ready or {}) do
    copy[key] = value
  end
  copy.dedup_key = version
  return copy
end

local function raise_implement_version_mismatch(repo, issue_number, ready, state, expected_version, attempt)
  local request = requests_lifecycle.build_implement_version_mismatch_comment_request(core,
    repo,
    issue_number,
    ready,
    expected_version,
    state and state.version,
    attempt
  )
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request", request)
end

local function handle_implementing_version_mismatch(repo, issue_number, current, ready, state, expected_version)
  local prior_attempts = core.implement_version_mismatch_attempt_count(
    current and current.comments,
    ready.proposal_id,
    expected_version,
    state and state.version
  )
  local attempt = prior_attempts + 1
  local message = "ready event does not match current implementing version"
  if attempt < MAX_VERSION_MISMATCH_DELIVERIES then
    core.log_error_fact("warn", "implement", ready.proposal_id, "STALE_VERSION_MISMATCH", "devloop_ready", message, {
      source_ref = ready.source_ref,
      attempt = attempt,
      terminal = false,
    })
    core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "skip-stale(version-mismatch)", message)
    raise_implement_version_mismatch(repo, issue_number, ready, state, expected_version, attempt)
    error("github-devloop: implement-version-mismatch retrying: ready event version "
      .. tostring(expected_version or "")
      .. " does not match current implementing version "
      .. tostring(state and state.version or ""))
  end
  core.log_error_fact("error", "implement", ready.proposal_id, "STALE_VERSION_MISMATCH", "devloop_ready", message, {
    source_ref = ready.source_ref,
    attempt = attempt,
    terminal = true,
  })
  core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "fail-closed(version-mismatch-budget)", message)
  error("github-devloop: implement-version-mismatch: ready event version "
    .. tostring(expected_version or "")
    .. " does not match current implementing version "
    .. tostring(state and state.version or ""))
end

local function implementing_mismatch_is_durable(current, proposal_id, state)
  local version = state and state.version
  return core.latest_implement_attempt_fact(current and current.comments, proposal_id, version) ~= nil
    or m_facts.implementing_fact(core, current and current.comments, proposal_id, version) ~= nil
end

local function live_implement_attempt_visible(comments, proposal_id, version)
  local _, status = core.implement_attempt_exec_live_fact(comments, proposal_id, version)
  return status == "running"
end

implemented_branch_head = function(base_head, branch)
  local ahead_result = core.git_branch_ahead_count(base_head, branch, 30)
  if ahead_result.exit_code ~= 0 then
    error("github-devloop: git branch ahead check failed: " .. tostring(ahead_result.stderr))
  end
  local ahead_count = tonumber(tostring(ahead_result.stdout or ""):match("%d+"))
  if ahead_count == nil or ahead_count <= 0 then
    return nil
  end

  local head_result = core.git_branch_head(branch, 30)
  if head_result.exit_code ~= 0 then
    error("github-devloop: git branch head failed: " .. tostring(head_result.stderr))
  end
  local head_sha = tostring(head_result.stdout or ""):gsub("%s+$", "")
  if not require("devloop.pr_safety").is_safe_head_sha(head_sha) then
    error("github-devloop: unsafe implementing branch head")
  end
  return head_sha
end

local function merge_integration_for_implementation(worktree, integration_branch, base_head)
  local merge_result = core.git_worktree_merge_no_edit(worktree, base_head, 120)
  if merge_result.exit_code == 0 then return true end
  local unmerged_result = core.git.unmerged_paths(worktree, 30)
  if unmerged_result.exit_code ~= 0 then
    error("github-devloop: git unmerged path check failed: " .. tostring(unmerged_result.stderr))
  end
  if tostring(unmerged_result.stdout or "") == "" then
    error("github-devloop: git integration merge failed: " .. tostring(merge_result.stderr))
  end
  core.log_line("info", "implement", "merge-target", "MERGE_SKEW", {
    "integration_branch=" .. tostring(integration_branch),
    "integration_sha=" .. tostring(base_head),
    "reason=integration merge requires codex conflict resolution",
  })
  return false
end

local function prepare_attempt(repo, issue_number, ready, branches, branch, base_head, attempt)
  local worktree = worktree_lifecycle.prepare_worktree(repo, issue_number, ready, branch, base_head)
  substrate_pin.refresh(worktree, branch, base_head, merge_integration_for_implementation(worktree, branches.integration, base_head))

  local codex_started_at = now()
  local exec_ref = core.implement_exec_ref(ready.proposal_id, ready.dedup_key)
  raise_implementing_state(repo, issue_number, ready, worktree, branch, branches.integration, base_head, attempt, codex_started_at, exec_ref)
  return worktree, codex_started_at, exec_ref
end

local function run_attempt(repo, issue_number, ready, current, branches, branch, base_head, worktree, codex_started_at, exec_ref, attempt, event_ts, event_queue)
  core.log_codex_start("implement", ready.proposal_id, "implement")
  local content_fetch = context_bundle.context_fetch_from_bundle(core, {
    dept = "implement",
    repo = repo,
    issue_number = issue_number,
    proposal_id = ready.proposal_id,
    version = ready.dedup_key,
    tick = event_ts,
  })
  local result = spawn_codex_sync({
    prompt = core.build_implement_prompt(ready.proposal_id, current, ready.framing, content_fetch),
    worktree = worktree, timeout = 2 * 60 * 60,  -- 2h: implement loops code+test until green; complex tasks exceed the 60min default (#1481)
    role = "implement", proposal_id = ready.proposal_id, dedup_key = ready.dedup_key,
  })

  if type(result) ~= "table" or result.exit_code ~= 0 then
    local stderr = type(result) == "table" and result.stderr or "nil result"
    core.log_codex_result("implement", ready.proposal_id, "implement", result, nil, stderr, {
      queue = event_queue,
      source_ref = ready.source_ref,
      terminal = false,
    })
    return {
      kind = "impl-failed",
      ready = ready,
      reason = "codex-failed",
      detail = stderr,
      attempt = attempt,
      started_at = codex_started_at,
      exec_ref = exec_ref,
      finished_at = now(),
      base_sha = base_head,
      outcome = "failed: codex-failed",
    }
  end
  core.log_codex_result("implement", ready.proposal_id, "implement", result, "result=completed", nil)

  local status = core.git_status(worktree, 30)
  if status.exit_code ~= 0 then
    error("github-devloop: git status failed: " .. tostring(status.stderr))
  end

  if tostring(status.stdout or "") == "" then
    local head_sha = implemented_branch_head(base_head, branch)
    if head_sha ~= nil and not substrate_pin.is_only_pin_delta(base_head, branch) then
      core.log_line("info", "implement", ready.proposal_id, "IMPLEMENT", {
        "branch=" .. tostring(branch),
        "head_sha=" .. tostring(head_sha),
        "reason=reusing clean ahead implementation branch",
      })
      return {
        kind = "implementing",
        ready = ready,
        worktree = worktree,
        branch = branch,
        head_sha = head_sha,
        base_branch = branches.integration,
        base_sha = base_head,
        attempt = attempt,
        started_at = codex_started_at,
        exec_ref = exec_ref,
        finished_at = now(),
        outcome = "completed",
      }
    end

    local detail = tostring(result.stdout or "")
    if detail == "" then
      detail = tostring(result.stderr or "")
    end
    core.log_codex_result("implement", ready.proposal_id, "implement", result, nil, "no-changes", {
      queue = event_queue,
      source_ref = ready.source_ref,
      terminal = false,
    })
    return {
      kind = "impl-failed",
      ready = ready,
      reason = "no-changes",
      detail = detail,
      attempt = attempt,
      started_at = codex_started_at,
      exec_ref = exec_ref,
      finished_at = now(),
      base_sha = base_head,
      outcome = "failed: no-changes",
    }
  end

  local add_result = core.git_add_all(worktree, 30)
  if add_result.exit_code ~= 0 then
    error("github-devloop: git add failed: " .. tostring(add_result.stderr))
  end

  local commit_result = core.git_commit(worktree, payloads_builders.implement_commit_subject(core,
      issue_number,
      require("devloop.github_proxy_entity_view").commit_issue_subject_snapshot(core, repo, issue_number)
    ), 60)
  if commit_result.exit_code ~= 0 then
    error("github-devloop: git commit failed: " .. tostring(commit_result.stderr))
  end

  local branch_result = core.git_current_branch(worktree, 30)
  if branch_result.exit_code ~= 0 then
    error("github-devloop: git branch fact failed: " .. tostring(branch_result.stderr))
  end
  local actual_branch = tostring(branch_result.stdout or ""):gsub("%s+$", "")
  if actual_branch ~= branch then
    error("github-devloop: deterministic implementing branch mismatch")
  end
  if not require("devloop.pr_safety").is_safe_branch(branch) then
    error("github-devloop: unsafe implementing branch")
  end

  local head_result = git("github-devloop").git_head_sha(worktree, 30)
  if head_result.exit_code ~= 0 then
    error("github-devloop: git head fact failed: " .. tostring(head_result.stderr))
  end
  local head_sha = tostring(head_result.stdout or ""):gsub("%s+$", "")
  if not require("devloop.pr_safety").is_safe_head_sha(head_sha) then
    error("github-devloop: unsafe implementing head_sha")
  end

  return {
    kind = "implementing",
    ready = ready,
    worktree = worktree,
    branch = branch,
    head_sha = head_sha,
    base_branch = branches.integration,
    base_sha = base_head,
    attempt = attempt,
    started_at = codex_started_at,
    exec_ref = exec_ref,
    finished_at = now(),
    outcome = "completed",
  }
end

local function raise_attempt_outcome(repo, issue_number, outcome)
  if outcome == nil then
    return
  end
  raise_implement_attempt(repo, issue_number, outcome.ready, outcome.attempt, outcome.started_at, outcome.exec_ref)
  if outcome.kind == "implementing" then
    publish_implementation_branch(repo, issue_number, outcome.ready, outcome.worktree, outcome.branch)
    raise_implementing(
      repo,
      issue_number,
      outcome.ready,
      outcome.worktree,
      outcome.branch,
      outcome.head_sha,
      outcome.base_branch,
      outcome.base_sha,
      outcome.attempt,
      outcome.started_at,
      outcome.exec_ref
    )
    pr_child_handoff.raise_awaiting_pr_from_fact(
      "implement",
      repo,
      issue_number,
      outcome.ready,
      { title = nil, comments = {} },
      {
        branch = outcome.branch,
        head_sha = outcome.head_sha,
        base_branch = outcome.base_branch,
      },
      "implementation output published; waiting for visible delegated PR child"
    )
    return
  end
  if outcome.kind == "impl-failed" then
    raise_impl_failed(repo, issue_number, outcome.ready, outcome.reason, outcome.detail, outcome.attempt)
    return
  end
  error("github-devloop: unknown implementation outcome")
end

local function recheck_implementation_write_gate(repo, issue_number, marker_ready, expected_from_states, accepted_ready_hand_off, allow_same_version_implementing)
  local view = core.gh_issue_view_implement(repo, issue_number, 30)
  if view.exit_code ~= 0 then
    error("github-devloop: gh issue implement recheck failed: " .. tostring(view.stderr))
  end
  local current = parsers_issue.parse_issue_view_implement(core, view.stdout)
  core.log_forged_markers("implement", marker_ready.proposal_id, current.comments)
  local state = core.current_state(current.comments, marker_ready.proposal_id)
  if state.state == "implementing"
    and tostring(state.version or "") == tostring(marker_ready.dedup_key or "") then
    local link = m_facts.pr_link_fact(core, current.comments, marker_ready.proposal_id)
    if link ~= nil and tostring(link.impl_version or "") == tostring(marker_ready.dedup_key) then
      handoff_existing_pr_link(repo, issue_number, marker_ready, current, link, "linked PR fact is already visible")
      return false
    end
    local fact = m_facts.implementing_fact(core, current.comments, marker_ready.proposal_id, marker_ready.dedup_key)
    if fact ~= nil then
      core.log_cas_decision("implement", marker_ready.proposal_id, state, "implementing", "implementing", "skip-idempotent(implementation marker already visible)", "implementation fact marker already visible")
      return false
    end
    if not transitions.expected_states_include(expected_from_states, "implementing") and not allow_same_version_implementing then
      core.log_cas_decision("implement", marker_ready.proposal_id, state, "ready", "implementing", "skip-idempotent(already at to_state)", "implementation state marker already visible")
      return false
    end
    return true
  end
  if state.state == "impl-failed" and tostring(state.version or "") == tostring(marker_ready.dedup_key or "") then
    core.log_cas_decision("implement", marker_ready.proposal_id, state, "implementing", "impl-failed", "skip-idempotent(already failed)", "implementation failure marker already visible")
    return false
  end
  for _, expected in ipairs(expected_from_states or {}) do
    if transitions.expected_state_matches(state, expected) then
      return true
    end
  end
  local transition = transitions.implementation_transition_status(state, expected_from_states or { "ready" }, marker_ready.dedup_key)
  if transition ~= "apply" then
    if transition == "pending" and payloads_predicates.is_ready_hand_off(core, accepted_ready_hand_off, marker_ready) then
      core.log_cas_decision("implement", marker_ready.proposal_id, {
        state = "ready",
        version = marker_ready.dedup_key,
        stage_rank = core.stage_rank("ready"),
      }, "ready", "implementing", "apply(own-ready-hand-off)", "write-time ready hand-off still matches this generation")
      return true
    end
    core.log_cas_decision("implement", marker_ready.proposal_id, state, "ready", "implementing", core.cas_outcome(state, transition, marker_ready.dedup_key), "write-time issue state changed")
    return false
  end
  return true
end

local function precheck_implementation_write_gate(repo, issue_number, marker_ready, expected_from_states, accepted_ready_hand_off)
  local view = core.gh_issue_view_implement(repo, issue_number, 30)
  if view.exit_code ~= 0 then
    error("github-devloop: gh issue implement recheck failed: " .. tostring(view.stderr))
  end
  local current = parsers_issue.parse_issue_view_implement(core, view.stdout)
  core.log_forged_markers("implement", marker_ready.proposal_id, current.comments)
  local state = core.current_state(current.comments, marker_ready.proposal_id)
  if state.state == "implementing"
    and tostring(state.version or "") == tostring(marker_ready.dedup_key or "") then
    local link = m_facts.pr_link_fact(core, current.comments, marker_ready.proposal_id)
    if link ~= nil and tostring(link.impl_version or "") == tostring(marker_ready.dedup_key) then
      handoff_existing_pr_link(repo, issue_number, marker_ready, current, link, "linked PR fact is already visible")
      return false
    end
    if not transitions.expected_states_include(expected_from_states, "implementing") then
      core.log_cas_decision("implement", marker_ready.proposal_id, state, "ready", "implementing", "skip-idempotent(already at to_state)", "implementation state marker already visible")
      return false
    end
    return true
  end
  if state.state == "impl-failed" and tostring(state.version or "") == tostring(marker_ready.dedup_key or "") then
    core.log_cas_decision("implement", marker_ready.proposal_id, state, "implementing", "impl-failed", "skip-idempotent(already failed)", "implementation failure marker already visible")
    return false
  end
  for _, expected in ipairs(expected_from_states or {}) do
    if transitions.expected_state_matches(state, expected) then
      return true
    end
  end
  local transition = transitions.implementation_transition_status(state, expected_from_states or { "ready" }, marker_ready.dedup_key)
  if transition ~= "apply" then
    if transition == "pending" and payloads_predicates.is_ready_hand_off(core, accepted_ready_hand_off, marker_ready) then
      core.log_cas_decision("implement", marker_ready.proposal_id, {
        state = "ready",
        version = marker_ready.dedup_key,
        stage_rank = core.stage_rank("ready"),
      }, "ready", "implementing", "apply(own-ready-hand-off)", "pre-spawn ready hand-off still matches this generation")
      return true
    end
    core.log_cas_decision("implement", marker_ready.proposal_id, state, "ready", "implementing", core.cas_outcome(state, transition, marker_ready.dedup_key), "pre-spawn issue state changed")
    return false
  end
  return true
end

local function backing_original(current, managed)
  local origin = forks.fork_origin_fact(core, current, managed)
  if origin == nil then
    return nil, nil
  end
  return origin, forks.rederive_issue_state(core, origin.repo, origin.issue_number)
end

local function operator_blocked_reimplement_allowed(ready, current, state)
  local reentry = ready and ready.operator_reentry
  if type(reentry) ~= "table"
    or reentry.command ~= "reimplement"
    or reentry.from_state ~= "blocked"
    or state.state ~= "blocked"
    or tostring(state.version or "") ~= tostring(reentry.state_version or "") then
    return false
  end
  local link = m_facts.pr_link_fact(core, current.comments, ready.proposal_id)
  return link ~= nil
    and tonumber(link.pr_number) == tonumber(reentry.pr_number)
    and tostring(link.impl_version or "") == tostring(reentry.impl_version or "")
end

local function process_ready_event(event)
  local ready = event.payload or {}
  if not v_ready.is_supported_ready(core, ready) then
    core.log_entry("implement", event, "unknown", core.payload_field(ready, "dedup_key"))
    core.log_cas_decision("implement", "unknown", { state = nil, version = nil }, "ready", "implementing", "skip-foreign(proposal_id)", "unsupported event payload")
    return
  end

  core.log_entry("implement", event, ready.proposal_id, ready.dedup_key)
  local repo, issue_number = base_ids.parse_proposal_id(ready.proposal_id)
  if repo == nil then
    core.log_cas_decision("implement", ready.proposal_id, { state = nil, version = nil }, "ready", "implementing", "skip-foreign(proposal_id)", "proposal_id is outside github-devloop")
    return
  end

  local lock_key = entity_lib.implement_lock_key(ready.proposal_id)
  if lock_key == nil then
    core.log_cas_decision("implement", ready.proposal_id, { state = nil, version = nil }, "ready", "implementing", "skip-foreign(proposal_id)", "no transition lock key")
    return
  end

  local attempt_plan = nil
  with_lock(lock_key, function()
    devloop_base.assert_trusted_bot_configured()

    local view = core.gh_issue_view_implement(repo, issue_number, 30)
    if view.exit_code ~= 0 then
      error("github-devloop: gh issue implement view failed: " .. tostring(view.stderr))
    end

    local current = parsers_issue.parse_issue_view_implement(core, view.stdout)
    current.repo = repo
    current.number = issue_number
    local managed = m_claims.managed_bot_logins(core)
    core.log_forged_markers("implement", ready.proposal_id, current.comments)
    if tostring(current.state or ""):upper() ~= "OPEN" then
      core.log_cas_decision("implement", ready.proposal_id, { state = nil, version = ready.dedup_key }, "ready", "implementing", "skip-stale(original-closed)", "current issue is not open")
      return
    end
    if slice_gate.check(repo, issue_number, ready, current) then
      return
    end
    local origin, original = backing_original(current, managed)
    if original ~= nil and tostring(original.state or ""):upper() ~= "OPEN" then
      core.log_cas_decision("implement", ready.proposal_id, { state = nil, version = ready.dedup_key }, "ready", "implementing", "skip-stale(original-closed)", "fork backing issue is closed: " .. tostring(origin.repo) .. "#" .. tostring(origin.issue_number))
      return
    end
    if fork_gate.check(repo, issue_number, ready, origin, original, managed) then
      return
    end
    local state = core.current_state(current.comments, ready.proposal_id)
    local gate = core.dependency_gate(repo, issue_number, {
      proposal_id = ready.proposal_id,
      version = core.ready_payload_inner_version(ready.dedup_key),
      comments = current.comments,
    })
    if not gate.ok then
      local inner_ready_version = core.ready_payload_inner_version(ready.dedup_key)
      local dep_version = core.ready_split_version(inner_ready_version)
      core.log_cas_decision("implement", ready.proposal_id, state, "ready", "dependency_wait", "hold-dependency-backstop", gate.reason)
      core.log_apply("implement", ready.proposal_id, "dependency_wait", dep_version, { add = { core._blocked_on_dependency_label }, remove = {} }, {
        "github-proxy.github_issue_comment_request",
        "github-proxy.github_issue_label_request",
      })
      core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request", core.build_ready_split_canonicalized_comment_request(
        repo,
        issue_number,
        ready.proposal_id,
        inner_ready_version,
        "dependency_wait",
        dep_version,
        gate,
        ready.source_ref
      ))
      core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_label_request", requests_labels.build_label_request(core,
        repo,
        issue_number,
        { core._blocked_on_dependency_label },
        {},
        base_ids.dedup_key({ "dependency", "label", "hold", tostring(ready.proposal_id), tostring(dep_version), tostring(gate.kind) }),
        ready.source_ref
      ))
      return
    end

    local branches = config.branch_config(core)
    local implementation_version = core.implementation_attempt_version(ready.dedup_key, ready.impl_retry_attempt)
    local branch_version = core.implementation_base_version(ready.dedup_key)
    local marker_ready = ready_for_implementation_version(ready, implementation_version)
    local branch = devloop_base.implement_branch(repo, issue_number, branch_version)

    if state.state == "implementing" then
      if tostring(state.version or "") ~= tostring(marker_ready.dedup_key or "") then
        if not implementing_mismatch_is_durable(current, ready.proposal_id, state) then
          core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "skip-stale(version-mismatch)", "implementing state marker has no durable progress fact")
          return
        end
        handle_implementing_version_mismatch(repo, issue_number, current, ready, state, marker_ready.dedup_key)
        return
      end
      local link = m_facts.pr_link_fact(core, current.comments, ready.proposal_id)
      if link ~= nil and tostring(link.impl_version or "") == tostring(marker_ready.dedup_key) then
        handoff_existing_pr_link(repo, issue_number, marker_ready, current, link, "linked PR fact is already visible")
        return
      end
      local fact = m_facts.implementing_fact(core, current.comments, ready.proposal_id, marker_ready.dedup_key)
      if fact == nil and live_implement_attempt_visible(current.comments, ready.proposal_id, marker_ready.dedup_key) then
        core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "skip-idempotent(already at to_state)", "implementation attempt heartbeat is still live")
        return
      end
      local progress = nil
      if fact ~= nil then
        progress = remote_branch_fact(fact.branch, fact.base_branch, fact)
      else
        progress = remote_branch_fact(branch, branches.integration, {
          proposal_id = ready.proposal_id,
          dedup_key = marker_ready.dedup_key,
        })
      end
      if progress ~= nil then
        progress.proposal_id = ready.proposal_id
        progress.dedup_key = marker_ready.dedup_key
        pr_child_handoff.raise_awaiting_pr_from_fact("implement", repo, issue_number, marker_ready, current, progress, "implementing remote branch progress is visible")
        return
      end
      local base_head = worktree_lifecycle.prepare_base(branches)
      local local_progress = local_branch_fact(base_head, branch, branches.integration, marker_ready.dedup_key)
      if local_progress ~= nil then
        local_progress.proposal_id = ready.proposal_id
        pr_child_handoff.raise_awaiting_pr_from_fact("implement", repo, issue_number, marker_ready, current, local_progress, "local implementation branch progress is visible")
        return
      end
      local attempts = core.implement_attempt_count(current.comments, ready.proposal_id, marker_ready.dedup_key)
      if attempts >= MAX_IMPLEMENT_ATTEMPTS then
        core.log_cas_decision("implement", ready.proposal_id, state, "implementing", "impl-failed", "applied(attempts-exhausted)", "implementation attempts exhausted with no PR or branch progress")
        raise_impl_failed(repo, issue_number, marker_ready, "retry-exhausted", "No linked PR, remote branch, or local branch progress was visible after " .. tostring(attempts) .. " attempts.", attempts)
        return
      end
      core.log_cas_decision("implement", ready.proposal_id, state, "implementing", "implementing", "applied(retry-no-progress)", "no PR or branch progress is visible; retrying implementation attempt")
      attempt_plan = {
        marker_ready = marker_ready,
        current = current,
        branches = branches,
        branch = branch,
        base_head = base_head,
        attempt = attempts + 1,
        expected_from_states = { "implementing" },
      }
      return
    end

    local retry_failure = nil
    local blocked_reentry = false
    if state.state == "impl-failed" and ready.impl_retry_attempt ~= nil and state.version == ready.dedup_key then
      retry_failure = core.impl_failure_fact(current.comments, ready.proposal_id, ready.dedup_key)
      if retry_failure ~= nil and tonumber(ready.impl_retry_attempt) <= tonumber(retry_failure.attempt or 1) then
        core.log_cas_decision("implement", ready.proposal_id, state, "impl-failed", "implementing", "skip-idempotent(retry-not-advanced)", "implementation retry event does not advance the failure attempt")
        return
      end
    elseif state.state == "blocked" and ready.impl_retry_attempt ~= nil and operator_blocked_reimplement_allowed(ready, current, state) then
      blocked_reentry = true
    elseif state.state == "implementing" or state.state == "impl-failed" then
      core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "skip-idempotent(already at to_state)", "implementation fact marker already visible")
      return
    end
    local expected_states = blocked_reentry
      and { { state = "blocked", version = ready.operator_reentry.state_version, target_version = ready.dedup_key } }
      or (retry_failure ~= nil and { "impl-failed" } or { "ready" })
    local transition = transitions.implementation_transition_status(state, expected_states, ready.dedup_key)
    if transition == "idempotent" or transition == "stale" then
      core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", core.cas_outcome(state, transition, ready.dedup_key), "ready event cannot advance current marker")
      return
    end
    local accepted_ready_hand_off = nil
    if transition == "pending" then
      local verified_state = nil
      local hand_off_reason = "missing"
      if ready.ready_hand_off ~= nil then
        verified_state, hand_off_reason = payloads_predicates.verified_hand_off_state(core, repo, ready.ready_hand_off, {
          proposal_id = ready.proposal_id,
          state = "ready",
          marker_version = ready.ready_hand_off.marker_version,
          event_version = ready.dedup_key,
        })
      end
      if retry_failure == nil and ready.impl_retry_attempt == nil and verified_state ~= nil then
        state = verified_state
        accepted_ready_hand_off = ready.ready_hand_off
        core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "apply(verified-own-ready-hand-off)", "ready marker comment verified by direct id lookup")
      else
        core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", core.cas_outcome(state, transition, ready.dedup_key), "ready state marker not yet visible")
        if ready.ready_hand_off ~= nil then
          core.log_line("info", "implement", ready.proposal_id, "HANDOFF", {
            "state=ready",
            "outcome=verify-failed",
            "reason=" .. tostring(hand_off_reason),
          })
        end
        error("github-devloop: ready state marker not yet visible for implement; retrying")
      end
    else
      core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", core.cas_outcome(state, transition, ready.dedup_key), "ready marker visible; attempting implementation")
    end

    local wip_ok, wip_reason, wip_count, wip_max = m_mq.wip_capacity_allows_start(core, repo, issue_number)
    if not wip_ok then
      core.log_cas_decision("implement", ready.proposal_id, state, "ready", "implementing", "hold-wip-cap", wip_reason .. ": " .. tostring(wip_count) .. "/" .. tostring(wip_max))
      return
    end

    local issue_slug = devloop_base.safe_issue_slug(repo, issue_number)
    core.log_line("info", "implement", ready.proposal_id, "IMPLEMENT", {
      "issue_slug=" .. tostring(issue_slug),
      "branch=" .. tostring(branch),
      "reason=implementation fact marker absent for this version",
    })

    attempt_plan = {
      marker_ready = marker_ready,
      current = current,
      branches = branches,
      branch = branch,
      attempt = ready.impl_retry_attempt or 1,
      expected_from_states = expected_states,
      accepted_ready_hand_off = accepted_ready_hand_off,
    }
  end)
  if attempt_plan == nil then
    return
  end

  local worktree, codex_started_at, exec_ref
  with_lock(lock_key, function()
    if precheck_implementation_write_gate(
      repo,
      issue_number,
      attempt_plan.marker_ready,
      attempt_plan.expected_from_states,
      attempt_plan.accepted_ready_hand_off
    ) then
      if dispatch_live_run.dispatch_live_run_dedup(core, "implement", attempt_plan.marker_ready.proposal_id, attempt_plan.marker_ready.dedup_key) then
        core.log_cas_decision(
          "implement",
          attempt_plan.marker_ready.proposal_id,
          { state = "ready", version = attempt_plan.marker_ready.dedup_key, stage_rank = core.stage_rank("ready") },
          "ready",
          "implementing",
          "skip-idempotent(live-exec-ref)",
          "matching implementation codex run is still live"
        )
        return
      end
      if attempt_plan.base_head == nil then
        attempt_plan.base_head = worktree_lifecycle.prepare_base(attempt_plan.branches)
      end
      worktree, codex_started_at, exec_ref = prepare_attempt(
        repo,
        issue_number,
        attempt_plan.marker_ready,
        attempt_plan.branches,
        attempt_plan.branch,
        attempt_plan.base_head,
        attempt_plan.attempt
      )
    end
  end)
  if worktree == nil then
    return
  end

  local outcome = run_attempt(
    repo,
    issue_number,
    attempt_plan.marker_ready,
    attempt_plan.current,
    attempt_plan.branches,
    attempt_plan.branch,
    attempt_plan.base_head,
    worktree,
    codex_started_at,
    exec_ref,
    attempt_plan.attempt,
    event.ts,
    event.queue
  )
  with_lock(lock_key, function()
    if recheck_implementation_write_gate(repo, issue_number, attempt_plan.marker_ready, attempt_plan.expected_from_states, attempt_plan.accepted_ready_hand_off, true) then
      raise_attempt_outcome(repo, issue_number, outcome)
    end
  end)
end

local function act_implement(event)
  queue.dispatch_consumed_queue("implement", spec, event, {
    devloop_ready = process_ready_event,
  })
end

return saga.department(spec, {
  done = implement_done,
  act = act_implement,
  wrap = core.wrap_pipeline_failure,
  name = "implement",
})
