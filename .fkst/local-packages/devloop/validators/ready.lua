local devloop_base = require("devloop.base")
local forge_validators = require("devloop.forge_validators")
local strings = require("contract.strings")
local source_refs = require("contract.source_ref")

local payloads_predicates = require("devloop.payloads.predicates")
local payloads_shared = require("devloop.payloads.shared")
local C = {}

local function is_supported_redrive_delivery(payload)
  if payload.redrive_delivery == nil then
    return payload.implementation_version == nil
  end
  if payload.implementation_version == nil then
    return false
  end
  local ok, expected = pcall(
    payloads_shared.ready_redrive_delivery_dedup_key,
    payload.proposal_id,
    payload.implementation_version,
    payload.redrive_delivery
  )
  return ok and payload.dedup_key == expected
end

local function is_supported_blocked_reimplement(payload)
  local reentry = payload.operator_reentry
  if type(reentry) ~= "table"
    or reentry.command ~= "reimplement"
    or reentry.from_state ~= "blocked"
    or not devloop_base.is_safe_proposal_ref(payload.proposal_id, reentry.impl_version)
    or not devloop_base.is_safe_proposal_ref(payload.proposal_id, reentry.state_version)
    or reentry.impl_version ~= payload.dedup_key then
    return false
  end
  if reentry.terminal_reason == "implementing-timeout-without-pr" then
    local round = tonumber(reentry.timeout_round)
    return reentry.pr_number == nil and round ~= nil and round >= 1 and round == math.floor(round)
  end
  return reentry.terminal_reason == nil and forge_validators.is_positive_pr_number(reentry.pr_number)
end

function C.is_supported_ready(M, payload)
  return type(payload) == "table"
    and payload.schema == "github-devloop.ready.v1"
    and devloop_base.is_safe_proposal_ref(payload.proposal_id, payload.dedup_key)
    and devloop_base.is_safe_proposal_ref(
      payload.proposal_id,
      payload.implementation_version or payload.dedup_key
    )
    and is_supported_redrive_delivery(payload)
    and (payload.framing == nil or strings.is_bounded_string(payload.framing, M._max_framing_len))
    and (payload.operator_reentry == nil or is_supported_blocked_reimplement(payload))
    and (payload.ready_hand_off == nil
      or (payload.impl_retry_attempt == nil
        and payloads_predicates.is_own_state_marker_hand_off(payload.ready_hand_off, {
          proposal_id = payload.proposal_id,
          state = "ready",
          marker_version = payload.ready_hand_off.marker_version,
          event_version = payload.dedup_key,
        })))
    and (payload.impl_retry_attempt == nil
      or (tonumber(payload.impl_retry_attempt) ~= nil
        and tonumber(payload.impl_retry_attempt) >= 1
        and tonumber(payload.impl_retry_attempt) == math.floor(tonumber(payload.impl_retry_attempt))
        and tonumber(payload.impl_retry_attempt) <= M._max_impl_retry_attempts))
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

return C
