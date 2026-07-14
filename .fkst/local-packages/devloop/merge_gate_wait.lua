local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local parsers_misc = require("devloop.parsers.misc")
local C = {}
local strings = require("contract.strings")
local forge_validators = require("devloop.forge_validators")

local wait_bucket_seconds = 1800

local function wait_bucket(now_seconds)
  local seconds = tonumber(now_seconds) or now()
  return tostring(math.floor(seconds / wait_bucket_seconds))
end

function C.merge_gate_wait_version_lineage(M, version)
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

function C.merge_gate_wait_marker(M, issue_proposal_id, pr_number, version, head_sha, reason, kind)
  if not forge_validators.is_positive_pr_number(pr_number) or not forge_validators.is_git_sha(head_sha) then
    error("github-devloop: invalid merge-gate-wait marker")
  end
  return '<!-- fkst:github-devloop:merge-gate-wait:v1 proposal="' .. tostring(issue_proposal_id)
    .. '" pr="' .. tostring(pr_number)
    .. '" version="' .. tostring(version)
    .. '" head_sha="' .. tostring(head_sha)
    .. '" kind="' .. tostring(strings.sanitize_key(kind or "CI_WAIT", false):gsub("/", "-"))
    .. '" reason="' .. tostring(strings.sanitize_key(reason or "ci-wait", false):gsub("/", "-"))
    .. '" -->'
end

function C.build_merge_gate_wait_comment_request(M, repo, merge_ready, reason, kind, source_ref)
  local safe_reason = tostring(strings.sanitize_key(reason or "ci-wait", false):gsub("/", "-"))
  local wait_version = C.merge_gate_wait_version_lineage(M, merge_ready.version)
  local marker = C.merge_gate_wait_marker(M,
    merge_ready.proposal_id,
    merge_ready.pr_number,
    wait_version,
    merge_ready.reviewed_head_sha,
    safe_reason,
    kind
  )
  return entity_lib.build_entity_comment_request({
    kind = "pr",
    repo = repo,
    number = merge_ready.pr_number,
  }, "github-devloop merge gate wait: " .. safe_reason
    .. "\n\n" .. marker, base_ids.dedup_key({
    "merge",
    "wait",
    tostring(merge_ready.proposal_id),
    tostring(wait_version),
    tostring(merge_ready.reviewed_head_sha),
    safe_reason,
    wait_bucket(now()),
  }), source_ref)
end

function C.merge_gate_wait_fact(M, comments, issue_proposal_id, issue_version, pr_number, head_sha)
  if type(comments) ~= "table" then
    return nil
  end
  local wait_version = C.merge_gate_wait_version_lineage(M, issue_version)
  local marker_pattern = "<!%-%- fkst:github%-devloop:merge%-gate%-wait:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_issue = marker:match('proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      local marker_head_sha = marker:match('head_sha="([^"]+)"')
      local marker_kind = marker:match('kind="([^"]+)"')
      local marker_reason = marker:match('reason="([^"]+)"')
      if marker_issue == tostring(issue_proposal_id)
        and tostring(marker_pr) == tostring(pr_number)
        and marker_version == tostring(wait_version)
        and tostring(marker_head_sha) == tostring(head_sha)
        and forge_validators.is_git_sha(marker_head_sha)
        and strings.is_bounded_string(marker_kind, M._max_key_len)
        and strings.is_bounded_string(marker_reason, M._max_key_len) then
        return {
          proposal_id = marker_issue,
          pr_number = tonumber(marker_pr),
          version = marker_version,
          head_sha = marker_head_sha,
          kind = marker_kind,
          reason = marker_reason,
          comment_created_at = parsers_misc._comment_created_at(M, comment),
        }
      end
    end
  end
  return nil
end

return C
