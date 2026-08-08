local base_ids = require("devloop.base_ids")
local forge_validators = require("devloop.forge_validators")
local m_claims = require("devloop.claims")
local pr_safety = require("devloop.pr_safety")
local parsers_misc = require("devloop.parsers.misc")
local common = require("departments.observability.common")
local strings = require("contract.strings")
local decompose_lib = require("devloop.decompose")
local config = require("devloop.config")
local m_facts = require("devloop.markers.facts")
local m_builders = require("devloop.markers.builders")
local devloop_entity_view = require("devloop.github_proxy_entity_view")
local devloop_state = require("devloop.state")

local M = {}

function M.install_reaper(core)
local dept = common.dept
local max_reap_reason_len = common.max_reap_reason_len

local function reaper_body_path(repo, pr_number, proposal_id)
  local safe_repo = base_ids.safe_repo(repo):gsub("[/%s]+", "-")
  local safe_issue = strings.sanitize_key(tostring(proposal_id or "unknown"), false):gsub("[/%s]+", "-")
  local identity = safe_repo .. "-pr-" .. tostring(pr_number) .. "-" .. safe_issue
  identity = identity:gsub("[^%w%._%-]", "-"):gsub("%-+", "-"):gsub("^%-+", ""):gsub("%-+$", "")
  if identity == "" then
    identity = "orphan-pr"
  end
  if #identity > 180 then
    identity = identity:sub(1, 180):gsub("%-+$", "")
  end
  return "/tmp/fkst-github-devloop-reap-" .. identity .. ".md"
end

local function orphan_reap_log_line(repo, pr_number, proposal_id, action, reason)
  return table.concat({
    "github-devloop",
    "dept=" .. dept,
    "tag=REAP",
    "repo=" .. tostring(repo or ""),
    "pr=" .. tostring(pr_number or ""),
    "proposal_id=" .. tostring(proposal_id or "unknown"),
    "action=" .. tostring(action or "skip"),
    "reason=" .. tostring(reason or ""),
  }, " ")
end

local function successor_issue_numbers(comments, proposal_id)
  local successors = {}
  local seen = {}
  local dedup_prefix = "decompose/" .. tostring(proposal_id) .. "/"
  local marker_pattern = "<!%-%- fkst:github%-proxy:issue%-created:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments or {})) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local dedup = marker:match('dedup="([^"]+)"')
      local issue = marker:match('issue="([^"]+)"')
      if tostring(dedup or ""):sub(1, #dedup_prefix) == dedup_prefix
        and forge_validators.is_positive_pr_number(issue)
        and not seen[tostring(issue)] then
        seen[tostring(issue)] = true
        table.insert(successors, tonumber(issue))
      end
    end
  end
  table.sort(successors)
  return successors
end

local function successor_summary(successors, fallback_count)
  if #successors == 0 then
    return tostring(fallback_count or 0) .. " successor issue(s)"
  end
  local refs = {}
  for _, issue in ipairs(successors) do
    table.insert(refs, "#" .. tostring(issue))
  end
  return table.concat(refs, ", ")
end

local function terminal_parent_reason(parent_issue, entity)
  local proposal_id = entity.proposal_id
  local pr_comments = entity.pr and entity.pr.comments or {}
  local successors = successor_issue_numbers(pr_comments, proposal_id)
  if tostring(parent_issue and parent_issue.state or ""):upper() == "CLOSED" then
    return {
      code = "parent-closed",
      text = "Parent issue #" .. tostring(select(2, base_ids.parse_proposal_id(proposal_id)) or "unknown") .. " is closed.",
      successors = successors,
    }
  end
  local decomposed = decompose_lib.decomposed_fact(pr_comments, proposal_id)
    or decompose_lib.decomposed_fact(parent_issue and parent_issue.comments or {}, proposal_id)
  if decomposed ~= nil
    and tostring(decomposed.pr_number or "") == tostring(entity.pr_number or "")
    and #successors >= decomposed.count then
    return {
      code = "parent-decomposed",
      text = "Parent issue #"
        .. tostring(select(2, base_ids.parse_proposal_id(proposal_id)) or "unknown")
        .. " has a trusted decomposed marker with successors: "
        .. successor_summary(successors, decomposed.count),
      successors = successors,
    }
  end
  return nil
end

local function reaper_comment_body(proposal_id, pr_number, reason)
  local _repo, issue_number = base_ids.parse_proposal_id(proposal_id)
  local parent_ref = "#" .. tostring(issue_number or "unknown")
  local reason_text = tostring(reason and reason.text or "")
  if #reason_text > max_reap_reason_len then
    reason_text = base_ids.truncate_utf8(reason_text, max_reap_reason_len)
  end
  return "github-devloop reaped this managed PR because its parent issue is terminal.\n\n"
    .. "Parent: " .. parent_ref .. "\n"
    .. "Reason: " .. reason_text .. "\n"
    .. "Successors: " .. successor_summary(reason and reason.successors or {}, nil) .. "\n"
    .. "Branch cleanup is intentionally left to a separate manual or managed path.\n\n"
    .. m_builders.orphan_reaped_marker(proposal_id, pr_number, reason and reason.code or "parent-terminal")
    .. "\n"
end

local function reap_orphan_pr(repo, entity)
  if entity == nil or entity.pr_origin == nil or entity.pr == nil then
    return
  end
  local origin = entity.pr_origin
  local proposal_id = origin.proposal_id
  local pr_number = entity.pr_number
  if not require("devloop.pr_safety").is_devloop_issue_branch(origin.branch) then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "skip", "non-devloop-branch"))
    return
  end
  if tostring(entity.pr.head_ref_name or "") ~= tostring(origin.branch or "") then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "skip", "branch-mismatch"))
    return
  end
  if tostring(entity.pr.state or ""):upper() ~= "OPEN" then
    return
  end
  if m_facts.has_orphan_reaped_marker(entity.pr.comments, proposal_id, pr_number) then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "skip-idempotent", "orphan-reaped-marker-visible"))
    return
  end

  local parent = entity.parent_issue or common.fetch_issue(core, repo, origin.issue_number, entity.observability_limits, entity.observability_deadline)
  if parent == nil then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "deferred", "deadline-parent-fetch"))
    return
  end
  local parent_state = devloop_state.current_state(parent.comments, proposal_id)
  local reason = terminal_parent_reason(parent, entity)
  if reason == nil then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "skip", "parent-active"))
    return
  end
  if reason.code ~= "parent-decomposed"
    and tostring(parent.state or ""):upper() ~= "CLOSED"
    and parent_state.state ~= "blocked"
    and parent_state.state ~= "impl-failed"
    and parent_state.state ~= "merged" then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "skip", "parent-marker-not-terminal"))
    return
  end

  if not m_claims.verify_pr_review_issue_claim(dept, repo, origin.issue_number, parent, proposal_id) then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "skip", "backing-issue-not-self-owned"))
    return
  end

  if config.write_mode() ~= "real" then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "dry-run", reason.code))
    return
  end

  local closed = core.observability_run_cmd({
    run = function(timeout)
      return core.gh_pr_close(repo, pr_number, timeout)
    end,
  }, entity.observability_limits, entity.observability_deadline, "orphan PR close")
  if core.observability_result_deferred(closed) then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "deferred", "deadline"))
    return
  end
  devloop_entity_view.invalidate_entity_after_write(repo, "pr", pr_number)
  local path = reaper_body_path(repo, pr_number, proposal_id)
  local body = core.with_github_debug_stamp(reaper_comment_body(proposal_id, pr_number, reason), {
    emitter = "github-devloop.observability.reaper",
    target = "pr:" .. tostring(repo) .. "#" .. tostring(pr_number),
    dedup_key = proposal_id,
    context = reason and reason.code,
  })
  file.write(path, body)
  local commented = core.observability_run_cmd({
    run = function(timeout)
      return core.gh_pr_comment(repo, pr_number, path, timeout)
    end,
  }, entity.observability_limits, entity.observability_deadline, "orphan PR reaper comment")
  if core.observability_result_deferred(commented) then
    log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "deferred", "deadline-after-close"))
    return
  end
  devloop_entity_view.invalidate_entity_after_write(repo, "pr", pr_number)
  log.info(orphan_reap_log_line(repo, pr_number, proposal_id, "closed", reason.code))
end

function core.reap_orphan_prs(repo, entities)
  for _, entity in ipairs(entities or {}) do
    reap_orphan_pr(repo, entity)
  end
end
end

return M
