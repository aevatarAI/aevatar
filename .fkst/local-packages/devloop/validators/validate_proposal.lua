local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local source_refs = require("contract.source_ref")
local convergence_shared = require("devloop.convergence.shared")
local C = {}
function C.is_intake_hand_off(hand_off, proposal)
  if type(hand_off) ~= "table" or type(proposal) ~= "table" then
    return false
  end
  return hand_off.kind == "own-intake-decision"
    and hand_off.proposal_id == proposal.proposal_id
    and hand_off.decision == "enable"
    and hand_off.dedup_key == proposal.dedup_key
    and source_refs.has_bounded_source_ref(hand_off.source_ref, devloop_base._max_key_len)
    and type(proposal.source_ref) == "table"
    and tostring(hand_off.source_ref.kind or "") == tostring(proposal.source_ref.kind or "")
    and tostring(hand_off.source_ref.ref or "") == tostring(proposal.source_ref.ref or "")
end

function C.validate_proposal(proposal)
  if type(proposal) ~= "table" then
    return false
  end
  if proposal.schema ~= "consensus.proposal.v1" then
    return false
  end
  local repo, issue_number = base_ids.parse_proposal_id(proposal.proposal_id)
  if repo == nil or issue_number == nil then
    local review_repo, pr_number = devloop_base.parse_pr_review_proposal_id(proposal.proposal_id)
    if review_repo == nil or pr_number == nil then
      return false
    end
    if not strings.is_path_safe_key(proposal.proposal_id, devloop_base._max_key_len)
      or devloop_base.canonical_pr_review_proposal_dedup_for_proposal(
        proposal.dedup_key,
        proposal.proposal_id
      ) == nil then
      return false
    end
  else
    if not devloop_base.is_safe_proposal_ref(proposal.proposal_id, proposal.dedup_key) then
      return false
    end
  end
  if not strings.is_bounded_string(proposal.title, devloop_base._max_title_len) then
    return false
  end
  if not strings.is_bounded_string(proposal.body, devloop_base._max_body_len) then
    return false
  end
  if proposal.content_fetch ~= nil and not strings.is_bounded_string(proposal.content_fetch, 4000) then
    return false
  end
  if not source_refs.has_bounded_source_ref(proposal.source_ref, devloop_base._max_key_len) then
    return false
  end
  if proposal.effect_version ~= nil and not strings.is_bounded_string(proposal.effect_version, devloop_base._max_dedup_len) then
    return false
  end
  if proposal.findings_record ~= nil and not strings.is_bounded_string(proposal.findings_record, convergence_shared.findings_record_len) then
    return false
  end
  return proposal.intake_hand_off == nil or C.is_intake_hand_off(proposal.intake_hand_off, proposal)
end

return C
