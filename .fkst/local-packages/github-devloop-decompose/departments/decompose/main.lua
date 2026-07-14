local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local parsers_pr = require("devloop.parsers.pr")
local parsers_issue = require("devloop.parsers.issue")
local core = require("core")
local saga = require("workflow.saga")
local strings = require("contract.strings")
local context_bundle = require("devloop.context_bundle")
local decompose_lib = require("devloop.decompose")
local config = require("devloop.config")
local conv_reconcile = require("devloop.convergence.reconcile")
local conv_attempts = require("devloop.convergence.attempts")
local devloop_entity_view = require("devloop.github_proxy_entity_view")
local workflow_codex = require("workflow.codex")

local spec = {
  consumes = { "devloop_decompose" }, published_seam = { "devloop_decompose" },
  produces = { "github-proxy.github_issue_create_request", "github-proxy.github_pr_comment_request" },
  stall_window = "2m",
  retry = { max_attempts = 2, base = "5s", cap = "10s" },
}

local MAX_RUNTIME_ID_LEN = 180
local context_cache = setmetatable({}, { __mode = "k" })

local function marker_body_file(repo, pr_number)
  local id = "decompose-" .. strings.runtime_safe_segment(repo) .. "-pr-" .. strings.runtime_safe_segment(pr_number)
  if #id > MAX_RUNTIME_ID_LEN then
    id = id:sub(1, MAX_RUNTIME_ID_LEN)
  end
  return "/tmp/fkst-github-devloop-" .. id .. ".md"
end

local function parse_failure_key(decompose)
  return base_ids.dedup_key({
    "github-devloop",
    "decompose-parse-failure",
    tostring(decompose.proposal_id),
    tostring(decompose.version),
  })
end

local function decompose_plan(decompose, current_issue, content_fetch)
  local prompt = core.build_decompose_prompt(decompose, current_issue, content_fetch)
  local result = spawn_codex_sync(workflow_codex.judgment_codex_opts(
    prompt,
    devloop_base.judgment_worktree_with_exec(exec_sync, "decompose", decompose.dedup_key)
  ))
  if type(result) == "table" and result.exit_code ~= nil and result.exit_code ~= 0 then
    error("github-devloop: decompose codex failed: " .. tostring(result.stderr or ""))
  end
  local stdout = type(result) == "table" and result.stdout or result
  local issues = core.parse_decompose_plan(stdout)
  if issues ~= nil then
    return issues
  end

  local key = parse_failure_key(decompose)
  local previous = tonumber(cache_get(key) or "0") or 0
  cache_set(key, tostring(previous + 1))
  if previous < 1 then
    error("github-devloop: decompose JSON parse failed; retrying")
  end
  return core.fallback_decompose_plan(decompose)
end

local function read_current_pr(repo, pr_number)
  local pr_view = core.gh_pr_view_origin(repo, pr_number, 30)
  if pr_view.exit_code ~= 0 then
    error("github-devloop: gh pr decompose view failed: " .. tostring(pr_view.stderr))
  end
  return parsers_pr.parse_pr_view_origin(core, pr_view.stdout)
end

local function read_decompose_issue(repo, issue_number)
  local issue_view = core.gh_issue_view_decompose(repo, issue_number, 30)
  if issue_view.exit_code ~= 0 then
    error("github-devloop: gh issue decompose view failed: " .. tostring(issue_view.stderr))
  end
  return parsers_issue.parse_issue_view_decompose(core, issue_view.stdout)
end

local function read_decompose_child_issues(repo, proposal_id)
  local child_list = core.gh_issue_list_decompose_children(repo, proposal_id, 30)
  if child_list.exit_code ~= 0 then
    error("github-devloop: gh issue decompose child list failed: " .. tostring(child_list.stderr))
  end
  return decompose_lib.parse_decompose_child_issue_list(core, child_list.stdout)
end

local function plan_current_decompose(event, repo, issue_number, decompose)
  local current_issue = read_decompose_issue(repo, issue_number)
  local depth = decompose_lib.decompose_lineage_depth(core, current_issue.body)
  if depth >= decompose_lib.max_decompose_depth(core) then
    return current_issue, nil, "depth-cap"
  end
  decompose.current_issue_body = current_issue.body
  local content_fetch = context_bundle.context_fetch_from_bundle(core, {
    dept = "decompose",
    repo = repo,
    issue_number = issue_number,
    pr_number = decompose.pr_number,
    proposal_id = decompose.proposal_id,
    version = decompose.dedup_key,
    tick = event.ts,
  })
  local issues = decompose_plan(decompose, current_issue, content_fetch)
  if #issues < 1 then
    issues = core.fallback_decompose_plan(decompose)
  end
  return current_issue, issues, nil
end

local function raise_issue_create(repo, decompose, issue, index)
  local create_request = core.build_issue_create_request(repo, decompose, issue, index)
  core.log_raise("decompose", decompose.proposal_id, "github-proxy.github_issue_create_request", create_request)
end

local function child_completion_check(child_issues, decompose, index)
  return function()
    local completed = decompose_lib.decompose_child_issue_fact_indexes(core,
      child_issues,
      decompose.proposal_id,
      decompose.version,
      decompose.pr_number
    )
    return completed[index] == true
  end
end

local function all_children_complete(child_issues, decompose, count)
  local completed = decompose_lib.decompose_child_issue_fact_indexes(core,
    child_issues,
    decompose.proposal_id,
    decompose.version,
    decompose.pr_number
  )
  for index = 1, count do
    if completed[index] ~= true then
      return false
    end
  end
  return true
end

local function effect_id_for_create(repo, decompose, issue, index)
  return core.build_issue_create_request(repo, decompose, issue, index).dedup_key
end

local function perform_issue_create(repo, decompose, issue, index)
  return function()
    raise_issue_create(repo, decompose, issue, index)
  end
end

local function heal_missing_children(event, repo, issue_number, decompose, state, decomposed)
  local child_issues = read_decompose_child_issues(repo, decompose.proposal_id)
  local completed = decompose_lib.decompose_child_issue_fact_indexes(core,
    child_issues,
    decompose.proposal_id,
    decompose.version,
    decompose.pr_number
  )
  local child_body_missing = {}
  for index = 1, decomposed.count do
    if not completed[index] then
      table.insert(child_body_missing, index)
    end
  end
  if #child_body_missing == 0 then
    core.log_cas_decision("decompose", decompose.proposal_id, state, "blocked", "decomposed", "skip-idempotent(decomposed marker and children already visible)", "decompose already applied")
    return
  end

  local _, issues, reason = plan_current_decompose(event, repo, issue_number, decompose)
  if reason == "depth-cap" then
    core.log_cas_decision("decompose", decompose.proposal_id, state, "blocked", "decomposed", "retry-pending(decomposed children missing)", "decomposed marker is visible but child issues are missing")
    error("github-devloop: decomposed marker visible but child issues are missing")
  end
  local count = math.min(#issues, decomposed.count)
  if count < decomposed.count then
    local fallback = core.fallback_decompose_plan(decompose)
    for index = count + 1, decomposed.count do
      issues[index] = fallback[1]
    end
  end

  completed = decompose_lib.decompose_child_issue_fact_indexes(core, child_issues, decompose.proposal_id, decompose.version, decompose.pr_number)
  local missing = {}
  for index = 1, decomposed.count do
    if not completed[index] then
      table.insert(missing, index)
    end
  end
  if #missing == 0 then
    core.log_cas_decision("decompose", decompose.proposal_id, state, "blocked", "decomposed", "skip-idempotent(decomposed marker and children already visible)", "decompose already applied")
    return
  end

  core.log_apply("decompose", decompose.proposal_id, nil, nil, { add = {}, remove = {} }, {
    "github-proxy.github_issue_create_request",
  })
  for _, index in ipairs(missing) do
    core.effect_once({
      effect_id = effect_id_for_create(repo, decompose, issues[index], index),
      completion_check = child_completion_check(child_issues, decompose, index),
      perform = perform_issue_create(repo, decompose, issues[index], index),
    })
  end
end

local function write_decomposed_marker(repo, decompose, count)
  local path = marker_body_file(repo, decompose.pr_number)
  local body = core.with_github_debug_stamp(core.decomposed_comment_body(decompose, count), {
    emitter = "github-devloop.decompose",
    target = "pr:" .. tostring(repo) .. "#" .. tostring(decompose.pr_number),
    dedup_key = decompose.dedup_key,
    context = decompose.proposal_id,
  })
  file.write(path, body)
  local result = core.gh_pr_comment(repo, decompose.pr_number, path, 30)
  if result.exit_code ~= 0 then
    error("github-devloop: gh pr decomposed marker comment failed: " .. tostring(result.stderr))
  end
  devloop_entity_view.invalidate_entity_after_write(repo, "pr", decompose.pr_number)

  local confirmed_pr = read_current_pr(repo, decompose.pr_number)
  if not decompose_lib.has_decomposed_marker(core, confirmed_pr.comments, decompose.proposal_id, decompose.version, decompose.pr_number) then
    error("github-devloop: decomposed marker not yet visible after write; retrying")
  end
  return confirmed_pr
end

local function decompose_context(event)
  if type(event) == "table" and context_cache[event] ~= nil then
    if context_cache[event] == false then
      return nil
    end
    return context_cache[event]
  end
  local decompose = event.payload or {}
  if not decompose_lib.is_supported_decompose(core, decompose) then
    core.log_entry("decompose", event, "unknown", core.payload_field(decompose, "dedup_key"))
    core.log_cas_decision("decompose", "unknown", { state = nil, version = nil }, "blocked", "decomposed", "skip-foreign(payload)", "unsupported event payload")
    if type(event) == "table" then
      context_cache[event] = false
    end
    return nil
  end

  core.log_entry("decompose", event, decompose.proposal_id, decompose.dedup_key)
  local entity = entity_lib.parse_entity_proposal_id(decompose.proposal_id)
  if entity == nil or entity.issue_number == nil then
    core.log_cas_decision("decompose", decompose.proposal_id, { state = nil, version = nil }, "blocked", "decomposed", "skip-foreign(proposal_id)", "proposal_id is outside issue-backed github-devloop")
    if type(event) == "table" then
      context_cache[event] = false
    end
    return nil
  end
  local repo = entity.repo
  local issue_number = entity.issue_number
  local _, pr_number = devloop_base.parse_pr_source_ref(decompose.source_ref)
  if tostring(pr_number or "") ~= tostring(decompose.pr_number) then
    core.log_cas_decision("decompose", decompose.proposal_id, { state = nil, version = nil }, "blocked", "decomposed", "skip-foreign(source_ref)", "source_ref PR does not match decompose payload")
    if type(event) == "table" then
      context_cache[event] = false
    end
    return nil
  end
  if not m_claims.verify_pr_review_issue_claim(core, "decompose", repo, issue_number, nil, decompose.proposal_id) then
    if type(event) == "table" then
      context_cache[event] = false
    end
    return nil
  end

  local lock_key = entity_lib.transition_lock_key(decompose.proposal_id)
  if lock_key == nil then
    core.log_cas_decision("decompose", decompose.proposal_id, { state = nil, version = nil }, "blocked", "decomposed", "skip-foreign(proposal_id)", "no transition lock key")
    if type(event) == "table" then
      context_cache[event] = false
    end
    return nil
  end

  local context = {
    decompose = decompose,
    repo = repo,
    issue_number = issue_number,
    lock_key = lock_key,
  }
  if type(event) == "table" then
    context_cache[event] = context
  end
  return context
end

local function accepted_decompose(event)
  local context = decompose_context(event)
  if context == nil and type(event) == "table" then
    context_cache[event] = nil
  end
  return context ~= nil
end

local function decomposed_done(event)
  local context = decompose_context(event)
  if context == nil then
    if type(event) == "table" then
      context_cache[event] = nil
    end
    return false
  end
  local done = false
  with_lock(context.lock_key, function()
    devloop_base.assert_trusted_bot_configured()
    local current_pr = read_current_pr(context.repo, context.decompose.pr_number)
    core.log_forged_markers("decompose",
      context.decompose.proposal_id,
      current_pr.comments)
    local state = require("devloop.entity").current_entity_state(core, current_pr.comments, context.decompose.proposal_id)
    if not conv_reconcile.has_fix_reconcile_marker(core, current_pr.comments, context.decompose.proposal_id, context.decompose.version)
      or state.state ~= "blocked"
      or tostring(state.version or "") ~= tostring(context.decompose.version) then
      return
    end
    local decomposed = decompose_lib.decomposed_fact(core, current_pr.comments, context.decompose.proposal_id, context.decompose.version, context.decompose.pr_number)
    if decomposed == nil then
      if conv_attempts.has_decompose_exhausted_marker(core, current_pr.comments, context.decompose.proposal_id, context.decompose.version) then
        core.log_cas_decision("decompose", context.decompose.proposal_id, state, "blocked", "decomposed",
          "skip-idempotent(decompose-exhausted)", "blocked decompose output obligation already reached terminal stop")
        done = true
      end
      return
    end
    local child_issues = read_decompose_child_issues(context.repo, context.decompose.proposal_id)
    if not all_children_complete(child_issues, context.decompose, decomposed.count) then
      return
    end
    core.log_cas_decision("decompose", context.decompose.proposal_id, state, "blocked", "decomposed", "skip-idempotent(decomposed marker and children already visible)", "decompose already applied")
    done = true
  end)
  if done and type(event) == "table" then
    context_cache[event] = nil
  end
  return done
end

local function act_decompose(event)
  local context = decompose_context(event)
  if context == nil then
    return
  end
  if type(event) == "table" then
    context_cache[event] = nil
  end
  local decompose = context.decompose
  local repo = context.repo
  local issue_number = context.issue_number
  with_lock(context.lock_key, function()
    devloop_base.assert_trusted_bot_configured()

    local current_pr = read_current_pr(repo, decompose.pr_number)
    core.log_forged_markers("decompose", decompose.proposal_id, current_pr.comments)

    local state = require("devloop.entity").current_entity_state(core, current_pr.comments, decompose.proposal_id)
    if not conv_reconcile.has_fix_reconcile_marker(core, current_pr.comments, decompose.proposal_id, decompose.version)
      or state.state ~= "blocked"
      or tostring(state.version or "") ~= tostring(decompose.version) then
      core.log_cas_decision("decompose", decompose.proposal_id, state, "blocked", "decomposed", "retry-pending(blocked-fix-reconcile-not-visible)", "blocked/fix-reconcile marker is not yet visible")
      error("github-devloop: blocked fix reconcile marker not yet visible for decompose; retrying")
    end
    local decomposed = decompose_lib.decomposed_fact(core,
      current_pr.comments,
      decompose.proposal_id,
      decompose.version,
      decompose.pr_number
    )
    if decomposed ~= nil then
      heal_missing_children(event, repo, issue_number, decompose, state, decomposed)
      return
    end

    local current_issue, issues, reason = plan_current_decompose(event, repo, issue_number, decompose)
    local depth = decompose_lib.decompose_lineage_depth(core, current_issue.body)
    if reason == "depth-cap" or depth >= decompose_lib.max_decompose_depth(core) then
      core.log_cas_decision("decompose", decompose.proposal_id, state, "blocked", "decomposed", "applied(decompose-exhausted:depth-cap)", "decompose lineage depth cap reached")
      core.log_apply("decompose", decompose.proposal_id, nil, nil, { add = {}, remove = {} }, {
        "github-proxy.github_pr_comment_request",
      })
      core.log_raise("decompose", decompose.proposal_id, "github-proxy.github_pr_comment_request", conv_attempts.build_decompose_exhausted_comment_request(core,
        { kind = "pr", repo = repo, number = decompose.pr_number },
        decompose.proposal_id,
        state,
        decompose.source_ref,
        1
      ))
      return
    end
    local count = math.min(#issues, decompose_lib.max_decompose_issues(core))
    if count < 1 then
      issues = core.fallback_decompose_plan(decompose)
      count = 1
    end

    if config.write_mode(core) ~= "real" then
      core.log_cas_decision("decompose", decompose.proposal_id, state, "blocked", "decomposed", "dry-run(marker-write-required)", "FKST_GITHUB_WRITE=1 is required before issue create requests")
      return
    end
    write_decomposed_marker(repo, decompose, count)
    local child_issues = read_decompose_child_issues(repo, decompose.proposal_id)

    core.log_apply("decompose", decompose.proposal_id, nil, nil, { add = {}, remove = {} }, {
      "github-proxy.github_issue_create_request",
    })
    for index = 1, count do
      core.effect_once({
        effect_id = effect_id_for_create(repo, decompose, issues[index], index),
        completion_check = child_completion_check(child_issues, decompose, index),
        perform = perform_issue_create(repo, decompose, issues[index], index),
      })
    end
  end)
end

return saga.department(spec, {
  accept = accepted_decompose,
  done = decomposed_done,
  act = act_decompose,
  wrap = core.wrap_pipeline_failure,
  name = "decompose",
})
