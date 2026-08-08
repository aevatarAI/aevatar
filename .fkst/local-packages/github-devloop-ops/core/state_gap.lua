local parsers_misc = require("devloop.parsers.misc")
local devloop_state = require("devloop.state")
local S = {}
local contract_time = require("contract.time")
local issue_observation_facts = require("devloop.restart.issue_observation_facts")

function S.install(M)

local max_dashboard_edges = 8
local max_worst_offenders = 3
local session_hand_off_seconds = 60

local function pattern_escape(value)
  return tostring(value or ""):gsub("([^%w])", "%%%1")
end

local function state_marker_stage_rank(marker, state)
  local explicit_rank = tonumber(marker:match('stage_rank="(%d+)"'))
  return explicit_rank or devloop_state.stage_rank(state)
end

local function parse_marker_time(comment)
  local created_at = parsers_misc._comment_created_at(comment)
  local seconds = contract_time.iso_timestamp_epoch_seconds(created_at)
  if seconds == nil then
    return nil, nil
  end
  return created_at, seconds
end

local function append_state_markers(markers, comments, proposal_id)
  local marker_pattern = "<!%-%- fkst:github%-devloop:state:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments or {})) do
    local created_at, created_seconds = parse_marker_time(comment)
    if created_seconds ~= nil then
      for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
        local marker_proposal = marker:match('proposal="([^"]+)"')
        local state = marker:match('state="([^"]+)"')
        local version = marker:match('version="([^"]*)"')
        if marker_proposal == proposal_id and devloop_state.is_state(state) then
          table.insert(markers, {
            proposal_id = proposal_id,
            state = state,
            version = version,
            stage_rank = state_marker_stage_rank(marker, state),
            created_at = created_at,
            created_seconds = created_seconds,
          })
        end
      end
    end
  end
end

local function append_entity_comments(list, comments)
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments or {})) do
    table.insert(list, comment)
  end
end

local function trusted_entity_comments(entity)
  local comments = {}
  append_entity_comments(comments, entity and entity.parent_issue and entity.parent_issue.comments or {})
  append_entity_comments(comments, entity and entity.pr and entity.pr.comments or {})
  return comments
end

local function comment_seconds(comment)
  local _, seconds = parse_marker_time(comment)
  return seconds
end

local function timestamp_between(value, first, second)
  local seconds = tonumber(value)
  return seconds ~= nil
    and seconds >= tonumber(first or 0)
    and seconds <= tonumber(second or 0)
end

local function marker_attr(marker, name)
  return tostring(marker or ""):match(tostring(name) .. '="([^"]*)"')
end

local function find_marker_between(entity, proposal_id, marker_kind, version, from_seconds, to_seconds)
  local marker_pattern = "<!%-%- fkst:github%-devloop:" .. pattern_escape(marker_kind) .. ":v1.-%-%->"
  local found = nil
  for _, comment in ipairs(trusted_entity_comments(entity)) do
    local seconds = comment_seconds(comment)
    if timestamp_between(seconds, from_seconds, to_seconds) then
      for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
        if marker_attr(marker, "proposal") == tostring(proposal_id)
          and (version == nil or marker_attr(marker, "version") == tostring(version)) then
          if found == nil or seconds < found.created_seconds then
            found = {
              marker = marker,
              comment = comment,
              created_at = parsers_misc._comment_created_at(comment),
              created_seconds = seconds,
            }
          end
        end
      end
    end
  end
  return found
end

local function has_marker_between(entity, proposal_id, marker_kind, version, from_seconds, to_seconds)
  return find_marker_between(entity, proposal_id, marker_kind, version, from_seconds, to_seconds) ~= nil
end

local function has_dependency_marker_between(entity, proposal_id, version, from_seconds, to_seconds)
  for _, marker_kind in ipairs({
    "dependency-wait",
    "dependency-cycle",
    "dependency-unresolvable",
    "dependency-release",
  }) do
    if has_marker_between(entity, proposal_id, marker_kind, version, from_seconds, to_seconds) then
      return true
    end
  end
  return false
end

local function ready_implementing_wait_evidence(entity, previous, marker)
  return {
    wait_class = marker.created_seconds - previous.created_seconds > session_hand_off_seconds
      and "visibility-retry"
      or "direct-marker",
    handoff_class = "unknown",
  }
end

function M.state_gap_wait_evidence(entity, previous, marker)
  if previous == nil or marker == nil then
    return { wait_class = "unattributed" }
  end
  if previous.state == "ready"
    and has_dependency_marker_between(entity, previous.proposal_id, previous.version, previous.created_seconds, marker.created_seconds) then
    return { wait_class = "dependency-gate" }
  end
  if previous.state == "ready" and marker.state == "implementing" then
    return ready_implementing_wait_evidence(entity, previous, marker)
  end
  if previous.state == "merge-ready" and marker.state == "merging" then
    return { wait_class = "merge-queue" }
  end
  return { wait_class = "unattributed" }
end

function M.state_gap_wait_class(entity, previous, marker)
  return M.state_gap_wait_evidence(entity, previous, marker).wait_class
end

local function marker_sort_key(marker)
  return tostring(marker.created_at or "")
    .. "/"
    .. string.format("%04d", tonumber(marker.stage_rank) or 0)
    .. "/"
    .. tostring(marker.state or "")
    .. "/"
    .. tostring(marker.version or "")
end

function M.state_gap_marker_stream(entity)
  local markers = {}
  if entity == nil or entity.proposal_id == nil then
    return markers
  end
  append_state_markers(markers, entity.parent_issue and entity.parent_issue.comments or {}, entity.proposal_id)
  append_state_markers(markers, entity.pr and entity.pr.comments or {}, entity.proposal_id)
  table.sort(markers, function(a, b)
    return marker_sort_key(a) < marker_sort_key(b)
  end)
  return markers
end

local function budget_status(from_state, gap_seconds)
  local budget_minutes = issue_observation_facts.budget_minutes(from_state)
  if budget_minutes == nil then
    return "no-budget", nil
  end
  local budget_seconds = math.floor(budget_minutes * 60)
  if gap_seconds > budget_seconds then
    return "over-budget", budget_seconds
  end
  if gap_seconds >= math.floor(budget_seconds * 0.8) then
    return "near-budget", budget_seconds
  end
  return "within-budget", budget_seconds
end

function M.state_gap_edges_for_entity(entity)
  local markers = M.state_gap_marker_stream(entity)
  local edges = {}
  local previous = nil
  for _, marker in ipairs(markers) do
    if previous ~= nil and previous.state ~= marker.state and marker.created_seconds >= previous.created_seconds then
      local gap_seconds = marker.created_seconds - previous.created_seconds
      local status, budget_seconds = budget_status(previous.state, gap_seconds)
      local edge = {
        proposal_id = entity.proposal_id,
        issue_number = entity.issue_number,
        from_state = previous.state,
        to_state = marker.state,
        edge = tostring(previous.state) .. "->" .. tostring(marker.state),
        gap_seconds = gap_seconds,
        from_created_at = previous.created_at,
        to_created_at = marker.created_at,
        budget_seconds = budget_seconds,
        budget_status = status,
      }
      local evidence = M.state_gap_wait_evidence(entity, previous, marker)
      for key, value in pairs(evidence) do
        edge[key] = value
      end
      table.insert(edges, edge)
    end
    previous = marker
  end
  return edges
end

local function percentile(sorted_values, fraction)
  local count = #sorted_values
  if count == 0 then
    return nil
  end
  local rank = math.ceil(count * fraction)
  if rank < 1 then
    rank = 1
  elseif rank > count then
    rank = count
  end
  return sorted_values[rank]
end

local function sort_edge_summaries(summaries)
  table.sort(summaries, function(a, b)
    if a.p95_seconds ~= b.p95_seconds then
      return (a.p95_seconds or 0) > (b.p95_seconds or 0)
    end
    return tostring(a.edge or "") < tostring(b.edge or "")
  end)
end

function M.state_gap_report(entities)
  local all_edges = {}
  for _, entity in ipairs(entities or {}) do
    for _, edge in ipairs(M.state_gap_edges_for_entity(entity)) do
      table.insert(all_edges, edge)
    end
  end
  local by_edge = {}
  for _, edge in ipairs(all_edges) do
      by_edge[edge.edge] = by_edge[edge.edge] or {
        edge = edge.edge,
        from_state = edge.from_state,
        to_state = edge.to_state,
        values = {},
        offenders = {},
        wait_class_counts = {},
        handoff_counts = {},
        over_budget_count = 0,
        near_budget_count = 0,
        budget_seconds = edge.budget_seconds,
      }
      local bucket = by_edge[edge.edge]
      table.insert(bucket.values, edge.gap_seconds)
      table.insert(bucket.offenders, edge)
      local wait_class = tostring(edge.wait_class or "unattributed")
      bucket.wait_class_counts[wait_class] = (bucket.wait_class_counts[wait_class] or 0) + 1
      if edge.handoff_class ~= nil then
        local handoff_class = tostring(edge.handoff_class)
        bucket.handoff_counts[handoff_class] = (bucket.handoff_counts[handoff_class] or 0) + 1
      end
      if edge.budget_status == "over-budget" then
        bucket.over_budget_count = bucket.over_budget_count + 1
      elseif edge.budget_status == "near-budget" then
        bucket.near_budget_count = bucket.near_budget_count + 1
      end
  end

  local summaries = {}
  for _, bucket in pairs(by_edge) do
    table.sort(bucket.values)
    table.sort(bucket.offenders, function(a, b)
      if a.gap_seconds ~= b.gap_seconds then
        return a.gap_seconds > b.gap_seconds
      end
      return tostring(a.proposal_id or "") < tostring(b.proposal_id or "")
    end)
    table.insert(summaries, {
      edge = bucket.edge,
      from_state = bucket.from_state,
      to_state = bucket.to_state,
      count = #bucket.values,
      p50_seconds = percentile(bucket.values, 0.50),
      p95_seconds = percentile(bucket.values, 0.95),
      max_seconds = bucket.values[#bucket.values],
      budget_seconds = bucket.budget_seconds,
      over_budget_count = bucket.over_budget_count,
      near_budget_count = bucket.near_budget_count,
      wait_class_counts = bucket.wait_class_counts,
      handoff_counts = bucket.handoff_counts,
      offenders = bucket.offenders,
    })
  end
  sort_edge_summaries(summaries)
  return {
    edges = all_edges,
    summaries = summaries,
  }
end

function M.state_gap_log_line(edge)
  return table.concat({
    "github-devloop",
    "dept=observability",
    "tag=GAP_EDGE",
    "proposal_id=" .. tostring(edge and edge.proposal_id or "unknown"),
    "gap_edge=" .. tostring(edge and edge.edge or "unknown"),
    "gap_seconds=" .. tostring(edge and edge.gap_seconds or 0),
    "budget_seconds=" .. tostring(edge and edge.budget_seconds or ""),
    "budget_status=" .. tostring(edge and edge.budget_status or "unknown"),
    "wait_class=" .. tostring(edge and edge.wait_class or "unattributed"),
    "handoff_class=" .. tostring(edge and edge.handoff_class or ""),
    "from_created_at=" .. tostring(edge and edge.from_created_at or ""),
    "to_created_at=" .. tostring(edge and edge.to_created_at or ""),
  }, " ")
end

local function format_duration(seconds)
  local value = tonumber(seconds)
  if value == nil then
    return "n/a"
  end
  if value < 60 then
    return tostring(math.floor(value)) .. "s"
  end
  local minutes = math.floor(value / 60)
  local rest = math.floor(value % 60)
  if minutes < 60 then
    return tostring(minutes) .. "m " .. tostring(rest) .. "s"
  end
  local hours = math.floor(minutes / 60)
  local minute_rest = minutes % 60
  return tostring(hours) .. "h " .. tostring(minute_rest) .. "m"
end

local function offender_ref(edge)
  if tonumber(edge.issue_number) ~= nil then
    return "#" .. tostring(edge.issue_number)
  end
  return tostring(edge.proposal_id or "unknown")
end

local function offender_summary(offenders)
  local parts = {}
  for index, edge in ipairs(offenders or {}) do
    if index > max_worst_offenders then
      break
    end
    table.insert(parts, offender_ref(edge) .. " " .. format_duration(edge.gap_seconds))
  end
  if #parts == 0 then
    return "none"
  end
  return table.concat(parts, ", ")
end

local function wait_class_summary(counts)
  local rows = {}
  for class, count in pairs(counts or {}) do
    table.insert(rows, {
      class = tostring(class),
      count = tonumber(count) or 0,
    })
  end
  table.sort(rows, function(a, b)
    if a.count ~= b.count then
      return a.count > b.count
    end
    return a.class < b.class
  end)
  local parts = {}
  for _, row in ipairs(rows) do
    table.insert(parts, row.class .. " " .. tostring(row.count))
  end
  if #parts == 0 then
    return "unattributed 0"
  end
  return table.concat(parts, ", ")
end

local function handoff_summary(counts)
  local rows = {}
  for class, count in pairs(counts or {}) do
    table.insert(rows, {
      class = tostring(class),
      count = tonumber(count) or 0,
    })
  end
  table.sort(rows, function(a, b)
    if a.count ~= b.count then
      return a.count > b.count
    end
    return a.class < b.class
  end)
  local parts = {}
  for _, row in ipairs(rows) do
    table.insert(parts, row.class .. " " .. tostring(row.count))
  end
  if #parts == 0 then
    return "none"
  end
  return table.concat(parts, ", ")
end

function M.append_state_gap_dashboard_section(lines, report)
  table.insert(lines, "")
  table.insert(lines, "## State-gap latency")
  local summaries = report and report.summaries or {}
  if #summaries == 0 then
    table.insert(lines, "- No completed state gaps in the trusted marker window.")
    return
  end
  local shown = 0
  for _, summary in ipairs(summaries) do
    if shown >= max_dashboard_edges then
      table.insert(lines, "- ... " .. tostring(#summaries - shown) .. " more")
      break
    end
    local budget = summary.budget_seconds ~= nil and format_duration(summary.budget_seconds) or "n/a"
    table.insert(lines, "- " .. tostring(summary.edge)
      .. ": count " .. tostring(summary.count)
      .. ", P50 " .. format_duration(summary.p50_seconds)
      .. ", P95 " .. format_duration(summary.p95_seconds)
      .. ", max " .. format_duration(summary.max_seconds)
      .. ", budget " .. budget
      .. ", near " .. tostring(summary.near_budget_count or 0)
      .. ", over " .. tostring(summary.over_budget_count or 0)
      .. ", classes " .. wait_class_summary(summary.wait_class_counts)
      .. ", handoff " .. handoff_summary(summary.handoff_counts)
      .. "; worst " .. offender_summary(summary.offenders))
    shown = shown + 1
  end
end

end

return S
