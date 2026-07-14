local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local m_claims = require("devloop.claims")
local parsers_misc = require("devloop.parsers.misc")
local parsers_pr = require("devloop.parsers.pr")
local C = {}
local strings = require("contract.strings")
local forge_validators = require("devloop.forge_validators")


local function pr_source_ref(repo, pr_number)
  return {
    kind = "external",
    ref = tostring(repo) .. "#pr/" .. tostring(pr_number),
  }
end

function C.pr_source_ref(repo, pr_number)
  return pr_source_ref(repo, pr_number)
end

function C.issue_source_ref(repo, issue_number)
  return {
    kind = "external",
    ref = tostring(repo) .. "#issue/" .. tostring(issue_number),
  }
end

function C.build_entity_comment_request(target, body, dedup_key, source_ref, opts)
  if type(target) ~= "table" then
    error("github-devloop: invalid entity comment target")
  end
  local request = {
    schema = "github-proxy.v1",
    repo = target.repo,
    body = body,
    dedup_key = dedup_key,
    source_ref = base_ids.normalize_source_ref(source_ref),
  }
  if type(opts) == "table" and opts.replace_marker ~= nil then
    request.replace_marker = tostring(opts.replace_marker)
  end
  if target.kind == "issue" then
    request.issue_number = target.number
    m_claims.attach_issue_claim(request, request.source_ref)
  elseif target.kind == "pr" then
    request.pr_number = target.number
  else
    error("github-devloop: invalid entity comment target kind")
  end
  return request
end

function C.current_entity_state(M, entity_comments, proposal_id)
  return M.current_state(entity_comments, proposal_id)
end

local function command_indicates_not_found(result)
  local stderr = tostring(result and result.stderr or ""):lower()
  return stderr:find("404", 1, true) ~= nil
    or stderr:find("not found", 1, true) ~= nil
end

local function linked_pr_numbers(M, issue_comments, proposal_id)
  local numbers = {}
  local seen = {}
  local marker_pattern = "<!%-%- fkst:github%-devloop:pr%-link:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, issue_comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_pr = marker:match('pr="([^"]+)"')
      local marker_branch = marker:match('branch="([^"]+)"')
      local marker_impl_version = marker:match('impl_version="([^"]*)"')
      local marker_base_branch = marker:match('base_branch="([^"]+)"')
      if marker_proposal == proposal_id
        and forge_validators.is_positive_pr_number(marker_pr)
        and forge_validators.is_git_ref_safe(marker_branch)
        and strings.is_bounded_string(marker_impl_version, M._max_dedup_len)
        and forge_validators.is_git_ref_safe(marker_base_branch)
        and not seen[tostring(marker_pr)] then
        seen[tostring(marker_pr)] = true
        table.insert(numbers, tonumber(marker_pr))
      end
    end
  end
  return numbers
end

function C.linked_pr_surface_snapshot(M, repo, proposal_id, issue_comments, opts)
  local options = opts or {}
  local snapshot = {
    comments = issue_comments or {},
    prs = {},
    absent_prs = {},
    deferred = false,
    defer_reason = nil,
  }
  for _, pr_number in ipairs(linked_pr_numbers(M, issue_comments, proposal_id)) do
    local pr_view
    if options.cache_only == true then
      pr_view = M.cached_entity_view(repo, "pr", pr_number)
      if pr_view == nil then
        snapshot.deferred = true
        snapshot.defer_reason = "pr-surface-not-cached"
        return snapshot
      end
    else
      pr_view = M.gh_pr_view_observe(repo, pr_number, 30)
    end
    if pr_view.exit_code ~= 0 then
      if command_indicates_not_found(pr_view) then
        snapshot.absent_prs[tostring(pr_number)] = true
      else
        error("github-devloop: linked PR state view failed: " .. tostring(pr_view.stderr))
      end
    else
      local current_pr = parsers_pr.parse_pr_view_origin(M, pr_view.stdout)
      if type(current_pr.comments) ~= "table" or tostring(current_pr.state or "") == "" then
        error("github-devloop: linked PR state view malformed")
      end
      table.insert(snapshot.prs, {
        number = pr_number,
        current = current_pr,
      })
    end
  end
  snapshot.fetch_before_compare = {
    ["pr-head"] = true,
  }
  return snapshot
end

function C.pr_proposal_id(repo, pr_number)
  if not require("devloop.pr_safety").is_safe_pr_number(pr_number) then
    error("github-devloop: invalid PR proposal number")
  end
  local safe_repo = strings.sanitize_key(repo, false)
  if safe_repo == nil or safe_repo == "" then
    error("github-devloop: invalid PR proposal repo")
  end
  return "github-devloop/pr/" .. safe_repo .. "/" .. tostring(pr_number)
end

function C.parse_pr_proposal_id(proposal_id)
  local repo_part, number = tostring(proposal_id or ""):match("^github%-devloop/pr/(.+)/(%d+)$")
  if repo_part == nil or not require("devloop.pr_safety").is_safe_pr_number(number) then
    return nil, nil
  end
  return repo_part, tonumber(number)
end

function C.pr_transition_lock_key(repo, pr_number)
  return "github-devloop/transition/" .. strings.sanitize_key(repo, false) .. "/pr/" .. tostring(pr_number)
end

function C.merge_lane_lock_key(repo)
  return "github-devloop/merge-lane/" .. strings.sanitize_key(repo, false)
end

function C.parse_entity_proposal_id(proposal_id)
  local repo, issue_number = base_ids.parse_proposal_id(proposal_id)
  if repo ~= nil then
    return {
      kind = "issue",
      repo = repo,
      issue_number = issue_number,
      number = issue_number,
      proposal_id = proposal_id,
    }
  end
  local pr_repo, pr_number = C.parse_pr_proposal_id(proposal_id)
  if pr_repo ~= nil then
    return {
      kind = "pr",
      repo = pr_repo,
      pr_number = pr_number,
      number = pr_number,
      proposal_id = proposal_id,
    }
  end
  return nil
end

function C.is_safe_entity_proposal_ref(proposal_id, dedup_key)
  local entity = C.parse_entity_proposal_id(proposal_id)
  if entity == nil then
    return false
  end
  if entity.kind == "issue" then
    return devloop_base.is_safe_proposal_ref(proposal_id, dedup_key)
  end
  return strings.is_path_safe_key(proposal_id, devloop_base._max_key_len)
    and strings.is_path_safe_key(dedup_key, devloop_base._max_dedup_len)
end

function C.transition_lock_key(proposal_id)
  local lock = require("devloop.base").transition_lock_key(proposal_id)
  if lock ~= nil then
    return lock
  end
  local repo, pr_number = C.parse_pr_proposal_id(proposal_id)
  if repo == nil then
    return nil
  end
  return C.pr_transition_lock_key(repo, pr_number)
end

function C.observe_lock_key(repo, number, kind)
  if kind == "pr" then
    return C.pr_transition_lock_key(repo, number)
  end
  return require("devloop.base").observe_lock_key(repo, number)
end

function C.result_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.review_result_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.review_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.loop_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.implement_lock_key(proposal_id)
  return C.transition_lock_key(proposal_id)
end

function C.pr_native_origin(repo, pr_number, pr)
  return {
    proposal_id = C.pr_proposal_id(repo, pr_number),
    repo = repo,
    issue_number = nil,
    branch = pr.head_ref_name,
    impl_version = pr.updated_at or pr.updatedAt or pr.head_sha or "pr/" .. tostring(pr_number),
    base_branch = pr.base_ref_name,
    pr_native = true,
  }
end

return C
