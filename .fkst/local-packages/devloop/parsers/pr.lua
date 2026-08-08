local C = {}
local shared = require("devloop.parsers.shared")
local parsers_misc = require("devloop.parsers.misc")

function C.parse_pr_list_observe(stdout)
  return shared.parse_numbered_list(stdout)
end

function C.parse_pr_list_freshness(stdout)
  local decoded = json.decode(stdout or "[]")
  local prs = {}
  shared.each_paginated_item(decoded, function(pr)
    if type(pr) == "table" and tonumber(pr.number) ~= nil then
      table.insert(prs, {
        number = tonumber(pr.number),
        state = pr.state,
        updated_at = pr.updated_at or pr.updatedAt,
        head_sha = pr.headRefOid or pr.head_ref_oid,
        head_ref_name = pr.headRefName or pr.head_ref_name,
        base_ref_name = pr.baseRefName or pr.base_ref_name,
        is_draft = pr.isDraft or pr.is_draft,
      })
    end
  end)
  return prs
end

function C.parse_pr_list_merge_queue(stdout)
  return C.parse_pr_list_head_base(stdout)
end

function C.parse_pr_list_recent_merged(stdout)
  local decoded = json.decode(stdout or "[]")
  local prs = {}
  if type(decoded) ~= "table" then
    return prs
  end
  shared.each_paginated_item(decoded, function(pr)
    local number = type(pr) == "table" and tonumber(pr.number) or nil
    if number ~= nil then
      table.insert(prs, {
        number = number,
        title = tostring(pr.title or ""),
        merged_at = pr.mergedAt or pr.merged_at,
        head_sha = pr.headRefOid or pr.head_ref_oid,
      })
    end
  end)
  return prs
end

local function repository_name_with_owner(head_repository, head_repository_owner)
  if type(head_repository) == "string" then
    return head_repository
  end
  if type(head_repository) ~= "table" then
    return nil
  end
  if head_repository.nameWithOwner ~= nil and head_repository.nameWithOwner ~= "" then
    return tostring(head_repository.nameWithOwner)
  end
  if head_repository.name_with_owner ~= nil and head_repository.name_with_owner ~= "" then
    return tostring(head_repository.name_with_owner)
  end
  if head_repository.full_name ~= nil and head_repository.full_name ~= "" then
    return tostring(head_repository.full_name)
  end
  local name = head_repository.name
  local owner = nil
  if type(head_repository.owner) == "table" and head_repository.owner.login ~= nil then
    owner = head_repository.owner.login
  elseif type(head_repository_owner) == "table" and head_repository_owner.login ~= nil then
    owner = head_repository_owner.login
  elseif type(head_repository_owner) == "string" then
    owner = head_repository_owner
  end
  if owner ~= nil and name ~= nil then
    return tostring(owner) .. "/" .. tostring(name)
  end
  return nil
end

function C.parse_pr_list_promotions(stdout)
  local decoded = json.decode(stdout or "[]")
  local prs = {}
  if type(decoded) ~= "table" then
    return prs
  end
  shared.each_paginated_item(decoded, function(pr)
    local number = type(pr) == "table" and tonumber(pr.number) or nil
    if number ~= nil then
      local head = type(pr.head) == "table" and pr.head or {}
      local base = type(pr.base) == "table" and pr.base or {}
      table.insert(prs, {
        number = number,
        state = pr.state,
        merged_at = pr.mergedAt or pr.merged_at,
        head_ref_name = pr.headRefName or pr.head_ref_name or head.ref,
        head_sha = pr.headRefOid or pr.head_ref_oid or head.sha,
        head_repository = repository_name_with_owner(head.repo or pr.headRepository, pr.headRepositoryOwner),
        base_ref_name = pr.baseRefName or pr.base_ref_name or base.ref,
      })
    end
  end)
  return prs
end

local function decode_pr_view(value)
  if type(value) == "table" then
    return value
  end
  return json.decode(value or "{}")
end

function C.parse_pr_view_origin(value)
  local decoded = decode_pr_view(value)
  local head_repo = repository_name_with_owner(
    decoded.headRepository or decoded.head_repository,
    decoded.headRepositoryOwner or decoded.head_repository_owner
  )
  local is_cross_repository = decoded.isCrossRepository
  if is_cross_repository == nil then
    is_cross_repository = decoded.is_cross_repository
  end
  return {
    title = decoded.title ~= nil and tostring(decoded.title) or "",
    body = decoded.body ~= nil and tostring(decoded.body) or "",
    head_ref_name = decoded.headRefName or decoded.head_ref_name,
    head_sha = decoded.headRefOid or decoded.head_ref_oid,
    base_ref_name = decoded.baseRefName or decoded.base_ref_name,
    base_ref_oid = decoded.baseRefOid or decoded.base_ref_oid,
    state = decoded.state,
    updated_at = decoded.updatedAt or decoded.updated_at,
    merged_at = decoded.mergedAt or decoded.merged_at,
    merge_commit_sha = type(decoded.mergeCommit or decoded.merge_commit) == "table"
      and (decoded.mergeCommit or decoded.merge_commit).oid
      or decoded.mergeCommitOid
      or decoded.merge_commit_oid
      or decoded.merge_commit_sha,
    labels = shared.label_names(decoded.labels),
    comments = parsers_misc.comments_from_json(decoded.comments),
    head_repository = head_repo,
    is_cross_repository = is_cross_repository,
    mergeable = decoded.mergeable,
    merge_state_status = decoded.mergeStateStatus or decoded.merge_state_status,
  }
end

function C.parse_pr_view_fix(stdout)
  return C.parse_pr_view_origin(stdout)
end

local function status_rollup_entries(value)
  if type(value) ~= "table" then
    return {}
  end
  if type(value.nodes) == "table" then
    return value.nodes
  end
  return value
end

function C.parse_pr_view_merge(value)
  local decoded = decode_pr_view(value)
  local result = C.parse_pr_view_origin(decoded)
  result.is_draft = decoded.isDraft
  if result.is_draft == nil then
    result.is_draft = decoded.is_draft
  end
  result.mergeable = decoded.mergeable
  result.merge_state_status = decoded.mergeStateStatus or decoded.merge_state_status
  result.status_check_rollup_present = decoded.statusCheckRollup ~= nil or decoded.status_check_rollup ~= nil
  result.status_check_rollup = status_rollup_entries(decoded.statusCheckRollup or decoded.status_check_rollup)
  result.merged_at = decoded.mergedAt or decoded.merged_at
  result.labels = shared.label_names(decoded.labels)
  return result
end

function C.parse_pr_list_head_base(stdout)
  local decoded = json.decode(stdout or "[]")
  local prs = {}
  if type(decoded) ~= "table" then
    return prs
  end
  shared.each_paginated_item(decoded, function(pr)
    local number = type(pr) == "table" and tonumber(pr.number) or nil
    if number ~= nil then
      local head_ref_name = pr.headRefName or pr.head_ref_name
      local head_sha = pr.headRefOid or pr.head_ref_oid
      if type(pr.head) == "table" then
        head_ref_name = head_ref_name or pr.head.ref
        head_sha = head_sha or pr.head.sha
      end
      local base_ref_name = pr.baseRefName or pr.base_ref_name
      if base_ref_name == nil and type(pr.base) == "table" then
        base_ref_name = pr.base.ref
      end
      table.insert(prs, {
        number = number,
        head_sha = head_sha,
        head_ref_name = head_ref_name,
        base_ref_name = base_ref_name,
        state = pr.state,
      })
    end
  end)
  return prs
end

function C.parse_pr_view_head_state(stdout)
  local decoded = json.decode(stdout or "{}")
  return {
    head_ref_name = decoded.headRefName or decoded.head_ref_name,
    base_ref_name = decoded.baseRefName or decoded.base_ref_name,
    state = decoded.state,
  }
end

return C
