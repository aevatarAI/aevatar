local git_mechanics = require("devloop.git_mechanics")
local entity_lib = require("devloop.entity")
local m_claims = require("devloop.claims")
local C = {}
local m_mq = require("devloop.merge_queue")

local function log_batch_window(M, proposal_id, fields)
  local facts = { "batch_window=true" }
  for _, field in ipairs(fields or {}) do
    table.insert(facts, field)
  end
  M.log_line("info", "merge", proposal_id or "merge", "BATCH_WINDOW", facts)
end

local function record_merged_files(M, repo, entry, merged_files)
  local files, reason = m_mq.merge_queue_changed_files(M, repo, entry)
  if files == nil then
    log_batch_window(M, entry.proposal_id, {
      "action=stop",
      "pr=" .. tostring(entry.pr_number),
      "reason=" .. tostring(reason),
    })
    return false
  end
  table.insert(merged_files, files)
  log_batch_window(M, entry.proposal_id, {
    "action=sample",
    "pr=" .. tostring(entry.pr_number),
    "base=" .. tostring(files.base_sha or ""),
    "head=" .. tostring(files.head_sha or ""),
    "files=" .. tostring(#files.paths),
  })
  return true
end

local function files_disjoint_from_window(M, files, merged_files)
  for _, merged in ipairs(merged_files or {}) do
    local disjoint, path = m_mq.merge_queue_files_disjoint(M, files, merged)
    if not disjoint then
      return false, path, merged.pr_number
    end
  end
  return true, "disjoint", nil
end

local function current_base_head(M, branches)
  local base_head, reason = git_mechanics.current_base_head(M.git, branches.integration)
  if base_head == nil then
    return nil, reason
  end
  return base_head, "current-base-ok"
end

local function head_contains_base(M, base_head, entry)
  local head_sha = tostring(entry and entry.head_sha or "")
  if not require("devloop.pr_safety").is_safe_head_sha(base_head)
    or not require("devloop.pr_safety").is_safe_head_sha(head_sha)
    or not require("devloop.pr_safety").is_safe_branch(entry and entry.head_branch) then
    return false, "unsafe-current-base"
  end
  local fetch_result = M.git_fetch_branch("origin", entry.head_branch, 60)
  if fetch_result.exit_code ~= 0 then
    return false, "candidate-head-fetch-failed"
  end
  local fetched_head = M.git_fetch_head_commit(30)
  if fetched_head.exit_code ~= 0 then
    return false, "candidate-head-underivable"
  end
  local fetched_sha = tostring(fetched_head.stdout or ""):gsub("%s+$", "")
  if fetched_sha ~= head_sha then
    return false, "candidate-head-changed"
  end
  local result = git_mechanics.git_is_ancestor(M.git, base_head, head_sha, 30)
  if result.exit_code == 0 then
    return true, "current-base-contained"
  end
  return false, "current-base-not-contained"
end

local function entry_issue_number(M, entry)
  local entity = entity_lib.parse_entity_proposal_id(entry and entry.proposal_id)
  return entity and entity.issue_number or nil
end

local function batch_entry_claim_ok(M, repo, entry)
  return m_claims.verify_pr_review_issue_claim(M, "merge_batch", repo, entry_issue_number(M, entry), nil, entry and entry.proposal_id)
end

local function find_queue_entry(entries, merge_ready)
  for index, entry in ipairs(entries or {}) do
    if tostring(entry.pr_number) == tostring(merge_ready.pr_number)
      and tostring(entry.proposal_id or "") == tostring(merge_ready.proposal_id or "")
      and tostring(entry.version or "") == tostring(merge_ready.version or "") then
      return entry, index
    end
  end
  return nil, nil
end

function C.run_merge_batch_window(M, repo, branches, first_merge_ready, queue_entries, options, process_merge_ready)
  local first_entry, first_index = find_queue_entry(queue_entries, first_merge_ready)
  if first_entry == nil or first_index == nil then
    log_batch_window(M, first_merge_ready.proposal_id, {
      "action=complete",
      "size=1",
      "reason=head-not-initial-queue",
    })
    return first_merge_ready.pr_number
  end

  local merged_files = {}
  local merged_count = 1
  if not record_merged_files(M, repo, first_entry, merged_files) then
    return first_entry.pr_number
  end
  local last_merged_pr_number = first_entry.pr_number

  local previous_base_head = tostring(first_entry.base_sha or "")
  local required_base_head, base_reason = current_base_head(M, branches)
  if required_base_head == nil then
    log_batch_window(M, first_merge_ready.proposal_id, {
      "action=stop",
      "pr=" .. tostring(first_entry.pr_number),
      "reason=" .. tostring(base_reason),
      "size=" .. tostring(merged_count),
    })
    return last_merged_pr_number
  end
  if previous_base_head == required_base_head then
    log_batch_window(M, first_merge_ready.proposal_id, {
      "action=stop",
      "pr=" .. tostring(first_entry.pr_number),
      "reason=current-base-not-advanced",
      "base=" .. tostring(required_base_head),
      "size=" .. tostring(merged_count),
    })
    return last_merged_pr_number
  end

  for index = first_index + 1, #(queue_entries or {}) do
    local entry = queue_entries[index]
    if entry.state ~= "merge-ready" then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=lane-state-" .. tostring(entry.state),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    if not batch_entry_claim_ok(M, repo, entry) then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=claim-not-owned",
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    local base_ok, head_base_reason = head_contains_base(M, required_base_head, entry)
    if not base_ok then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=" .. tostring(head_base_reason),
        "base=" .. tostring(required_base_head or ""),
        "head=" .. tostring(entry.head_sha or ""),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    local files, file_reason = m_mq.merge_queue_changed_files(M, repo, entry)
    if files == nil then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=" .. tostring(file_reason),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    local disjoint, path, conflicting_pr = files_disjoint_from_window(M, files, merged_files)
    if not disjoint then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=file-overlap",
        "path=" .. tostring(path),
        "conflicting_pr=" .. tostring(conflicting_pr),
        "base=" .. tostring(files.base_sha or ""),
        "head=" .. tostring(files.head_sha or ""),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    log_batch_window(M, entry.proposal_id, {
      "action=try",
      "pr=" .. tostring(entry.pr_number),
      "reason=disjoint",
      "base=" .. tostring(files.base_sha or ""),
      "head=" .. tostring(files.head_sha or ""),
      "files=" .. tostring(#files.paths),
    })
    local merge_ready = m_mq.merge_ready_payload_from_queue_entry(M, entry, entity_lib.pr_source_ref(repo, entry.pr_number))
    if merge_ready == nil then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=invalid-merge-ready-payload",
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    merge_ready._merge_pass = "poll"
    local entity = entity_lib.parse_entity_proposal_id(merge_ready.proposal_id)
    local outcome = process_merge_ready(repo, entity and entity.issue_number or nil, merge_ready, branches, nil, {
      enforce_queue = false,
      write_mode = options and options.write_mode or nil,
    })
    if outcome == nil or outcome.status ~= "merged" then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=gate-not-merged",
        "outcome=" .. tostring(outcome and outcome.status or "held"),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    table.insert(merged_files, files)
    merged_count = merged_count + 1
    last_merged_pr_number = entry.pr_number
    previous_base_head = tostring(files.base_sha or "")
    required_base_head, base_reason = current_base_head(M, branches)
    if required_base_head == nil then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=" .. tostring(base_reason),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
    if previous_base_head == required_base_head then
      log_batch_window(M, entry.proposal_id, {
        "action=stop",
        "pr=" .. tostring(entry.pr_number),
        "reason=current-base-not-advanced",
        "base=" .. tostring(required_base_head),
        "size=" .. tostring(merged_count),
      })
      return last_merged_pr_number
    end
  end

  log_batch_window(M, first_merge_ready.proposal_id, {
    "action=complete",
    "size=" .. tostring(merged_count),
  })
  return last_merged_pr_number
end

return C
