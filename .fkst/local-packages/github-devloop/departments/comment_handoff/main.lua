local devloop_base = require("devloop.base")
local strings = require("contract.strings")
local core = require("core")
local saga = require("workflow.saga")
local source_refs = require("contract.source_ref")
local valid_round = require("devloop.rounds").valid_round
local handoff_helpers = require("devloop.comment_handoff")

local payloads_builders = require("devloop.payloads.builders")
local payloads_predicates = require("devloop.payloads.predicates")
local conv_reconcile = require("devloop.convergence.reconcile")
local spec = {
  consumes = { "github-proxy.github_comment_written" },
  produces = {
    "devloop_ready",
    "devloop_reconcile",
  },
  fanout = { "github-proxy.github_comment_written" },
  stall_window = "30s",
}

local function supported_handoff(payload)
  if type(payload) ~= "table"
    or payload.schema ~= "github-proxy.comment-written.v1"
    or not payloads_predicates.is_safe_comment_id(core, payload.comment_id)
    or type(payload.handoff) ~= "table" then
    return nil
  end
  local handoff = payload.handoff
  if handoff.kind == "github-devloop.ready"
    and devloop_base.is_safe_consensus_result_ref(handoff.proposal_id, handoff.version)
    and devloop_base.is_safe_consensus_result_ref(handoff.proposal_id, handoff.marker_version)
    and strings.is_bounded_string(handoff.version, core._max_dedup_len)
    and source_refs.has_bounded_source_ref(handoff.source_ref, core._max_key_len) then
    return handoff
  end
  if handoff.kind == "github-devloop.reconcile"
    and devloop_base.is_safe_consensus_result_ref(handoff.proposal_id, handoff.base_version)
    and strings.is_bounded_string(handoff.base_version, core._max_dedup_len)
    and valid_round(handoff.round) ~= nil
    and source_refs.has_bounded_source_ref(handoff.source_ref, core._max_key_len) then
    return handoff
  end
  return nil
end

local accept_handoff = handoff_helpers.acceptor(supported_handoff)

local function handoff_done(_event)
  return false
end

local log_unsupported_handoff = function(event) return handoff_helpers.log_unsupported(core, supported_handoff, event) end

local function act_handoff(event)
  local payload = event.payload or {}
  local handoff = supported_handoff(payload)
  if handoff == nil then
    log_unsupported_handoff(event)
    return
  end

  core.log_entry("comment_handoff", event, handoff.proposal_id, payload.dedup_key)
  if handoff.kind == "github-devloop.ready" then
    local ready = payloads_builders.build_devloop_ready_payload(core, {
      proposal_id = handoff.proposal_id,
      dedup_key = handoff.marker_version,
      source_ref = handoff.source_ref,
      include_ready_hand_off = true,
      ready_comment_id = payload.comment_id,
    })
    core.log_cas_decision("comment_handoff", handoff.proposal_id, { state = "ready", version = ready.dedup_key }, "comment-written", "devloop_ready", "applied(own-write-comment-id)", "ready marker comment write was acknowledged")
    core.log_raise("comment_handoff", handoff.proposal_id, "devloop_ready", ready)
    return
  end

  if handoff.kind == "github-devloop.reconcile" then
    local reconcile = conv_reconcile.build_devloop_reconcile_payload(core, {
      proposal_id = handoff.proposal_id,
      source_ref = handoff.source_ref,
    }, handoff.round, handoff.base_version)
    core.log_cas_decision("comment_handoff", handoff.proposal_id, { state = "thinking", version = handoff.base_version }, "comment-written", "devloop_reconcile", "applied(own-write-comment-id)", "converge round comment write was acknowledged")
    core.log_raise("comment_handoff", handoff.proposal_id, "devloop_reconcile", reconcile)
    return
  end

  log_unsupported_handoff(event)
end

return saga.department(spec, {
  accept = accept_handoff,
  done = handoff_done,
  act = act_handoff,
  on_skip_foreign = log_unsupported_handoff,
  wrap = core.wrap_pipeline_failure,
  name = "comment_handoff",
})
