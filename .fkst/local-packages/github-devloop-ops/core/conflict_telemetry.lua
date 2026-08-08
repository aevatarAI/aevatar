local devloop_base = require("devloop.base")
local entity_lib = require("devloop.entity")
local base_ids = require("devloop.base_ids")
local S = {}
local conflict_telemetry = require("devloop.conflict_telemetry")
local devloop_logging = require("devloop.logging")

function S.install(M)
local conflict_hotspot_threshold = 3
local conflict_hotspot_window_days = 7

local function hotspot_title(file)
  local title = "Split conflict hotspot: " .. tostring(file)
  if #title > M._max_title_len then
    title = base_ids.truncate_utf8(title, M._max_title_len)
  end
  return title
end

local function hotspot_body(hotspot)
  local lines = {
    "Conflict hotspot detected from structured fix-lane telemetry.",
    "",
    "File: `" .. tostring(hotspot.file) .. "`",
    "Window: " .. tostring(conflict_hotspot_window_days) .. " days",
    "Distinct PRs: " .. tostring(#hotspot.prs) .. " (" .. table.concat(hotspot.prs, ", ") .. ")",
    "",
    "Evidence:",
  }
  for _, fact in ipairs(hotspot.evidence or {}) do
    table.insert(lines, "- conflict_file=" .. tostring(fact.file)
      .. " pr=" .. tostring(fact.pr)
      .. " ts=" .. tostring(fact.timestamp or "")
      .. " proposal_id=" .. tostring(fact.proposal_id))
  end
  table.insert(lines, "")
  table.insert(lines, "Requested outcome:")
  table.insert(lines, "- Evaluate whether this file should be split or sharded to reduce recurring merge conflicts.")
  table.insert(lines, "- Feed the normal intake, consensus, implementation, and review pipeline; this patrol must not restructure code directly.")
  local body = table.concat(lines, "\n")
  if #body > M._max_body_len then
    body = base_ids.truncate_utf8(body, M._max_body_len)
  end
  return body
end

local function hotspot_parent_comment_target(repo, hotspot)
  for _, fact in ipairs(hotspot and hotspot.evidence or {}) do
    local entity = entity_lib.parse_entity_proposal_id(fact.proposal_id)
    if entity ~= nil
      and entity.kind == "issue"
      and tostring(entity.repo or "") == tostring(repo or "")
      and entity.issue_number ~= nil then
      return {
        repo = repo,
        issue_number = entity.issue_number,
      }
    end
  end
  return nil
end

function M.build_conflict_hotspot_issue_create_request(repo, hotspot)
  local key = conflict_telemetry.conflict_path_key(hotspot.file)
  return {
    schema = "github-proxy.issue-create.v1",
    repo = repo,
    title = hotspot_title(hotspot.file),
    body = hotspot_body(hotspot),
    labels = json.decode("[]"),
    dedup_key = base_ids.dedup_key({
      "conflict-hotspot",
      tostring(repo or ""),
      key,
    }),
    parent_comment_target = hotspot_parent_comment_target(repo, hotspot),
    source_ref = {
      kind = "external",
      ref = tostring(repo or "") .. "#conflict-hotspot/" .. key,
    },
  }
end

function M.observe_conflict_hotspots(repo, timeout)
  local cmd = devloop_base.read_env("FKST_DEVLOOP_CONFLICT_LOG_CMD")
  if cmd == nil or tostring(cmd) == "" then
    log.info("github-devloop dept=observability tag=CONFLICT_HOTSPOT_PATROL action=no-op reason=log-source-unconfigured")
    return { facts = 0, hotspots = 0, raised = 0 }
  end
  local result = exec_sync({ cmd = cmd, timeout = timeout or 30 })
  if type(result) ~= "table" or result.exit_code ~= 0 then
    log.warn("github-devloop dept=observability tag=CONFLICT_HOTSPOT_PATROL action=no-op reason=log-source-failed")
    return { facts = 0, hotspots = 0, raised = 0 }
  end
  local facts = conflict_telemetry.parse_conflict_file_facts(result.stdout)
  local hotspots = conflict_telemetry.conflict_hotspots(facts, conflict_hotspot_threshold, now())
  local raised = 0
  for _, hotspot in ipairs(hotspots) do
    local request = M.build_conflict_hotspot_issue_create_request(repo, hotspot)
    devloop_logging.log_raise("observability", "conflict-hotspot/" .. tostring(hotspot.file), "github-proxy.github_issue_create_request", request)
    raised = raised + 1
    log.info("github-devloop dept=observability tag=CONFLICT_HOTSPOT_PATROL"
      .. " action=raise"
      .. " conflict_file=" .. tostring(hotspot.file)
      .. " distinct_prs=" .. tostring(#hotspot.prs)
      .. " dedup_key=" .. tostring(request.dedup_key))
  end
  if raised == 0 then
    log.info("github-devloop dept=observability tag=CONFLICT_HOTSPOT_PATROL"
      .. " action=no-op"
      .. " reason=below-threshold"
      .. " facts=" .. tostring(#facts)
      .. " hotspots=" .. tostring(#hotspots))
  end
  return {
    facts = #facts,
    hotspots = #hotspots,
    raised = raised,
  }
end
end

return S
