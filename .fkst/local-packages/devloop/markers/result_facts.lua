local devloop_base = require("devloop.base")
local parsers_misc = require("devloop.parsers.misc")
local shared = require("devloop.markers.shared")

local C = {}
local marker_attr = shared.marker_attr

function C.first_review_result_fact(comments, review_proposal_id, issue_proposal_id)
  if type(comments) ~= "table" then return nil end
  local marker_pattern = "<!%-%- fkst:github%-devloop:review%-result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local marker_proposal = marker_attr(marker, "proposal")
      local marker_dedup = devloop_base.canonical_pr_review_consensus_dedup_for_proposal(
        marker_attr(marker, "dedup"), marker_proposal)
      local decision = marker_attr(marker, "decision")
      if marker_proposal == tostring(review_proposal_id)
        and marker_attr(marker, "issue_proposal") == tostring(issue_proposal_id)
        and marker_dedup ~= nil
        and (decision == "approve" or decision == "reject") then
        return { decision = decision, dedup_key = marker_dedup }
      end
    end
  end
  return nil
end

function C.first_result_fact(comments, proposal_id, logical_identity)
  if type(comments) ~= "table" then return nil end
  local marker_pattern = "<!%-%- fkst:github%-devloop:result:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local decision = marker_attr(marker, "decision")
      local marker_identity = marker_attr(marker, "lineage") or marker_attr(marker, "dedup")
      if marker_attr(marker, "proposal") == tostring(proposal_id)
        and marker_identity == tostring(logical_identity)
        and (decision == "approve" or decision == "reject") then
        return { decision = decision, dedup_key = marker_attr(marker, "dedup") }
      end
    end
  end
  return nil
end

return C
