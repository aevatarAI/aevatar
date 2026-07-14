local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local m_facts = require("devloop.markers.facts")
local S = {}
local convergence_shared = require("devloop.convergence.shared")
local registry = require("workflow.registry")
local forge_validators = require("devloop.forge_validators")
local transition_version = require("contract.transition_version")

local source_ref_derivations = {
  entity = true,
  issue = true,
  pr = true,
}

local payload_derivations = {
  ["literal:github-devloop.fixing.v1"] = true,
  ["dedup:replayed-fixing"] = true,
  ["comment_body:fix-feedback"] = true,
}

local function fact(family, freshness)
  return { family = family, freshness = freshness }
end

local function advancing_fact(fact_family, successor, observe_surfaces, source_ref_derivation)
  return {
    fact_family = fact_family,
    successor = successor,
    observe_surfaces = observe_surfaces,
    source_ref_derivation = source_ref_derivation,
  }
end

local function obligation(kinds, exits)
  return {
    kinds = kinds,
    exits = exits,
  }
end

local function effect(kinds, completeness, completeness_derivation)
  local declared_kinds = kinds
  if type(kinds) ~= "table" then
    declared_kinds = {}
    for index = 1, tonumber(kinds) or 0 do
      declared_kinds[index] = "effect-" .. tostring(index)
    end
  end
  return {
    intent_count = #declared_kinds,
    kinds = declared_kinds,
    completeness = completeness,
    completeness_derivation = completeness_derivation,
  }
end

local function budget(minutes, receiver_max_work_justification)
  return {
    minutes = minutes,
    receiver_max_work_justification = receiver_max_work_justification,
  }
end

local function timeout(queue)
  return {
    action = "redrive",
    queue = queue,
    escalate_after_attempts = 3,
    on_escalate = {
      action = "force-terminate",
      terminal_state = "blocked",
      reason = "state-output-obligation-timeout",
    },
  }
end

local function liveness(contract)
  return contract
end

local function watchdog(mode, minutes)
  return {
    mode = mode,
    budget_ms = tonumber(minutes) * 60 * 1000,
  }
end

local function actionable_epoch(source)
  return {
    source = source,
    generation_source = "same_as_actionable_epoch",
  }
end

local function responsibility_signature(signature)
  return signature
end

local transition_helpers = {
  fact = fact,
  advancing_fact = advancing_fact,
  obligation = obligation,
  effect = effect,
  budget = budget,
  timeout = timeout,
  liveness = liveness,
  watchdog = watchdog,
  actionable_epoch = actionable_epoch,
  responsibility_signature = responsibility_signature, span_contract = responsibility_signature,
}

function S.transition_table(M, resolved)
resolved = resolved or {}
local package_name = M.restart_package_name or "github-devloop"
local transition_index = assert(resolved.transitions_index, package_name .. ": missing resolved restart transitions_index")
local transition_entries = assert(resolved.transitions, package_name .. ": missing resolved restart transitions")
return registry.build_indexed_array(resolved.transitions_label or "restart.transitions", transition_index, transition_entries, "from_state", M, transition_helpers, package_name)
end

function S.install(M, resolved)
resolved = resolved or {}

local package_name = M.restart_package_name or "github-devloop"
local default_consumer_sources = M.restart_consumer_sources or {}

local marker_fields = assert(resolved.marker_fields, package_name .. ": missing resolved restart marker_fields")

local required_replay_payload_fields = assert(resolved.replay_payload_fields, package_name .. ": missing resolved restart replay_payload_fields")

local transition_table = S.transition_table(M, resolved)

local audit_by_state = {}
for _, row in ipairs(transition_table) do
  audit_by_state[row.from_state] = row
end

function M.restart_completeness_audit()
  local rows = {}
  for _, row in ipairs(transition_table) do
    table.insert(rows, {
      state = row.from_state,
      marker_facts = row.marker_facts,
      kickoff = row.kickoff,
      replay = row.replay,
    })
  end
  return rows
end

function M.restart_completeness_audit_for_state(state)
  return audit_by_state[state]
end

function M.restart_transition_table()
  return transition_table
end

function M.restart_durable_marker_fields()
  return marker_fields
end

function M.restart_source_ref_derivations()
  return source_ref_derivations
end

function M.restart_required_replay_payload_fields()
  return required_replay_payload_fields
end

local function field_reference_error(reference)
  local marker_family, attr = tostring(reference or ""):match("^marker:([^%.]+)%.(.+)$")
  if marker_family ~= nil then
    if marker_fields[marker_family] == nil then
      return "unknown marker family " .. marker_family
    end
    if marker_fields[marker_family][attr] ~= true then
      return "unknown marker attr " .. marker_family .. "." .. attr
    end
    return nil
  end
  local derivation = tostring(reference or ""):match("^source_ref:(.+)$")
  if derivation ~= nil then
    if source_ref_derivations[derivation] == true then
      return nil
    end
    return "unknown source_ref derivation " .. derivation
  end
  if payload_derivations[tostring(reference or "")] == true then
    return nil
  end
  return "unsupported payload field source " .. tostring(reference)
end

function M.restart_field_coverage_errors(rows)
  local errors = {}
  for _, row in ipairs(rows or transition_table) do
    local required_fields = required_replay_payload_fields[row.from_state] or {}
    for field, reason in pairs(required_fields) do
      if (row.payload_fields or {})[field] == nil then
        table.insert(errors, tostring(row.from_state or "?") .. "." .. tostring(field) .. ": missing required replay payload field: " .. tostring(reason))
      end
    end
    for field, reference in pairs(row.payload_fields or {}) do
      local err = field_reference_error(reference)
      if err ~= nil then
        table.insert(errors, tostring(row.from_state or "?") .. "." .. tostring(field) .. ": " .. err)
      end
    end
  end
  return errors
end

local function source_contains_any(paths, needle)
  if needle == nil or needle == "" then
    return false
  end
  for _, path in ipairs(paths or {}) do
    local ok, text = pcall(file.read, path)
    if ok and tostring(text or ""):find(tostring(needle), 1, true) ~= nil then
      return true
    end
  end
  return false
end

function M.restart_effect_contract_errors(rows, consumer_sources)
  local errors = {}
  local sources = consumer_sources or default_consumer_sources
  for _, row in ipairs(rows or transition_table) do
    local effects = row.effects or {}
    local kinds = effects.kinds or {}
    local count = tonumber(effects.intent_count) or #kinds
    if count > 1 then
      if type(kinds) ~= "table" or #kinds ~= count then
        table.insert(errors, tostring(row.from_state or "?") .. ": multi-effect row must enumerate declared effects")
      end
      if type(effects.completeness_derivation) ~= "string" or effects.completeness_derivation == "" then
        table.insert(errors, tostring(row.from_state or "?") .. ": multi-effect row must declare a completeness derivation")
      elseif not source_contains_any(sources, effects.completeness_derivation) then
        table.insert(errors, tostring(row.from_state or "?") .. ": completeness derivation is not called by consumer sources")
      end
    end
  end
  return errors
end

function M.latest_complete_converge_round(comments, proposal_id, base_version, source_ref)
  local sr_digest = convergence_shared.source_ref_digest(source_ref)
  local latest = nil
  local facts = base_version ~= nil
    and conv_rounds.converge_round_facts(M, comments, proposal_id, base_version, sr_digest)
    or conv_rounds.converge_round_facts_for_source(M, comments, proposal_id, sr_digest)
  for _, fact in ipairs(facts) do
    if fact.narrowed_question ~= nil
      and fact.narrowed_question ~= ""
      and type(fact.angle_digests) == "table"
      and #fact.angle_digests > 0
      and (latest == nil or fact.round > latest.round) then
      latest = fact
    end
  end
  return latest
end

local function review_meta_fact_from_converge_marker(M, comments, issue_proposal_id, issue_version)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-converge%-round:v1.-%-%->"
  local heartbeat_version = M.liveness_heartbeat_version(issue_version, M.liveness_signal_producer_contract("review-converge-round"))
  local best = nil
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('issue_proposal="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local review_proposal = marker:match('proposal="([^"]+)"')
      local consensus_dedup = marker:match('dedup="([^"]*)"')
      local round = tonumber(marker:match('round="(%d+)"'))
      local _, pr_number, review_version = devloop_base.parse_pr_review_proposal_id(review_proposal)
      local repo = base_ids.parse_proposal_id(issue_proposal_id)
      if marker_issue == tostring(issue_proposal_id)
        and marker_version == tostring(heartbeat_version)
        and review_version == tostring(heartbeat_version)
        and repo ~= nil
        and forge_validators.is_positive_pr_number(pr_number)
        and strings.is_path_safe_key(review_proposal, M._max_key_len)
        and strings.is_bounded_string(consensus_dedup, M._max_dedup_len)
        and (best == nil or (round or 0) > (best.n or 0)) then
        best = {
          proposal_id = review_proposal,
          dedup_key = consensus_dedup,
          source_ref = entity_lib.pr_source_ref(repo, pr_number),
          pr_number = tonumber(pr_number),
          n = (round or 0) + 1,
        }
      end
    end
  end
  return best
end

function M.review_meta_replay_fact_from_state(comments, issue_proposal_id, issue_version, pr_number, head_sha, n)
  local repo = base_ids.parse_proposal_id(issue_proposal_id)
  if repo == nil
    or not forge_validators.is_positive_pr_number(pr_number)
    or not forge_validators.is_git_sha(head_sha)
    or not strings.is_bounded_string(issue_version, M._max_dedup_len) then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-meta:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_dedup = marker:match('dedup="([^"]*)"')
      local review_proposal = marker_dedup ~= nil and marker_dedup:match("^consensus:([^/].-)/review") or nil
      local _, review_pr_number, review_version, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
      if marker_issue == tostring(issue_proposal_id)
        and tostring(review_pr_number or "") == tostring(pr_number)
        and review_version == transition_version.safe_version_segment(M._strip_latest_fix_version_suffix(issue_version))
        and tostring(reviewed_head_sha or "") == tostring(head_sha)
        and devloop_base.is_safe_pr_review_result_ref(review_proposal, marker_dedup) then
        return {
          proposal_id = review_proposal,
          dedup_key = marker_dedup,
          source_ref = entity_lib.pr_source_ref(repo, pr_number),
          pr_number = tonumber(pr_number),
          n = tonumber(n) or 0,
        }
      end
    end
  end
  marker_pattern = "<!%-%- fkst:github%-devloop:fix%-reflection:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_dedup = marker:match('dedup="([^"]*)"')
      local verdict = marker:match('verdict="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local round = tonumber(marker:match('fix_round="(%d+)"'))
      local review_proposal = marker_dedup ~= nil and marker_dedup:match("^consensus:([^/].-)/review") or nil
      local _, review_pr_number, review_version, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(review_proposal)
      if marker_issue == tostring(issue_proposal_id)
        and verdict == "checkpoint"
        and marker_version == tostring(issue_version)
        and tostring(review_pr_number or "") == tostring(pr_number)
        and review_version == transition_version.safe_version_segment(M._strip_latest_fix_version_suffix(issue_version))
        and tostring(reviewed_head_sha or "") == tostring(head_sha)
        and devloop_base.is_safe_pr_review_result_ref(review_proposal, marker_dedup) then
        local reject_fact = m_facts.review_reject_fact(M, comments, issue_proposal_id, issue_version)
        if reject_fact == nil
          or tostring(reject_fact.review_proposal_id or "") ~= tostring(review_proposal)
          or tostring(reject_fact.review_dedup_key or "") ~= tostring(marker_dedup)
          or not strings.is_bounded_string(reject_fact.blocking_gap, M._max_blocking_gap_len) then
          return nil
        end
        local reflection_dedup = payloads_builders.fix_reflection_dedup_key(M, issue_proposal_id, issue_version, pr_number, round, marker_dedup)
        return {
          proposal_id = review_proposal,
          dedup_key = reflection_dedup,
          review_dedup_key = marker_dedup,
          source_ref = entity_lib.pr_source_ref(repo, pr_number),
          pr_number = tonumber(pr_number),
          n = tonumber(n) or 0,
          mode = "fix-reflection",
          fix_round = round,
          blocking_gap = reject_fact.blocking_gap,
        }
      end
    end
  end
  local reject_fact = m_facts.review_reject_fact(M, comments, issue_proposal_id, issue_version)
  local _, reject_pr_number, _, reviewed_head_sha = devloop_base.parse_pr_review_proposal_id(reject_fact and reject_fact.review_proposal_id)
  if reject_fact ~= nil
    and tostring(reject_pr_number or "") == tostring(pr_number)
    and tostring(reviewed_head_sha or "") == tostring(head_sha)
    and devloop_base.is_safe_pr_review_result_ref(reject_fact.review_proposal_id, reject_fact.review_dedup_key) then
    return {
      proposal_id = reject_fact.review_proposal_id,
      dedup_key = reject_fact.review_dedup_key,
      source_ref = entity_lib.pr_source_ref(repo, pr_number),
      pr_number = tonumber(pr_number),
      n = tonumber(n) or 0,
    }
  end
  return nil
end

function M.review_meta_replay_fact(comments, issue_proposal_id, issue_version, pr_number, head_sha)
  local converge_fact = review_meta_fact_from_converge_marker(M, comments, issue_proposal_id, issue_version)
  if converge_fact ~= nil then
    return converge_fact
  end
  return M.review_meta_replay_fact_from_state(comments, issue_proposal_id, issue_version, pr_number, head_sha, 0)
end

function M.fixing_replay_feedback_fact(comments, issue_proposal_id, issue_version)
  local reject_fact = m_facts.review_reject_fact(M, comments, issue_proposal_id, issue_version)
  if reject_fact ~= nil then
    return reject_fact
  end
  local meta_fix_fact = m_facts.review_meta_fix_fact(M, comments, issue_proposal_id, issue_version)
  if meta_fix_fact ~= nil then
    return meta_fix_fact
  end
  return m_facts.merge_gate_fix_fact(M, comments, issue_proposal_id, issue_version)
end

function M.fixing_version_matches_link(issue_version, link_version)
  local current = tostring(issue_version or "")
  local linked = tostring(link_version or "")
  if current == linked or M._strip_latest_fix_version_suffix(current) == linked then
    return true
  end
  local current_base = transition_version.strip_suffixes(current)
  local linked_base = transition_version.strip_suffixes(linked)
  if current_base == "" or linked_base == "" then
    return false
  end
  return transition_version.safe_version_segment(current_base) == transition_version.safe_version_segment(linked_base)
end

end

return S
