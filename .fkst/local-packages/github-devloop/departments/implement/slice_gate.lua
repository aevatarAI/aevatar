local entity_lib = require("devloop.entity")
local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local requests_labels = require("devloop.requests.labels")
local core = require("core")

local M = {}

local function marker_attr(marker, name)
  return marker:match('%f[%w_]' .. name .. '="([^"]*)"')
end

local function body_text(current)
  return tostring(current and current.body or "")
end

local function slice_marker(current)
  for marker in body_text(current):gmatch("<!%-%- fkst:ratchet%-slice:v1.-%-%->") do
    local entry_key = marker_attr(marker, "entry_key")
    if entry_key ~= nil and entry_key ~= "" then
      return marker
    end
  end
  return nil
end

local function parse_entry_key(current)
  local marker = slice_marker(current)
  if marker == nil then
    return nil
  end
  local entry_key = marker_attr(marker, "entry_key")
  if entry_key == nil or not entry_key:match("^[0-9a-f]+$") or #entry_key ~= 64 then
    return nil
  end
  return entry_key
end

local function read_ledger(entry_key)
  local ref = core.ratchet_slice_ledger_ref(entry_key)
  local listed = core.git_ls_remote_ref("origin", ref, 30)
  if type(listed) ~= "table" or listed.exit_code ~= 0 then
    error("github-devloop: ratchet slice ledger ls-remote failed: " .. tostring(listed and listed.stderr or "missing result"))
  end
  local sha = core.parse_ratchet_slice_ledger_ref_sha(listed.stdout)
  if sha == nil then
    return nil
  end
  local fetched = core.git_fetch_ref("origin", ref, 30)
  if type(fetched) ~= "table" or fetched.exit_code ~= 0 then
    error("github-devloop: ratchet slice ledger fetch failed: " .. tostring(fetched and fetched.stderr or "missing result"))
  end
  local commit = core.git_cat_file_pretty(sha, 30)
  if type(commit) ~= "table" or commit.exit_code ~= 0 then
    error("github-devloop: ratchet slice ledger cat-file failed: " .. tostring(commit and commit.stderr or "missing result"))
  end
  return core.decode_ratchet_slice_ledger(commit.stdout)
end

local function duplicate_comment(repo, issue_number, ready, entry_key, canonical_number)
  local body = "Duplicate migration slice for entry_key=" .. tostring(entry_key)
    .. "; canonical is #" .. tostring(canonical_number)
    .. "\n\n<!-- fkst:github-devloop:duplicate-slice:v1 entry_key=\"" .. tostring(entry_key)
    .. "\" canonical=\"" .. tostring(canonical_number) .. "\" -->"
  return entity_lib.build_entity_comment_request({
    kind = "issue",
    repo = repo,
    number = issue_number,
  }, body, base_ids.dedup_key({
    "implement",
    "duplicate-slice",
    tostring(entry_key),
    tostring(issue_number),
    tostring(canonical_number),
  }), ready.source_ref)
end

local function duplicate_label(repo, issue_number, ready, entry_key, canonical_number)
  return requests_labels.build_label_request(core,
    repo,
    issue_number,
    { "fkst:duplicate-slice" },
    {},
    base_ids.dedup_key({
      "implement",
      "duplicate-slice",
      "label",
      tostring(entry_key),
      tostring(issue_number),
      tostring(canonical_number),
    }),
    ready.source_ref
  )
end

function M.check(repo, issue_number, ready, current)
  local entry_key = parse_entry_key(current)
  if entry_key == nil then
    return false
  end
  local ledger = read_ledger(entry_key)
  if ledger == nil or ledger.state ~= "issue-created" then
    return false
  end
  local canonical = tonumber(ledger.issue_number)
  if canonical == nil or canonical == tonumber(issue_number) then
    return false
  end
  core.log_cas_decision("implement", ready.proposal_id, {
    state = "ready",
    version = ready.dedup_key,
  }, "ready", "duplicate-slice", "skip-stale(noncanonical-slice)", "canonical migration slice is #" .. tostring(canonical))
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_comment_request",
    duplicate_comment(repo, issue_number, ready, entry_key, canonical))
  core.log_raise("implement", ready.proposal_id, "github-proxy.github_issue_label_request",
    duplicate_label(repo, issue_number, ready, entry_key, canonical))
  if devloop_base.read_env("FKST_GITHUB_WRITE") == "1" then
    local closed = core.gh_issue_close(repo, issue_number, 30)
    if type(closed) ~= "table" or closed.exit_code ~= 0 then
      error("github-devloop: duplicate slice close failed: " .. tostring(closed and closed.stderr or "missing result"))
    end
  end
  return true
end

return M
