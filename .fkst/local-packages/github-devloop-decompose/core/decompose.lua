local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local S = {}
local strings = require("contract.strings")
local decimal_checksum = strings.decimal_checksum
local decompose_lib = require("devloop.decompose")
local comment_strings = require("devloop.strings")

function S.install(M)
local max_decompose_issues = decompose_lib.max_decompose_issues(M)
local fallback_title = "Rework blocked PR with a smaller or alternative approach"

local function bounded_text(value, limit, fallback)
  local text = tostring(value or "")
  if text == "" then
    text = fallback or "(empty)"
  end
  if #text > limit then
    text = base_ids.truncate_utf8(text, limit)
  end
  return text
end

local function issue_fingerprint(decompose, index)
  return decimal_checksum(table.concat({
    tostring(decompose.proposal_id or ""),
    tostring(decompose.version or ""),
    tostring(decompose.pr_number or ""),
    tostring(decompose.round or ""),
    tostring(index or ""),
  }, "\n"))
end

function M.parse_decompose_plan(stdout)
  local ok, decoded = pcall(json.decode, stdout or "")
  if not ok or type(decoded) ~= "table" or type(decoded.issues) ~= "table" then
    return nil
  end
  local issues = {}
  for _, issue in ipairs(decoded.issues) do
    if #issues >= max_decompose_issues then
      break
    end
    if type(issue) ~= "table"
      or not strings.is_bounded_string(issue.title, M._max_title_len)
      or not strings.is_bounded_string(issue.body, M._max_body_len) then
      return nil
    end
    table.insert(issues, {
      title = bounded_text(issue.title, M._max_title_len, fallback_title),
      body = bounded_text(issue.body, M._max_body_len, "Define a smaller independently-completable follow-up."),
    })
  end
  if #issues < 1 then
    return nil
  end
  return issues
end

function M.fallback_decompose_plan(decompose)
  return {
    {
      title = "Rework blocked PR #" .. tostring(decompose.pr_number) .. " with a smaller or alternative approach",
      body = "The parent PR repeatedly failed review after " .. tostring(decompose.round)
        .. " fix rounds. Rework it as a smaller independently-completable issue, or choose an alternative implementation approach that avoids repeating the same failed fix path.",
    },
  }
end

function M.decomposed_comment_body(decompose, count)
  return comment_strings.comment_string(M, "decomposed_prefix") .. tostring(count) .. comment_strings.comment_string(M, "decomposed_suffix")
    .. "\n\n" .. decompose_lib.decomposed_marker(M, decompose.proposal_id, decompose.version, decompose.pr_number, count)
end

function M.build_issue_create_request(repo, decompose, issue, index)
  local safe_title = bounded_text(issue.title, M._max_title_len, fallback_title)
  local parent_summary = "Parent issue: #" .. tostring(select(2, base_ids.parse_proposal_id(decompose.proposal_id)) or "unknown")
    .. "\nParent PR: #" .. tostring(decompose.pr_number)
    .. "\nBlocked reason: fix loop reached " .. tostring(decompose.round) .. " rounds and was reconciled to blocked."
  local body = parent_summary
    .. "\n\nSmaller scope / alternative approach:\n" .. devloop_base.neutralize_untrusted_comment_text(issue.body)
    .. "\n\nNon-goals:\n- Do not repeat the same high-round fix path without reducing scope or changing approach."
    .. "\n\nAcceptance:\n- The work is independently reviewable."
    .. "\n- The implementation can pass the normal intake, consensus, implementation, and review pipeline."
    .. "\n\n" .. decompose_lib.decompose_lineage_marker(M, decompose.proposal_id, decompose_lib.decompose_lineage_depth(M, decompose.current_issue_body) + 1)
    .. "\n\n" .. decompose_lib.decompose_child_marker(M, decompose.proposal_id, decompose.version, decompose.pr_number, index)
  if #body > M._max_body_len then
    body = base_ids.truncate_utf8(body, M._max_body_len)
  end
  local fingerprint = issue_fingerprint(decompose, index)
  return {
    schema = "github-proxy.issue-create.v1",
    repo = repo,
    title = safe_title,
    body = body,
    labels = json.decode("[]"),
    dedup_key = base_ids.dedup_key({
      "decompose",
      tostring(decompose.proposal_id),
      tostring(decompose.version),
      tostring(index),
      fingerprint,
    }),
    parent_comment_target = {
      repo = repo,
      pr_number = decompose.pr_number,
    },
    source_ref = base_ids.normalize_source_ref(decompose.source_ref),
  }
end

end

return S
