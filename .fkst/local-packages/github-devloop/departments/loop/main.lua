local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local requests_lifecycle = require("devloop.requests.lifecycle")
local parsers_issue = require("devloop.parsers.issue")
local convergence_shared = require("devloop.convergence.shared")
local core, saga = require("core"), require("workflow.saga")
local context_bundle = require("devloop.context_bundle")
local config = require("devloop.config")



local payloads_builders = require("devloop.payloads.builders")
local conv_rounds = require("devloop.convergence.rounds")
local v_unresolved = require("devloop.validators.unresolved")
local v_validate_proposal = require("devloop.validators.validate_proposal")
local entity_lib = require("devloop.entity")
local spec = {
  consumes = { "consensus.consensus_converge" },
  produces = {
    "consensus.proposal",
    "github-proxy.github_issue_comment_request",
  },
  fanout = { "consensus.consensus_converge" },
  stall_window = "30s",
  retry = { max_attempts = 12, base = "5s", cap = "30s" },
}

return saga.department(spec, { done = function() return false end, act = function(event)
  local unresolved = event.payload or {}
  if not v_unresolved.is_supported_unresolved(core, unresolved) then
    core.log_entry("loop", event, "unknown", core.payload_field(unresolved, "dedup_key"))
    core.log_cas_decision("loop", "unknown", { state = nil, version = nil }, "thinking", "thinking", "skip-foreign(proposal_id)", "unsupported event payload")
    return
  end

  core.log_entry("loop", event, unresolved.proposal_id, unresolved.dedup_key)
  local repo, issue_number = base_ids.parse_proposal_id(unresolved.proposal_id)
  if repo == nil then
    core.log_cas_decision("loop", unresolved.proposal_id, { state = nil, version = nil }, "thinking", "thinking", "skip-foreign(proposal_id)", "proposal_id is outside github-devloop")
    return
  end

  local lock_key = entity_lib.loop_lock_key(unresolved.proposal_id)
  if lock_key == nil then
    core.log_cas_decision("loop", unresolved.proposal_id, { state = nil, version = nil }, "thinking", "thinking", "skip-foreign(proposal_id)", "no transition lock key")
    return
  end

  with_lock(lock_key, function()
    devloop_base.assert_trusted_bot_configured()

    local view = core.gh_issue_view_loop(repo, issue_number, 30)
    if view.exit_code ~= 0 then
      error("github-devloop: gh issue loop view failed: " .. tostring(view.stderr))
    end

    local current = parsers_issue.parse_issue_view_loop(core, view.stdout)
    core.log_forged_markers("loop", unresolved.proposal_id, current.comments)
    local state = core.current_state(current.comments, unresolved.proposal_id)
    local transition = core.transition_status(state, { "thinking" }, "blocked")
    if transition == "idempotent" or transition == "stale" then
      core.log_cas_decision("loop", unresolved.proposal_id, state, "thinking", "thinking", core.cas_outcome(state, transition, unresolved.dedup_key), "unresolved event cannot advance current marker")
      return
    end
    if transition == "pending" then
      core.log_cas_decision("loop", unresolved.proposal_id, state, "thinking", "thinking", core.cas_outcome(state, transition, unresolved.dedup_key), "thinking state marker not yet visible")
      error("github-devloop: thinking state marker not yet visible for unresolved; retrying")
    end

    local base_version = conv_rounds.converge_base_version(core, unresolved.dedup_key)
    local sr_digest = convergence_shared.source_ref_digest(unresolved.source_ref)
    local facts = conv_rounds.converge_round_facts_for_proposal_boundary(core, current.comments, unresolved.proposal_id, unresolved.narrowed_question, unresolved.angle_digests)
    local round = math.max(tonumber(unresolved.round) or 0, conv_rounds.max_converge_round(core, facts))
    if conv_rounds.has_converge_round_marker(core, current.comments, unresolved.proposal_id, base_version, sr_digest, round) then
      core.log_cas_decision("loop", unresolved.proposal_id, state, "thinking", "thinking", "skip-idempotent(converge round marker already visible)", "converge round marker for incoming round is already visible")
      return
    end

    local marker_body = conv_rounds.converge_round_marker(core,
      unresolved.proposal_id,
      base_version,
      sr_digest,
      round,
      unresolved.dedup_key,
      unresolved.narrowed_question,
      unresolved.angle_digests
    )
    local facts_with_current = conv_rounds.append_converge_round_fact(core, facts, round, unresolved.narrowed_question, unresolved.angle_digests, unresolved.dedup_key)
    local budget_round = math.max(round, conv_rounds.converge_boundary_budget_round(core, current.comments, unresolved.proposal_id, unresolved.narrowed_question, unresolved.angle_digests))
    local hit_round_cap = budget_round >= config.max_converge_rounds(core)
    if hit_round_cap or conv_rounds.is_true_stall(core, facts_with_current, round) then
      local comment_request = requests_lifecycle.build_converge_round_comment_request(core, repo, issue_number, unresolved, round, marker_body, {
        kind = "github-devloop.reconcile",
        proposal_id = unresolved.proposal_id,
        round = round,
        base_version = base_version,
        source_ref = base_ids.normalize_source_ref(unresolved.source_ref),
      })
      local reason = hit_round_cap
        and ("convergence budget reached at round " .. tostring(budget_round))
        or ("true convergence stall at round " .. tostring(round))
      core.log_cas_decision("loop", unresolved.proposal_id, state, "thinking", "thinking", core.cas_outcome(state, transition, unresolved.dedup_key), reason)
      core.log_apply("loop", unresolved.proposal_id, nil, nil, { add = {}, remove = {} }, {
        "github-proxy.github_issue_comment_request",
      })
      core.log_raise("loop", unresolved.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
      return
    end

    local next_n = round + 1
    local next_dedup = conv_rounds.converge_proposal_base_dedup(core, unresolved.dedup_key) .. "/loop/" .. tostring(next_n)
    local content_fetch = context_bundle.context_fetch_ref_from_bundle(core, {
      dept = "loop",
      repo = repo,
      issue_number = issue_number,
      proposal_id = unresolved.proposal_id,
      version = next_dedup,
      tick = event.ts,
    })
    local proposal = payloads_builders.build_board_loop_proposal(core, repo, issue_number, current, unresolved.source_ref, next_n, {
      narrowed_question = unresolved.narrowed_question,
      angle_digests = unresolved.angle_digests,
    }, event.ts, content_fetch, next_dedup)
    if not v_validate_proposal.validate_proposal(core, proposal) then
      log.warn("github-devloop dept=loop proposal_id=" .. tostring(unresolved.proposal_id) .. " tag=SKIP reason=cannot-build-valid-loop-proposal")
      return
    end
    local comment_request = requests_lifecycle.build_converge_round_comment_request(core, repo, issue_number, unresolved, round, marker_body)

    core.log_cas_decision("loop", unresolved.proposal_id, state, "thinking", "thinking", core.cas_outcome(state, transition, unresolved.dedup_key), "raising loop proposal round " .. tostring(next_n))
    core.log_apply("loop", unresolved.proposal_id, nil, nil, { add = {}, remove = {} }, {
      "consensus.proposal",
      "github-proxy.github_issue_comment_request",
    })
    core.log_raise("loop", unresolved.proposal_id, "consensus.proposal", proposal)
    core.log_raise("loop", unresolved.proposal_id, "github-proxy.github_issue_comment_request", comment_request)
  end)
end, wrap = core.wrap_pipeline_failure, name = "loop" })
