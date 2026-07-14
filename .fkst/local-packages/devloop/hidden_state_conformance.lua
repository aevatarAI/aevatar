local devloop_base = require("devloop.base")
local convergence_shared, poll_fakes = require("devloop.convergence.shared"), require("devloop.hidden_state_conformance.poll_fakes")
local contract_time = require("contract.time")
local decompose_lib = require("devloop.decompose")
local replayer = require("devloop.replayer")
local conv_rounds = require("devloop.convergence.rounds")
local m_builders = require("devloop.markers.builders")

local C = {}

local ALLOWLIST_PATH = "migration/hidden-state.allowlist"
local REPO = "owner/repo"
local ISSUE_NUMBER = 42
local PR_NUMBER = 7
local ISSUE_PROPOSAL = "github-devloop/issue/owner/repo/42"
local PR_PROPOSAL = "github-devloop/pr/owner/repo/7"
local BRANCH = "devloop-owner-repo-42-01HY"
local BASE_BRANCH = "integration/dev"
local HEAD_SHA = "0123456789abcdef0123456789abcdef01234567"
local BASE_SHA = "fedcba9876543210fedcba9876543210fedcba98"
local ALT_HEAD_SHA = "89abcdef0123456789abcdef0123456789abcdef"
local SOURCE_REF = { kind = "external", ref = "owner/repo#issue/42" }
local PR_SOURCE_REF = { kind = "external", ref = "owner/repo#pr/7" }

local function package_name(core)
  return tostring(core.restart_package_name or "github-devloop")
end

local function production_replay_dept(core)
  local package = package_name(core)
  local dept = ({ ["github-devloop"] = "observe_issue", ["github-devloop-pr"] = "observe_pr" })[package]
  if dept == nil then
    error("devloop: hidden-state conformance has no production replay department for package " .. package)
  end
  for _, source in ipairs(core.restart_consumer_sources or {}) do
    if tostring(source or ""):match("departments/" .. dept .. "/main%.lua$") then
      return dept
    end
  end
  error("devloop: hidden-state conformance production replay department is not declared in restart_consumer_sources: " .. dept)
end

local function marker_author(core)
  if type(core.assert_trusted_bot_configured) == "function" then devloop_base.assert_trusted_bot_configured() end
  if type(core.trusted_bot_login) == "function" then return devloop_base.trusted_bot_login() end
  return tostring(core._test_bot_login or "fkst-test-bot")
end
local function comment(core, body, when)
  return {
    id = tostring(when or body):gsub("[^%w_%-]", "_"):sub(1, 60),
    body = body,
    author_login = marker_author(core),
    created_at = when or "2026-06-03T01:02:03Z",
  }
end

local function key(core, row, fact_family, successor)
  return table.concat({
    package_name(core),
    tostring(row.from_state or "?"),
    tostring(fact_family or "?"),
    tostring(successor or "?"),
  }, "|")
end

local function key_prefix(core, row)
  return table.concat({
    package_name(core),
    tostring(row.from_state or "?"),
    "",
  }, "|")
end

local function parse_allowlist_line(line)
  local text = tostring(line or "")
  if text == "" or text:match("^%s*#") then
    return nil
  end
  local parts = {}
  for part in text:gmatch("[^|]+") do
    table.insert(parts, part)
  end
  if #parts < 6 then
    return nil, "invalid hidden-state allowlist line: " .. text
  end
  if not tostring(parts[5]):match("^issue=#?%d+$") or tostring(parts[6]) == "why=" or not tostring(parts[6]):match("^why=") then
    return nil, "invalid hidden-state allowlist metadata: " .. text
  end
  return table.concat({ parts[1], parts[2], parts[3], parts[4] }, "|")
end

local function load_allowlist()
  local out = {}
  local ok, text = pcall(file.read, ALLOWLIST_PATH)
  if not ok then
    return out
  end
  for line in tostring(text or ""):gmatch("[^\n]+") do
    local parsed, err = parse_allowlist_line(line)
    if err ~= nil then
      table.insert(out, "__ERROR__|" .. err)
    elseif parsed ~= nil then
      out[parsed] = true
    end
  end
  return out
end

local function has_poll_surface(fact)
  local surfaces = fact.observe_surfaces or {}
  return surfaces.issue == true or surfaces.pr == true or surfaces.liveness_scan == true
end

local function row_has_declared_surface(row, fact)
  local row_surfaces = row.observe_surfaces or {}
  for surface, enabled in pairs(fact.observe_surfaces or {}) do
    if enabled == true and row_surfaces[surface] == true then
      return true
    end
  end
  return false
end

local function declared_by_key(core, rows)
  local declared = {}
  for _, row in ipairs(rows or {}) do
    for _, fact in ipairs(row.advancing_facts or {}) do
      declared[key(core, row, fact.fact_family, fact.successor)] = true
    end
  end
  return declared
end

local function global_advancing_fact_variants(rows)
  local variants = {}
  local seen = {}
  for _, row in ipairs(rows or {}) do
    for _, fact in ipairs(row.advancing_facts or {}) do
      local family = tostring(fact.fact_family or "")
      if family ~= "" then
        local variant_key = family .. "\0" .. tostring(fact.successor or "")
        if seen[variant_key] ~= true then
          seen[variant_key] = true
          table.insert(variants, fact)
        end
      end
    end
  end
  return variants
end

local function first_successor(row)
  for _, successor in ipairs(row.to_states or {}) do
    local value = tostring(successor or "")
    if value ~= "" and value ~= tostring(row.from_state or "") then
      return value
    end
  end
  return nil
end

local function remember_fact_family(by_family, ordered, declared, overwrite)
  local family = tostring((declared or {}).fact_family or "")
  if family == "" then
    return
  end
  if by_family[family] == nil then
    table.insert(ordered, family)
  end
  if overwrite == true or by_family[family] == nil then
    by_family[family] = declared
  end
end

local function required_fact_variant(row, required)
  local family = tostring((required or {}).family or "")
  if family == "" then
    return nil
  end
  return {
    fact_family = family,
    successor = first_successor(row),
    synthetic_required_fact = true,
  }
end

local function has_declared_advancing_facts(row)
  return type(row.advancing_facts) == "table" and #row.advancing_facts > 0
end

local function exemption_reason(row)
  local exemption = row.non_durable_advance
  if type(exemption) ~= "table" then
    return nil
  end
  -- Exempt only rows with no autonomous poll-derived durable-fact successor:
  -- pure operator-command reentry or terminal/recovery holds.
  local category = tostring(exemption.category or "")
  if category ~= "operator-reentry" and category ~= "terminal-hold" then
    return nil
  end
  local reason = tostring(exemption.reason or "")
  if reason == "" then
    return nil
  end
  return reason
end

local function has_allowlisted_row(core, allowlist, row)
  local prefix = key_prefix(core, row)
  for item in pairs(allowlist or {}) do
    if item:sub(1, #prefix) == prefix then
      return true
    end
  end
  return false
end

local function declaration_errors(core, rows, allowlist)
  local messages = {}
  local allowed_derivations = {
    ["source_ref:entity"] = true,
    ["source_ref:issue"] = true,
    ["source_ref:pr"] = true,
  }
  for _, row in ipairs(rows or {}) do
    local successors = {}
    for _, successor in ipairs(row.to_states or {}) do
      successors[successor] = true
    end
    if row.terminal ~= true
      and not has_declared_advancing_facts(row)
      and exemption_reason(row) == nil
      and not has_allowlisted_row(core, allowlist, row) then
      table.insert(messages, key_prefix(core, row) .. "*: non-terminal row must declare advancing_facts, non_durable_advance, or a shrink-only allowlist entry")
    end
    for _, fact in ipairs(row.advancing_facts or {}) do
      local label = key(core, row, fact.fact_family, fact.successor)
      if type(fact.fact_family) ~= "string" or fact.fact_family == "" then
        table.insert(messages, label .. ": advancing_facts entry must declare fact_family")
      end
      if type(fact.successor) ~= "string" or fact.successor == "" then
        table.insert(messages, label .. ": advancing_facts entry must declare successor")
      elseif successors[fact.successor] ~= true and tostring(fact.successor or "") ~= tostring(row.from_state or "") then
        table.insert(messages, label .. ": advancing_facts successor is not in to_states")
      end
      if type(fact.observe_surfaces) ~= "table" or next(fact.observe_surfaces) == nil then
        table.insert(messages, label .. ": advancing_facts entry must declare observe_surfaces")
      elseif not row_has_declared_surface(row, fact) then
        table.insert(messages, label .. ": advancing_facts observe_surfaces are not declared on row")
      elseif not has_poll_surface(fact) then
        table.insert(messages, label .. ": advancing fact must be re-derivable on a poll observe surface")
      end
      if allowed_derivations[tostring(fact.source_ref_derivation or "")] ~= true then
        table.insert(messages, label .. ": advancing_facts entry must declare source_ref_derivation")
      end
    end
  end
  local declared = declared_by_key(core, rows)
  local current_package_prefix = package_name(core) .. "|"
  for item in pairs(allowlist or {}) do
    if item:match("^__ERROR__|") then
      table.insert(messages, item:gsub("^__ERROR__|", ""))
    elseif item:sub(1, #current_package_prefix) == current_package_prefix and declared[item] == nil then
      table.insert(messages, item .. ": hidden-state allowlist entry has no matching advancing_facts row")
    end
  end
  return messages
end

local function with_effect_capture(core, fn)
  local events = {
    decisions = {},
    raises = {},
    applies = {},
  }
  local previous_decision = core.log_cas_decision
  local previous_raise = core.log_raise
  local previous_apply = core.log_apply
  core.log_cas_decision = function(dept, proposal_id, state, from_state, to_state, outcome, reason)
    table.insert(events.decisions, {
      dept = dept,
      proposal_id = proposal_id,
      state = state,
      from_state = from_state,
      to_state = to_state,
      outcome = outcome,
      reason = reason,
    })
  end
  core.log_raise = function(dept, proposal_id, queue, payload)
    table.insert(events.raises, {
      dept = dept,
      proposal_id = proposal_id,
      queue = queue,
      payload = payload,
    })
  end
  core.log_apply = function(dept, proposal_id, apply_state, version, label_changes, queues)
    table.insert(events.applies, {
      dept = dept,
      proposal_id = proposal_id,
      apply_state = apply_state,
      version = version,
      label_changes = label_changes,
      queues = queues,
    })
  end
  local ok, result = pcall(fn)
  core.log_cas_decision = previous_decision
  core.log_raise = previous_raise
  core.log_apply = previous_apply
  if not ok then
    error(result)
  end
  return result, events
end

local function source_ref_for(core, derivation)
  if derivation == "source_ref:pr" then
    return PR_SOURCE_REF
  end
  return SOURCE_REF
end

local function row_source_ref(core, row)
  if package_name(core) == "github-devloop-pr" then
    return PR_SOURCE_REF
  end
  for _, fact in ipairs(row.advancing_facts or {}) do
    if fact.source_ref_derivation == "source_ref:pr" then
      return PR_SOURCE_REF
    end
  end
  return SOURCE_REF
end

local function base_version(row)
  if tostring(row.from_state or "") == "impl-failed" then
    return "ready/behavioral/2026-06-03T01-02-03Z"
  end
  if tostring(row.from_state or "") == "implementing" then
    return "ready/behavioral/2026-06-03T01-02-03Z"
  end
  return tostring(row.from_state) .. "/behavioral/2026-06-03T01-02-03Z"
end

local function state_for(row)
  return {
    state = row.from_state,
    version = base_version(row),
    proposal_id = ISSUE_PROPOSAL,
    marker_created_at = "2026-06-03T01:02:03Z",
  }
end

local function base_entity(core, row, source_ref)
  local state = state_for(row)
  local body = core.state_marker(ISSUE_PROPOSAL, row.from_state, state.version, "result-marker,ready-label,devloop-ready")
  local labels = { "fkst-dev:enabled", core.state_label(row.from_state) }
  return {
    schema = "github-proxy.v1",
    type = "issue",
    repo = REPO,
    number = ISSUE_NUMBER,
    title = "Behavioral hidden-state conformance",
    body = "",
    state = "OPEN",
    updated_at = "2026-06-03T01:02:03Z",
    labels = labels,
    comments = { comment(core, body, "2026-06-03T01:02:03Z") },
    source_ref = source_ref,
  }, state
end

local function child_pr(core, state, child_state)
  local body = m_builders.pr_origin_marker(core, ISSUE_PROPOSAL, ISSUE_NUMBER, BRANCH, state.version, BASE_BRANCH)
  if child_state ~= nil then
    body = body .. "\n" .. core.state_marker(PR_PROPOSAL, child_state, state.version)
  end
  if child_state == "merged" then
    body = body .. "\n" .. m_builders.merged_marker(core, PR_PROPOSAL, PR_NUMBER, state.version, HEAD_SHA)
  end
  return {
    repo = REPO,
    number = PR_NUMBER,
    state = child_state == "merged" and "MERGED" or "OPEN",
    head_ref_name = BRANCH,
    base_ref_name = BASE_BRANCH,
    head_sha = HEAD_SHA,
    mergeable = "MERGEABLE",
    merge_state_status = "CLEAN",
    status_check_rollup = {
      { state = "COMPLETED", status = "COMPLETED", conclusion = "SUCCESS", name = "test" },
    },
    merge_commit_sha = HEAD_SHA,
    force_fresh = true,
    merged_at = child_state == "merged" and "2026-06-03T01:04:03Z" or nil,
    comments = { comment(core, body, "2026-06-03T01:04:03Z") },
  }
end

local function awaiting_pr_child_state_for_successor(successor)
  if successor == "ready" then
    return "closed-unmerged"
  end
  return successor
end

local function review_proposal(core, state) return devloop_base.pr_review_proposal_id(REPO, PR_NUMBER, state.version, HEAD_SHA) end
local function review_dedup(core, state) return "consensus:" .. review_proposal(core, state) .. "/review" end

local function add_common_pr_facts(core, entity, state, facts, include_pr_link_marker)
  local link = {
    proposal_id = ISSUE_PROPOSAL,
    pr_number = PR_NUMBER,
    branch = BRANCH,
    impl_version = state.version,
    base_branch = BASE_BRANCH,
  }
  facts.link = link
  facts.current_pr = facts.current_pr or child_pr(core, state, nil)
  if include_pr_link_marker == true then
    facts["pr-link"] = link
    table.insert(entity.comments, comment(core, m_builders.pr_link_marker(core, ISSUE_PROPOSAL, PR_NUMBER, BRANCH, state.version, BASE_BRANCH), "2026-06-03T01:03:03Z"))
  else
    facts._synthetic_pr_link = true
  end
  facts.snapshot.prs = {
    { number = PR_NUMBER, current = facts.current_pr },
  }
end

local function fact_value(core, row, state, family, successor)
  if family == "dependency-gate" then
    if successor == "ready" or successor == "implementing" then
      return { ok = true, kind = "satisfied", reason = "all-blockers-closed", unmet = {} }
    end
    if successor == "blocked" then
      return { ok = false, kind = "unresolvable", reason = "dependency-gate-stale", unmet = { 99 } }
    end
    return { ok = false, kind = "waiting", reason = "waiting-on-dependency", unmet = { 99 } }
  end
  if family == "dependency-wait" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      version = state.version,
      hold_kind = "waiting",
      reason = "waiting-on-dependency",
      unmet = { 99 },
    }
  end
  if family == "dependency-release" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      version = state.version,
    }
  end
  if family == "implement-attempt" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      dedup_key = state.version,
      attempt = 1,
      started_at = "2026-06-03T01:03:03Z",
    }
  end
  if family == "implementing" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      dedup_key = state.version,
      branch = BRANCH,
      head_sha = HEAD_SHA,
      base_branch = BASE_BRANCH,
      base_sha = BASE_SHA,
    }
  end
  if family == "impl-failure" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      dedup_key = state.version,
      reason = "codex-failed",
      attempt = 1,
    }
  end
  if family == "child-state" then
    return {
      proposal_id = PR_PROPOSAL,
      state = row.from_state == "awaiting-pr" and awaiting_pr_child_state_for_successor(successor) or successor,
      version = state.version,
    }
  end
  if family == "canonical-child-pr-merged" then
    return {
      proposal_id = PR_PROPOSAL,
      state = "merged",
      version = state.version,
      head_sha = HEAD_SHA,
    }
  end
  if family == "decomposed" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      version = state.version,
      pr_number = PR_NUMBER,
      count = 1,
    }
  end
  if family == "fix-feedback" then
    local reviewed = successor == "reviewing" and BASE_SHA or HEAD_SHA
    local review_id = devloop_base.pr_review_proposal_id(REPO, PR_NUMBER, state.version, reviewed)
    return {
      proposal_id = ISSUE_PROPOSAL,
      version = state.version,
      pr_number = PR_NUMBER,
      review_proposal_id = review_id,
      review_dedup_key = "consensus:" .. review_id .. "/review",
      reviewed_head_sha = reviewed,
      reason = "behavioral-fixture",
    }
  end
  if family == "review-result" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      pr_number = PR_NUMBER,
      review_proposal_id = review_proposal(core, state),
      review_dedup_key = review_dedup(core, state),
      reviewed_head_sha = HEAD_SHA,
      decision = successor == "merge-ready" and "approve" or "reject",
      blocking_gap = "behavioral-fixture",
    }
  end
  if family == "review-meta" or family == "review-converge-round" then
    local is_review_meta_replay = family == "review-converge-round" and successor == "review-meta"
    return {
      proposal_id = ISSUE_PROPOSAL,
      pr_number = PR_NUMBER,
      review_proposal_id = review_proposal(core, state),
      review_dedup_key = is_review_meta_replay and (review_dedup(core, state) .. "/loop/3") or review_dedup(core, state),
      reviewed_head_sha = HEAD_SHA,
      version = state.version,
      n = 3,
      action = (successor == "fixing" or is_review_meta_replay) and "fix" or "block",
      blocking_gap = "behavioral-fixture",
    }
  end
  if family == "merge-ready" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      pr_number = PR_NUMBER,
      version = state.version,
      review_proposal_id = review_proposal(core, state),
      review_dedup_key = review_dedup(core, state),
      head_sha = HEAD_SHA,
      approve = successor ~= "blocked",
    }
  end
  if family == "merging" then
    return {
      proposal_id = ISSUE_PROPOSAL,
      pr_number = PR_NUMBER,
      version = state.version,
      head_sha = HEAD_SHA,
    }
  end
  if family == "decompose-children" then
    return {}
  end
  if family == "converge-round" then
    local stalled = successor == "blocked"
    return {
      proposal_id = ISSUE_PROPOSAL,
      base_version = state.version,
      round = 3,
      dedup = state.version .. "/loop/3",
      narrowed_question = stalled and "behavioral fixture narrowed question" or "behavioral fixture changing question",
      angle_digests = stalled and { "a", "b", "c" } or { "a", "b", "changed" },
      true_stall_fixture = stalled,
      visible_round_sequence = not stalled,
    }
  end
  if family == "state" then
    return state
  end
  return nil
end

local function store_fact_value(facts, family, value)
  facts[family] = value
  facts[tostring(family):gsub("%-", "_")] = value
  if family == "pr-link" then
    facts.link = value
  elseif family == "pr-delegation" then
    facts.pr_delegation = value
  elseif family == "child-state" then
    facts.child_state = value
  elseif family == "fix-feedback" then
    facts.fix_feedback = value
    facts.feedback = facts.feedback or value
  elseif family == "review-result" or family == "merge-gate" then
    facts.feedback = facts.feedback or value
  elseif family == "review-meta" or family == "review-converge-round" then
    facts.review_meta = value
    facts.feedback = facts.feedback or value
  elseif family == "merge-ready" then
    facts.merge_ready = value
  elseif family == "impl-failure" then
    facts.impl_failure = value
  end
end

local function install_marker(core, entity, state, family, value, is_synthetic)
  if family == "dependency-wait" then
    table.insert(entity.comments, comment(core, core.dependency_wait_marker(ISSUE_PROPOSAL, state.version, value.unmet or {}, value.hold_kind, value.reason), "2026-06-03T01:03:04Z"))
  elseif family == "dependency-release" then
    table.insert(entity.comments, comment(core, core.dependency_release_marker(ISSUE_PROPOSAL, state.version), "2026-06-03T01:03:05Z"))
  elseif family == "implement-attempt" then
    table.insert(entity.comments, comment(core, core.implement_attempt_marker(ISSUE_PROPOSAL, state.version, value.attempt, value.started_at), "2026-06-03T01:03:06Z"))
  elseif family == "implementing" then
    table.insert(entity.comments, comment(core, m_builders.implementing_marker(core, ISSUE_PROPOSAL, state.version, BRANCH, HEAD_SHA, BASE_BRANCH, BASE_SHA), "2026-06-03T01:03:07Z"))
  elseif family == "impl-failure" then
    table.insert(entity.comments, comment(core, core.impl_failure_marker(ISSUE_PROPOSAL, state.version, value.reason or "codex-failed", value.attempt or 1), "2026-06-03T01:03:07Z"))
  elseif family == "decomposed" then
    table.insert(entity.comments, comment(core, decompose_lib.decomposed_marker(core, ISSUE_PROPOSAL, state.version, PR_NUMBER, value.count or 1), "2026-06-03T01:03:08Z"))
  elseif family == "fix-feedback" then
    table.insert(entity.comments, comment(core, m_builders.merge_gate_marker(core, ISSUE_PROPOSAL, PR_NUMBER, state.version, value.review_proposal_id, value.review_dedup_key, value.reviewed_head_sha, BASE_SHA, value.reason or "behavioral-fixture"), "2026-06-03T01:03:09Z"))
  elseif family == "review-result" then
    table.insert(entity.comments, comment(core, m_builders.review_result_marker(core, value.review_proposal_id, ISSUE_PROPOSAL, value.decision or "reject", value.review_dedup_key, core.version_fix_round(state.version), value.blocking_gap or "behavioral-fixture"), "2026-06-03T01:03:09Z"))
    if value.decision == "approve" then
      table.insert(entity.comments, comment(core, m_builders.merge_ready_marker(core, ISSUE_PROPOSAL, PR_NUMBER, state.version, value.review_proposal_id, value.review_dedup_key, HEAD_SHA), "2026-06-03T01:03:09Z"))
    else
      table.insert(entity.comments, comment(core, m_builders.merge_gate_marker(core, ISSUE_PROPOSAL, PR_NUMBER, state.version, value.review_proposal_id, value.review_dedup_key, HEAD_SHA, BASE_SHA, value.blocking_gap or "behavioral-fixture"), "2026-06-03T01:03:09Z"))
    end
  elseif family == "review-meta" then
    table.insert(entity.comments, comment(core, m_builders.review_meta_marker(core, ISSUE_PROPOSAL, value.review_dedup_key, value.action, state.version, value.blocking_gap or "behavioral-fixture"), "2026-06-03T01:03:09Z"))
  elseif family == "review-converge-round" then
    local digest = convergence_shared.source_ref_digest(PR_SOURCE_REF)
    if value.action == "block" then
      for round = 1, value.n do
        table.insert(entity.comments, comment(core, conv_rounds.review_converge_round_marker(core, value.review_proposal_id, ISSUE_PROPOSAL, state.version, HEAD_SHA, digest, round, state.version .. "/review-loop/" .. tostring(round), "behavioral fixture same review question", { "a", "b", "c" }), "2026-06-03T01:03:1" .. tostring(round) .. "Z"))
      end
    else
      table.insert(entity.comments, comment(core, conv_rounds.review_converge_round_marker(core, value.review_proposal_id, ISSUE_PROPOSAL, state.version, HEAD_SHA, digest, value.n, value.review_dedup_key, "behavioral fixture review question", {
        { perspective = "one", verdict = "comment", digest = "a" },
        { perspective = "two", verdict = "abstain", digest = "b" },
        { perspective = "three", verdict = "abstain", digest = "c" },
      }), "2026-06-03T01:03:09Z"))
    end
  elseif family == "merge-ready" then
    if value.approve ~= false then
      table.insert(entity.comments, comment(core, m_builders.review_result_marker(core, value.review_proposal_id, ISSUE_PROPOSAL, "approve", value.review_dedup_key), "2026-06-03T01:03:08Z"))
    end
    table.insert(entity.comments, comment(core, m_builders.merge_ready_marker(core, ISSUE_PROPOSAL, PR_NUMBER, state.version, value.review_proposal_id, value.review_dedup_key, HEAD_SHA), "2026-06-03T01:03:09Z"))
  elseif family == "merging" then
    table.insert(entity.comments, comment(core, m_builders.review_result_marker(core, value.review_proposal_id or review_proposal(core, state), ISSUE_PROPOSAL, "approve", value.review_dedup_key or review_dedup(core, state)), "2026-06-03T01:03:07Z"))
    table.insert(entity.comments, comment(core, m_builders.merge_ready_marker(core, ISSUE_PROPOSAL, PR_NUMBER, state.version, value.review_proposal_id or review_proposal(core, state), value.review_dedup_key or review_dedup(core, state), HEAD_SHA), "2026-06-03T01:03:08Z"))
    table.insert(entity.comments, comment(core, m_builders.merging_marker(core, ISSUE_PROPOSAL, PR_NUMBER, state.version, HEAD_SHA), "2026-06-03T01:03:09Z"))
  elseif family == "converge-round" then
    if value.true_stall_fixture == true then
      for round = 1, value.round do
        table.insert(entity.comments, comment(core, conv_rounds.converge_round_marker(core, ISSUE_PROPOSAL, state.version, convergence_shared.source_ref_digest(SOURCE_REF), round, state.version .. "/loop/" .. tostring(round), value.narrowed_question, value.angle_digests), "2026-06-03T01:03:1" .. tostring(round) .. "Z"))
      end
    elseif value.visible_round_sequence == true then
      for round = 1, value.round - 1 do
        table.insert(entity.comments, comment(core, conv_rounds.converge_round_marker(core, ISSUE_PROPOSAL, state.version, convergence_shared.source_ref_digest(SOURCE_REF), round, state.version .. "/loop/" .. tostring(round), "behavioral fixture narrowed question", { "a", "b", "c" }), "2026-06-03T01:03:1" .. tostring(round) .. "Z"))
      end
      table.insert(entity.comments, comment(core, conv_rounds.converge_round_marker(core, ISSUE_PROPOSAL, state.version, convergence_shared.source_ref_digest(SOURCE_REF), value.round, value.dedup, value.narrowed_question, value.angle_digests), "2026-06-03T01:03:10Z"))
    else
      table.insert(entity.comments, comment(core, conv_rounds.converge_round_marker(core, ISSUE_PROPOSAL, state.version, convergence_shared.source_ref_digest(SOURCE_REF), value.round, value.dedup, value.narrowed_question, value.angle_digests), "2026-06-03T01:03:10Z"))
    end
  elseif is_synthetic == true then
    table.insert(entity.comments, comment(core, '<!-- fkst:github-devloop:synthetic-visible-fact:v1 proposal="' .. ISSUE_PROPOSAL
      .. '" family="' .. tostring(family):gsub('"', "'")
      .. '" version="' .. tostring(state.version):gsub('"', "'")
      .. '" -->', "2026-06-03T01:03:11Z"))
  end
end

local function add_context_facts(core, row, entity, state, facts, source_ref, include_fact, declared)
  if row.from_state == "awaiting-pr" then
    local child_state = include_fact and awaiting_pr_child_state_for_successor(declared.successor) or nil
    if declared.fact_family == "canonical-child-pr-merged" then
      child_state = nil
    end
    facts.current_pr = child_pr(core, state, child_state)
    if declared.fact_family == "canonical-child-pr-merged" and include_fact == true then
      facts.current_pr.state = "MERGED"
      facts.current_pr.merged_at = "2026-06-03T01:04:03Z"
      facts.current_pr.merge_commit_sha = HEAD_SHA
    end
    table.insert(entity.comments, comment(core, m_builders.pr_delegation_marker(core, ISSUE_PROPOSAL, PR_PROPOSAL, PR_NUMBER, state.version, "g1"), "2026-06-03T01:03:03Z"))
    facts.pr_delegation = {
      proposal_id = ISSUE_PROPOSAL,
      pr_proposal_id = PR_PROPOSAL,
      pr_proposal = PR_PROPOSAL,
      pr_number = PR_NUMBER,
      version = state.version,
      delegation = "g1",
    }
    facts["pr-delegation"] = facts.pr_delegation
    facts.snapshot.prs = {
      { number = PR_NUMBER, current = facts.current_pr },
    }
  elseif package_name(core) == "github-devloop-pr" or source_ref == PR_SOURCE_REF then
    add_common_pr_facts(core, entity, state, facts, declared.fact_family ~= "pr-link" or include_fact == true)
    if include_fact == true and declared.fact_family == "pr-link" and declared.successor == "fixing" then
      facts.current_pr.mergeable, facts.current_pr.merge_state_status = "CONFLICTING", "DIRTY"
    elseif include_fact == true and declared.fact_family == "merging" then
      if declared.successor == "merged" then
        facts.current_pr.state, facts.current_pr.merged_at = "MERGED", "2026-06-03T01:04:03Z"
      elseif declared.successor == "reviewing" then
        facts.current_pr.head_sha = ALT_HEAD_SHA
      elseif declared.successor == "fixing" then
        facts.current_pr.mergeable, facts.current_pr.merge_state_status = "CONFLICTING", "DIRTY"
      elseif declared.successor == "blocked" then
        facts.current_pr.status_check_rollup = {
          { state = "IN_PROGRESS", status = "IN_PROGRESS", conclusion = "", name = "test" },
        }
      end
    end
    entity.type = "pr"
    entity.number = PR_NUMBER
    facts.current_pr = facts.current_pr or entity
    facts.current = entity
    facts.snapshot.comments = entity.comments
  elseif row.from_state == "blocked" then
    add_common_pr_facts(core, entity, state, facts, true)
  end
end

local function build_fixture_base(core, row, source_ref)
  local entity, state = base_entity(core, row, source_ref)
  local facts = {
    proposal_id = ISSUE_PROPOSAL,
    source_ref = source_ref,
    event_ts = "2026-06-03T01:05:00Z",
    now_seconds = contract_time.iso_timestamp_epoch_seconds("2026-06-03T01:05:00Z"),
  }
  facts.current = entity
  facts.current_issue = entity
  facts.snapshot = { comments = entity.comments, prs = {}, state = state }
  return entity, state, facts
end

local function build_fixture(core, row, declared, include_fact)
  local source_ref = source_ref_for(core, declared.source_ref_derivation)
  local entity, state, facts = build_fixture_base(core, row, source_ref)
  add_context_facts(core, row, entity, state, facts, source_ref, include_fact, declared)

  if include_fact then
    local value = fact_value(core, row, state, declared.fact_family, declared.successor)
    if value ~= nil then
      if package_name(core) ~= "github-devloop-pr" then
        store_fact_value(facts, declared.fact_family, value)
      end
      install_marker(core, entity, state, declared.fact_family, value)
    end
  elseif declared.fact_family == "converge-round" then
    local value = fact_value(core, row, state, declared.fact_family, row.from_state)
    if value ~= nil then
      install_marker(core, entity, state, declared.fact_family, value)
    end
  end
  if include_fact
    and row.from_state == "implementing"
    and declared.fact_family == "implementing" then
    local created = contract_time.iso_timestamp_epoch_seconds(state.marker_created_at)
    local budget = tonumber(row.budget and row.budget.minutes)
    if created ~= nil and budget ~= nil then
      facts.now_seconds = created + ((budget + 1) * 60)
    end
  end
  return entity, state, facts
end

local function build_exemption_fixture(core, row, rows, focus)
  local source_ref = row_source_ref(core, row)
  local entity, state, facts = build_fixture_base(core, row, source_ref)
  add_context_facts(core, row, entity, state, facts, source_ref, true, focus)

  local by_family = {}
  local ordered = {}
  for _, declared in ipairs(global_advancing_fact_variants(rows)) do
    remember_fact_family(by_family, ordered, declared, true)
  end
  for _, required in ipairs(row.required_facts or {}) do
    remember_fact_family(by_family, ordered, required_fact_variant(row, required), false)
  end
  if focus ~= nil and by_family[tostring(focus.fact_family or "")] ~= nil then
    by_family[tostring(focus.fact_family or "")] = focus
  end

  for _, family in ipairs(ordered) do
    local declared = by_family[family]
    local value = fact_value(core, row, state, family, declared.successor)
    if value == nil and declared.synthetic_required_fact == true then
      value = {
        proposal_id = ISSUE_PROPOSAL,
        version = state.version,
        family = family,
        synthetic_visible_fact = true,
      }
    end
    if value ~= nil then
      store_fact_value(facts, family, value)
      install_marker(core, entity, state, family, value, declared.synthetic_required_fact == true)
      if family == "canonical-child-pr-merged" then
        facts.current_pr = child_pr(core, state, "merged")
        facts.current_pr.force_fresh = true
        facts.snapshot.prs = {
          { number = PR_NUMBER, current = facts.current_pr },
        }
      end
    end
  end
  return entity, state, facts
end

local function advanced_to(events, from_state, successor)
  local saw_effect = false
  for _, apply in ipairs(events.applies or {}) do
    if tostring(apply.apply_state or "") == tostring(successor or "") then
      return true
    end
    for _, queue in ipairs(apply.queues or {}) do
      if tostring(queue or "") ~= "" then
        saw_effect = true
      end
    end
  end
  for _, decision in ipairs(events.decisions or {}) do
    if tostring(decision.to_state or "") == tostring(successor or "") then
      local outcome = tostring(decision.outcome or "")
      if outcome:find("applied", 1, true) ~= nil or outcome:find("release", 1, true) ~= nil or outcome:find("hold", 1, true) ~= nil then
        return true
      end
    end
  end
  if saw_effect and tostring(successor or "") == tostring(from_state or "") then
    return true
  end
  return false
end

local function successor_states(row)
  local successors = {}
  for _, state in ipairs(row.to_states or {}) do
    local value = tostring(state or "")
    if value ~= "" and value ~= tostring(row.from_state or "") then
      successors[value] = true
    end
  end
  return successors
end

local function advanced_to_successor_state(events, row)
  local successors = successor_states(row)
  for _, apply in ipairs((events or {}).applies or {}) do
    local to_state = tostring(apply.apply_state or "")
    if successors[to_state] == true then
      return true, to_state
    end
  end
  for _, decision in ipairs((events or {}).decisions or {}) do
    local to_state = tostring(decision.to_state or "")
    if successors[to_state] == true then
      local outcome = tostring(decision.outcome or "")
      if outcome:find("applied", 1, true) ~= nil
        or outcome:find("apply", 1, true) ~= nil
        or outcome:find("release", 1, true) ~= nil
        or outcome:find("hold", 1, true) ~= nil then
        return true, to_state
      end
    end
  end
  return false, nil
end

local function replay(core, row, declared, include_fact)
  local entity, state, facts = build_fixture(core, row, declared, include_fact)
  local issued, events = with_effect_capture(core, function()
    -- Keep the historical G-HIDDEN-STATE token as text only: core.replay_from_table.
    return replayer.replay_from_table(core, production_replay_dept(core), entity, state, row, facts)
  end)
  return issued, events
end

local function replay_exemption(core, row, rows, focus)
  local entity, state, facts = build_exemption_fixture(core, row, rows, focus)
  local issued, events = with_effect_capture(core, function()
    return replayer.replay_from_table(core, production_replay_dept(core), entity, state, row, facts)
  end)
  return issued, events
end

local function with_poll_fakes(core, fn)
  return poll_fakes.with(core, {
    base_branch = BASE_BRANCH,
    head_sha = HEAD_SHA,
  }, fn)
end

local function exemption_behavior_errors(core, rows, row)
  local messages = {}
  local variants = global_advancing_fact_variants(rows)
  if #variants == 0 then
    variants = { {} }
  end
  for _, focus in ipairs(variants) do
    local ok, issued, events = pcall(function()
      return with_poll_fakes(core, function()
        return replay_exemption(core, row, rows, focus)
      end)
    end)
    if not ok then
      table.insert(messages, key_prefix(core, row) .. "*: non_durable_advance all-facts poll fixture errored: " .. tostring(issued))
    else
      local advanced, successor = advanced_to_successor_state(events, row)
      if issued == true and advanced then
        table.insert(messages, key_prefix(core, row) .. "*: non_durable_advance exemption advanced to successor " .. tostring(successor) .. " with all durable fact families present")
      end
    end
  end
  return messages
end

local function behavioral_errors(core, rows, allowlist)
  local messages = {}
  for _, row in ipairs(rows or {}) do
    if row.terminal ~= true then
      for _, declared in ipairs(row.advancing_facts or {}) do
        local label = key(core, row, declared.fact_family, declared.successor)
        local prefix = key_prefix(core, row)
        local row_allowlisted = allowlist[label] == true
        for allowed in pairs(allowlist) do
          if allowed:sub(1, #prefix) == prefix then
            row_allowlisted = row_allowlisted or allowed == label
          end
        end
        local ok, issued, events = pcall(function()
          return with_poll_fakes(core, function()
            return replay(core, row, declared, true)
          end)
        end)
        local passes_positive = ok and issued == true and advanced_to(events, row.from_state, declared.successor)
        local positive_message = nil
        if not passes_positive then
          positive_message = label .. ": positive poll fixture did not advance to declared successor"
        end
        ok, issued, events = pcall(function()
          return with_poll_fakes(core, function()
            return replay(core, row, declared, false)
          end)
        end)
        local passes_negative = not (ok and issued == true and advanced_to(events, row.from_state, declared.successor))
        local negative_message = nil
        if ok and issued == true and advanced_to(events, row.from_state, declared.successor) then
          negative_message = label .. ": negative poll fixture advanced without declared fact"
        end
        if row_allowlisted then
          if passes_positive and passes_negative then
            table.insert(messages, label .. ": hidden-state allowlist entry is now passing; remove it")
          end
        else
          if positive_message ~= nil then
            table.insert(messages, positive_message)
          end
          if negative_message ~= nil then
            table.insert(messages, negative_message)
          end
        end
      end
      if exemption_reason(row) ~= nil then
        for _, message in ipairs(exemption_behavior_errors(core, rows, row)) do
          table.insert(messages, message)
        end
      end
    end
  end
  return messages
end

function C.hidden_state_conformance_errors(M, rows, allowlist)
  local core = M
  local effective_rows = rows or core.restart_transition_table()
  local effective_allowlist = allowlist or load_allowlist()
  local messages = declaration_errors(core, effective_rows, effective_allowlist)
  for _, message in ipairs(behavioral_errors(core, effective_rows, effective_allowlist)) do
    table.insert(messages, message)
  end
  table.sort(messages)
  return messages
end

function C.hidden_state_behavior_fixture(M, row, declared, include_fact)
  local core = M
  return build_fixture(core, row, declared, include_fact)
end

return C
