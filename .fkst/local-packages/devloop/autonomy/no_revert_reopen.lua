local contract_time = require("contract.time")

local no_revert_reopen = {}

local no_revert_reopen_window_days = 7
local no_revert_reopen_window_seconds = no_revert_reopen_window_days * 24 * 60 * 60
local scan_schema = "github-devloop.no-revert-reopen-scan.v1"

local function positive_number(value)
  local parsed = tonumber(value)
  if parsed == nil or parsed < 1 or parsed ~= math.floor(parsed) then
    return nil
  end
  return parsed
end

local function normalize_text(value)
  return tostring(value or ""):lower():gsub("%s+", " ")
end

local function title_or_body_reverts_pr(pr, target_number)
  local number = positive_number(target_number)
  if number == nil then
    return false
  end
  local text = normalize_text(tostring(pr and pr.title or "") .. "\n" .. tostring(pr and pr.body or ""))
  if text:find("revert", 1, true) == nil then
    return false
  end
  local escaped = tostring(number):gsub("([^%w])", "%%%1")
  local suffix = "%f[%D]"
  return text:find("#" .. escaped .. suffix) ~= nil
    or text:find("pull/" .. escaped .. suffix) ~= nil
    or text:find("pull request " .. escaped .. suffix) ~= nil
    or text:find("pr " .. escaped .. suffix) ~= nil
end

local function first_timestamp_seconds(...)
  for index = 1, select("#", ...) do
    local value = select(index, ...)
    local parsed = type(value) == "number" and value or contract_time.iso_timestamp_epoch_seconds(value)
    if parsed ~= nil then
      return parsed
    end
  end
  return nil
end

local function is_true(value)
  return value == true or tostring(value or ""):lower() == "true"
end

local function scan_timestamp(scan, ...)
  for index = 1, select("#", ...) do
    local key = select(index, ...)
    local parsed = first_timestamp_seconds(scan and scan[key])
    if parsed ~= nil then
      return parsed
    end
  end
  return nil
end

local function merge_seconds(fact, opts)
  local options = opts or {}
  local pr = options.merged_pr or options.pr or {}
  return first_timestamp_seconds(
    options.merged_at,
    pr.merged_at,
    pr.mergedAt,
    type(fact) == "table" and fact.merged_at or nil,
    type(fact) == "table" and fact.mergedAt or nil,
    type(fact) == "table" and fact.comment_created_at or nil,
    options.comment_created_at
  )
end

local function evidence_seconds(source)
  return first_timestamp_seconds(
    source and source.merged_at,
    source and source.mergedAt,
    source and source.committed_at,
    source and source.committedAt,
    source and source.authored_at,
    source and source.authoredAt,
    source and source.pushed_at,
    source and source.pushedAt,
    source and source.reopened_at,
    source and source.reopenedAt,
    source and source.updated_at,
    source and source.updatedAt,
    source and source.closed_at,
    source and source.closedAt
  )
end

local function evidence_within_window(fact, opts, source)
  local merged = merge_seconds(fact, opts)
  local evidence = evidence_seconds(source)
  if merged == nil or evidence == nil then
    return true
  end
  if evidence < merged then
    return false
  end
  return evidence <= merged + no_revert_reopen_window_seconds
end

local function append_pair(pairs, seen, pair)
  local key = tostring(pair.reverted_pr or "")
    .. "->"
    .. tostring(pair.revert_pr or pair.issue_number or pair.revert_commit or "")
    .. ":"
    .. tostring(pair.evidence or "")
  if seen[key] then
    return
  end
  seen[key] = true
  table.insert(pairs, pair)
end

local function issue_was_reopened(issue)
  if type(issue) ~= "table" then
    return false
  end
  return tostring(issue.state_reason or issue.stateReason or ""):upper() == "REOPENED"
    or issue.reopened == true
    or issue.issue_reopened == true
end

local function issue_from_entity(entity)
  if type(entity) ~= "table" then
    return nil
  end
  if type(entity.parent_issue) == "table" then
    return entity.parent_issue
  end
  if type(entity.issue) == "table" then
    return entity.issue
  end
  return entity
end

local function source_list(...)
  local values = {}
  for index = 1, select("#", ...) do
    local source = select(index, ...)
    if type(source) == "table" then
      for _, value in ipairs(source) do
        table.insert(values, value)
      end
    end
  end
  return values
end

local function commit_reverts_pr(commit, target_number)
  local number = positive_number(target_number)
  if number == nil or type(commit) ~= "table" then
    return false
  end
  local reverted = positive_number(commit.reverted_pr or commit.reverted_pr_number or commit.target_pr or commit.target_pr_number)
  if reverted ~= nil then
    return reverted == number
  end
  return title_or_body_reverts_pr({
    title = commit.message_head or commit.subject or commit.title,
    body = commit.message_body or commit.body or commit.message,
  }, number)
end

local function commit_identity(commit)
  return tostring(commit and (commit.sha or commit.oid or commit.commit_sha or commit.revert_commit or commit.id) or "")
end

local function pr_source_contains_target(fact, opts)
  local pr_number = positive_number(type(fact) == "table" and (fact.pr_number or fact.pr) or nil)
  if pr_number == nil or type(opts) ~= "table" or type(opts.recent_merged_prs) ~= "table" then
    return false
  end
  for _, pr in ipairs(opts.recent_merged_prs) do
    if positive_number(pr and (pr.number or pr.pr_number)) == pr_number then
      return true
    end
  end
  return false
end

local function issue_source_matches(fact, source)
  local issue_number = positive_number(type(fact) == "table" and (fact.issue_number or fact.issue) or nil)
  if issue_number == nil or type(source) ~= "table" then
    return false
  end
  local issue = issue_from_entity(source)
  local candidate_issue = positive_number(issue and (issue.number or issue.issue_number))
    or positive_number(source.issue_number)
  local candidate_pr = positive_number(source.pr_number or source.pr)
  local fact_pr = positive_number(type(fact) == "table" and (fact.pr_number or fact.pr) or nil)
  return candidate_issue == issue_number or (fact_pr ~= nil and candidate_pr == fact_pr)
end

local function issue_source_contains_target(fact, opts)
  if type(opts) ~= "table" then
    return false
  end
  if issue_source_matches(fact, opts.issue) or issue_source_matches(fact, opts.parent_issue) then
    return true
  end
  for _, entity in ipairs(opts.entities or {}) do
    if issue_source_matches(fact, entity) then
      return true
    end
  end
  for _, issue in ipairs(opts.recent_merged_issues or {}) do
    if issue_source_matches(fact, issue) then
      return true
    end
  end
  return false
end

local function sources_complete(fact, opts)
  local options = opts or {}
  return pr_source_contains_target(fact, options) and issue_source_contains_target(fact, options)
end

local function full_window_scan_covers(fact, opts)
  local options = opts or {}
  local scan = options.no_revert_reopen_scan
    or options.scan
    or (type(fact) == "table" and fact.no_revert_reopen_scan or nil)
  if type(scan) ~= "table" or tostring(scan.schema or "") ~= scan_schema then
    return false
  end
  if not is_true(scan.pr_reverts_complete)
    or not is_true(scan.revert_commits_complete)
    or not is_true(scan.issue_reopens_complete) then
    return false
  end
  local merged = merge_seconds(fact, options)
  if merged == nil then
    return false
  end
  local since = scan_timestamp(scan, "since_seconds", "since_at", "since")
  local until_value = scan_timestamp(scan, "until_seconds", "until_at", "until")
  if since == nil or until_value == nil then
    return false
  end
  return since <= merged and until_value >= merged + no_revert_reopen_window_seconds
end

local function scan_from(fact, opts)
  local options = opts or {}
  local scan = options.no_revert_reopen_scan
    or options.scan
    or (type(fact) == "table" and fact.no_revert_reopen_scan or nil)
  return type(scan) == "table" and scan or nil
end

local function evidence_pairs(fact, opts)
  local options = opts or {}
  local scan = scan_from(fact, options)
  local pairs = {}
  local seen = {}
  local pr_number = positive_number(type(fact) == "table" and (fact.pr_number or fact.pr) or nil)
  local issue_number = positive_number(type(fact) == "table" and (fact.issue_number or fact.issue) or nil)
  if pr_number == nil then
    return pairs
  end
  for _, pr in ipairs(options.recent_merged_prs or {}) do
    local revert_number = positive_number(pr and (pr.number or pr.pr_number))
    if revert_number ~= nil
      and revert_number ~= pr_number
      and title_or_body_reverts_pr(pr, pr_number)
      and evidence_within_window(fact, options, pr) then
      append_pair(pairs, seen, {
        reverted_pr = pr_number,
        revert_pr = revert_number,
        evidence = "explicit-revert-pr",
      })
    end
  end

  for _, commit in ipairs(source_list(
    scan and scan.revert_commits or nil,
    options.revert_commits,
    options.recent_revert_commits
  )) do
    if commit_reverts_pr(commit, pr_number) and evidence_within_window(fact, options, commit) then
      append_pair(pairs, seen, {
        reverted_pr = pr_number,
        revert_commit = commit_identity(commit),
        evidence = "revert-commit",
      })
    end
  end

  local function append_reopen_if_matching(candidate)
    local issue = issue_from_entity(candidate)
    local candidate_issue = positive_number(issue and (issue.number or issue.issue_number))
      or positive_number(candidate and candidate.issue_number)
    local candidate_pr = positive_number(candidate and (candidate.pr_number or candidate.pr))
    if issue_was_reopened(issue)
      and issue_number ~= nil
      and (candidate_issue == issue_number or candidate_pr == pr_number)
      and evidence_within_window(fact, options, issue) then
      append_pair(pairs, seen, {
        reverted_pr = pr_number,
        issue_number = issue_number,
        evidence = "issue-reopened",
      })
    end
  end

  append_reopen_if_matching(options.issue)
  append_reopen_if_matching(options.parent_issue)
  for _, entity in ipairs(options.entities or {}) do
    append_reopen_if_matching(entity)
  end
  for _, issue in ipairs(options.recent_merged_issues or {}) do
    append_reopen_if_matching(issue)
  end
  return pairs
end

function no_revert_reopen.window_seconds()
  return no_revert_reopen_window_seconds
end

function no_revert_reopen.evidence(fact, opts)
  return evidence_pairs(fact, opts)
end

function no_revert_reopen.gate(fact, opts)
  local options = opts or {}
  local pairs = evidence_pairs(fact, options)
  if #pairs > 0 then
    return "fail", tostring(pairs[1].evidence or "revert-or-reopen"), pairs
  end
  if not sources_complete(fact, options) then
    return "pending", "missing-source-scan", pairs
  end
  local merged = merge_seconds(fact, options)
  local current = tonumber(options.now_seconds)
  if merged == nil then
    return "pending", "missing-merge-timestamp", pairs
  end
  if current == nil then
    return "pending", "missing-now", pairs
  end
  if current < merged + no_revert_reopen_window_seconds then
    return "pending", "window-open", pairs
  end
  if not full_window_scan_covers(fact, options) then
    return "pending", "missing-full-window-scan", pairs
  end
  return "pass", "window-elapsed", pairs
end

return no_revert_reopen
