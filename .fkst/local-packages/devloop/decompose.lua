local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local parsers_misc = require("devloop.parsers.misc")
local payloads_builders = require("devloop.payloads.builders")
local C = {}
local source_refs = require("contract.source_ref")
local forge_validators = require("devloop.forge_validators")

local max_decompose_issues = 3
local max_decompose_depth = 1

function C.is_supported_decompose(M, payload)
  if type(payload) ~= "table" then
    return false
  end
  local repo, issue_number = base_ids.parse_proposal_id(payload.proposal_id)
  local has_review_binding = payload.review_proposal_id ~= nil
    or payload.review_dedup_key ~= nil
    or payload.head_sha ~= nil
  local valid_review_binding = not has_review_binding
    or (strings.is_path_safe_key(payload.review_proposal_id, M._max_key_len)
      and strings.is_bounded_string(payload.review_dedup_key, M._max_dedup_len)
      and forge_validators.is_git_sha(payload.head_sha))
  local forward_dedup = base_ids.dedup_key({
    "decompose",
    tostring(payload.proposal_id),
    tostring(payload.version),
  })
  local replay_dedup = base_ids.dedup_key({
    "decompose",
    "replay",
    tostring(payload.proposal_id),
    tostring(payload.version),
    tostring(payload.pr_number),
    tostring(payload.expected_child_count or "unknown"),
    tostring(payload.completed_child_count or "unknown"),
  })
  local has_replay_counts = payload.expected_child_count ~= nil or payload.completed_child_count ~= nil
  local valid_replay_counts = not has_replay_counts
    or (tonumber(payload.expected_child_count) ~= nil
      and tonumber(payload.completed_child_count) ~= nil
      and tonumber(payload.expected_child_count) >= 1
      and tonumber(payload.expected_child_count) <= max_decompose_issues
      and tonumber(payload.completed_child_count) >= 0
      and tonumber(payload.completed_child_count) < tonumber(payload.expected_child_count)
      and tonumber(payload.expected_child_count) % 1 == 0
      and tonumber(payload.completed_child_count) % 1 == 0)
  return payload.schema == "github-devloop.decompose.v1"
    and repo ~= nil
    and issue_number ~= nil
    and strings.is_path_safe_key(payload.proposal_id, M._max_key_len)
    and forge_validators.is_positive_pr_number(payload.pr_number)
    and strings.is_bounded_string(payload.version, M._max_dedup_len)
    and valid_review_binding
    and tonumber(payload.round) ~= nil
    and tonumber(payload.round) == M.version_fix_round(payload.version)
    and strings.is_path_safe_key(payload.dedup_key, M._max_dedup_len)
    and valid_replay_counts
    and ((not has_replay_counts and tostring(payload.dedup_key) == forward_dedup)
      or (has_replay_counts and tostring(payload.dedup_key) == replay_dedup))
    and source_refs.has_bounded_source_ref(payload.source_ref, M._max_key_len)
end

function C.decomposed_marker(M, proposal_id, version, pr_number, count)
  local issue_count = tonumber(count)
  if issue_count == nil or issue_count < 1 or issue_count > max_decompose_issues or issue_count % 1 ~= 0 then
    error("github-devloop: invalid decomposed count")
  end
  if not forge_validators.is_positive_pr_number(pr_number) then
    error("github-devloop: invalid decomposed pr number")
  end
  return '<!-- fkst:github-devloop:decomposed:v1 proposal="' .. tostring(proposal_id)
    .. '" version="' .. tostring(version)
    .. '" pr="' .. tostring(pr_number)
    .. '" count="' .. tostring(issue_count)
    .. '" -->'
end

function C.has_decomposed_marker(M, comments, proposal_id, version, pr_number)
  if type(comments) ~= "table" then
    return false
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:decomposed:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if marker:match('proposal="([^"]+)"') == tostring(proposal_id)
        and marker:match('version="([^"]*)"') == tostring(version)
        and tostring(marker:match('pr="([^"]+)"')) == tostring(pr_number) then
        return true
      end
    end
  end
  return false
end

function C.decomposed_fact(M, comments, proposal_id, version, pr_number)
  if type(comments) ~= "table" then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:decomposed:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments)) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(marker_pattern) do
      if marker:match('proposal="([^"]+)"') == tostring(proposal_id) then
        local marker_version = marker:match('version="([^"]*)"')
        local marker_pr_number = marker:match('pr="([^"]+)"')
        local count = tonumber(marker:match('count="([^"]+)"'))
        if (version == nil or marker_version == tostring(version))
          and (pr_number == nil or tostring(marker_pr_number) == tostring(pr_number))
          and forge_validators.is_positive_pr_number(marker_pr_number)
          and count ~= nil
          and count >= 1
          and count <= max_decompose_issues
          and count % 1 == 0 then
          return {
            proposal_id = tostring(proposal_id),
            version = marker_version,
            pr_number = tonumber(marker_pr_number),
            count = count,
            comment_created_at = parsers_misc._comment_created_at(M, comment),
          }
        end
      end
    end
  end
  return nil
end

function C.parse_decompose_child_issue_list(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local issues = {}
  if type(decoded) ~= "table" then
    return issues
  end
  for _, issue in ipairs(decoded) do
    if type(issue) == "table" then
      local author_login = issue.author_login
      if author_login == nil and type(issue.author) == "table" then
        author_login = issue.author.login
      end
      table.insert(issues, {
        number = issue.number,
        title = issue.title,
        state = issue.state,
        body = tostring(issue.body or ""),
        author_login = author_login,
        url = issue.url,
      })
    end
  end
  return issues
end

function C.decompose_child_issue_fact_indexes(M, issues, proposal_id, version, pr_number)
  local completed = {}
  local child_pattern = "<!%-%- fkst:github%-devloop:decompose%-child:v1.-%-%->"
  for _, issue in ipairs(issues or {}) do
    local body = tostring(type(issue) == "table" and issue.body or "")
    local trusted_child = type(issue) == "table"
      and parsers_misc.comment_author_login(M, issue) == devloop_base.trusted_bot_login()
      and tostring(issue.state or ""):upper() == "OPEN"
    if trusted_child then
      for marker in body:gmatch(child_pattern) do
        if marker:match('parent="([^"]+)"') == tostring(proposal_id)
          and marker:match('version="([^"]*)"') == tostring(version)
          and tostring(marker:match('pr="([^"]+)"')) == tostring(pr_number) then
          local index = tonumber(marker:match('index="([^"]+)"'))
          if index ~= nil and index >= 1 and index <= max_decompose_issues and index % 1 == 0 then
            completed[index] = true
          end
        end
      end
    end
  end
  return completed
end

function C.decompose_child_fact_indexes(M, comments, issues, proposal_id, version, pr_number, dedup_by_index)
  local completed = C.decompose_child_issue_fact_indexes(M, issues, proposal_id, version, pr_number)
  local created_pattern = "<!%-%- fkst:github%-proxy:issue%-created:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(M, comments or {})) do
    for marker in parsers_misc._comment_body(M, comment):gmatch(created_pattern) do
      local dedup = marker:match('dedup="([^"]+)"')
      for index = 1, max_decompose_issues do
        if type(dedup_by_index) == "table"
          and dedup_by_index[index] ~= nil
          and tostring(dedup) == tostring(dedup_by_index[index])
          and not completed[index] then
          completed[index] = true
        end
      end
    end
  end
  return completed
end

local function decompose_child_count(completed)
  local count = 0
  for _, present in pairs(completed or {}) do
    if present then
      count = count + 1
    end
  end
  return count
end

function C.decompose_children_complete(M, comments, issues, proposal_id, version, pr_number, expected_count)
  local count = tonumber(expected_count)
  if count == nil or count < 1 or count > max_decompose_issues or count % 1 ~= 0 then
    return true, 0
  end
  local completed = C.decompose_child_issue_fact_indexes(M, issues, proposal_id, version, pr_number)
  local completed_count = decompose_child_count(completed)
  return completed_count >= count, completed_count
end

function C.build_decompose_replay_payload(M, fact, comments_or_feedback, source_ref, completed_count)
  local feedback = comments_or_feedback
  if type(feedback) == "table" and feedback[1] ~= nil then
    feedback = M.fixing_replay_feedback_fact(comments_or_feedback, fact.proposal_id, fact.version)
  end
  local payload = payloads_builders.build_devloop_decompose_payload(M, {
    proposal_id = fact.proposal_id,
    pr_number = fact.pr_number,
    issue_version = fact.version,
    review_proposal_id = feedback and feedback.review_proposal_id or nil,
    review_dedup_key = feedback and feedback.review_dedup_key or nil,
    head_sha = feedback and feedback.reviewed_head_sha or nil,
    round = M.version_fix_round(fact.version),
    source_ref = source_ref,
  })
  payload.expected_child_count = fact.count
  payload.completed_child_count = tonumber(completed_count) or 0
  payload.dedup_key = base_ids.dedup_key({
    "decompose",
    "replay",
    tostring(fact.proposal_id),
    tostring(fact.version),
    tostring(fact.pr_number),
    tostring(payload.expected_child_count),
    tostring(payload.completed_child_count),
  })
  return payload
end

function C.decompose_child_marker(M, proposal_id, version, pr_number, index)
  return '<!-- fkst:github-devloop:decompose-child:v1 parent="' .. tostring(proposal_id)
    .. '" version="' .. tostring(version)
    .. '" pr="' .. tostring(pr_number)
    .. '" index="' .. tostring(index)
    .. '" -->'
end

function C.decompose_lineage_marker(M, root_proposal_id, depth)
  local n = tonumber(depth)
  if n == nil or n < 0 or n % 1 ~= 0 then
    error("github-devloop: invalid decompose lineage depth")
  end
  return '<!-- fkst:github-devloop:decompose-lineage:v1 root="' .. tostring(root_proposal_id)
    .. '" depth="' .. tostring(n)
    .. '" -->'
end

function C.decompose_lineage_depth(M, body)
  local text = tostring(body or "")
  local marker_pattern = "<!%-%- fkst:github%-devloop:decompose%-lineage:v1.-%-%->"
  local max_depth = 0
  for marker in text:gmatch(marker_pattern) do
    local depth = tonumber(marker:match('depth="(%d+)"'))
    if depth ~= nil and depth > max_depth then
      max_depth = depth
    end
  end
  return max_depth
end

function C.max_decompose_issues(M)
  return max_decompose_issues
end

function C.max_decompose_depth(M)
  return max_decompose_depth
end

return C
