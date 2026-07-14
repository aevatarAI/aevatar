local devloop_base = require("devloop.base")
local base_ids = require("devloop.base_ids")
local strings = require("contract.strings")
local C = {}
local shared = require("devloop.payloads.shared")

local function board_feed_cmd(M)
  local cmd = devloop_base.read_env("FKST_DEVLOOP_BOARD_CMD")
  if cmd == nil or strings.trim(cmd) == "" then
    return nil
  end
  return cmd
end

local function parse_board_list(M, stdout)
  local decoded = json.decode(stdout or "[]")
  local items = {}
  if type(decoded) ~= "table" then
    return items
  end
  for _, item in ipairs(decoded) do
    if type(item) == "table" and tonumber(item.number) ~= nil then
      table.insert(items, {
        number = tonumber(item.number),
        title = tostring(item.title or ""),
        labels = shared.label_names(M, item.labels),
      })
    end
  end
  return items
end

local function first_chars(M, value, limit)
  local text = tostring(value or ""):gsub("[%s]+", " ")
  if #text > limit then
    return base_ids.truncate_utf8(text, limit)
  end
  return text
end

local function recurrence_label_digest(M, labels)
  local selected = {}
  for _, label in ipairs(labels or {}) do
    local text = tostring(label)
    if text:find("^error%-class:", 1) ~= nil
      or text:find("^fingerprint:", 1) ~= nil
      or text:find("^fkst%-dev:", 1) ~= nil then
      table.insert(selected, text)
    end
    if #selected >= 4 then
      break
    end
  end
  if #selected == 0 then
    return "labels=none"
  end
  return "labels=" .. first_chars(M, table.concat(selected, ","), 120)
end

local function state_label(M, labels)
  for _, label in ipairs(labels or {}) do
    local text = tostring(label)
    if M.is_state_label(text) then
      return text
    end
  end
  return "open"
end

local function render_closed_issue_line(M, item)
  return "#" .. tostring(item.number)
    .. " [closed] "
    .. first_chars(M, item.title, 80)
    .. " (" .. recurrence_label_digest(M, item.labels) .. ")"
end

local function render_board_digest(M, issues, prs, closed_issues)
  local lines = {
    M._untrusted_issue_data_begin,
    "Open items snapshot:",
  }
  for _, item in ipairs(issues or {}) do
    if #lines >= 52 then
      break
    end
    table.insert(lines, "#" .. tostring(item.number)
      .. " [" .. state_label(M, item.labels) .. "] "
      .. first_chars(M, item.title, 60))
  end
  for _, item in ipairs(prs or {}) do
    if #lines >= 52 then
      break
    end
    table.insert(lines, "#" .. tostring(item.number)
      .. " [" .. state_label(M, item.labels) .. "] "
      .. first_chars(M, item.title, 60))
  end
  table.insert(lines, "")
  table.insert(lines, "Recent closed issues for recurrence judgment:")
  for _, item in ipairs(closed_issues or {}) do
    if #lines >= 84 then
      break
    end
    table.insert(lines, render_closed_issue_line(M, item))
  end
  if type(closed_issues) ~= "table" or #closed_issues == 0 then
    table.insert(lines, "(none fetched)")
  end
  table.insert(lines, M._untrusted_issue_data_end)
  return table.concat(lines, "\n")
end

local function fetch_board_feed(M)
  local cmd = board_feed_cmd(M)
  if cmd == nil then
    return nil
  end
  local result = exec_sync({ cmd = cmd, timeout = 30 })
  if type(result) ~= "table" or result.exit_code ~= 0 then
    error("github-devloop: FKST_DEVLOOP_BOARD_CMD failed")
  end
  local stdout = tostring(result.stdout or "")
  if stdout == "" then
    return nil
  end
  return M._untrusted_issue_data_begin .. "\n"
    .. "Board feed-through from FKST_DEVLOOP_BOARD_CMD:\n"
    .. stdout:gsub("%s*$", "")
    .. "\n" .. M._untrusted_issue_data_end
end

function C.board_digest_block(M, repo, tick)
  if tick == nil or tostring(tick) == "" then
    return ""
  end
  local key = "github-devloop/board-digest/" .. base_ids.safe_repo(repo) .. "/" .. M.safe_updated_at(tick)
  local cached = cache_get(key)
  if cached ~= nil and cached ~= "" then
    return cached
  end

  local feed = fetch_board_feed(M)
  if feed ~= nil then
    cache_set(key, feed)
    return feed
  end

  local github_port = shared.github(M)
  local ok_issue, issue_result = pcall(github_port.issue_list_board_digest, repo, 30)
  local ok_pr, pr_result = pcall(github_port.pr_list_board_digest, repo, 30)
  local ok_closed, closed_result = pcall(github_port.issue_list_recent_closed, repo, 30, 30)
  if not ok_issue or not ok_pr
    or type(issue_result) ~= "table" or issue_result.exit_code ~= 0
    or type(pr_result) ~= "table" or pr_result.exit_code ~= 0 then
    return ""
  end

  local closed_issues = nil
  if ok_closed and type(closed_result) == "table" and closed_result.exit_code == 0 then
    local ok_parse, parsed = pcall(parse_board_list, M, closed_result.stdout)
    if ok_parse then
      closed_issues = parsed
    end
  end

  local block = render_board_digest(
    M,
    parse_board_list(M, issue_result.stdout),
    parse_board_list(M, pr_result.stdout),
    closed_issues
  )
  cache_set(key, block)
  return block
end

function C.append_board_digest_to_proposal(M, proposal, repo, tick)
  local block = C.board_digest_block(M, repo, tick)
  if block == "" then
    return proposal
  end
  local body = tostring(proposal.body or "")
  local prefix = "\n\n"
  local neutralized = devloop_base.neutralize_untrusted_prompt_text(block)
  local remaining = M._max_body_len - #body - #prefix
  if remaining <= 0 then
    M.log_line("warn", "payloads", proposal.proposal_id, "BOARD_DIGEST", {
      "outcome=drop",
      "reason=body-budget-exhausted",
      "repo=" .. tostring(repo or ""),
      "tick=" .. tostring(tick or ""),
    })
    return proposal
  end
  if #neutralized > remaining then
    M.log_line("warn", "payloads", proposal.proposal_id, "BOARD_DIGEST", {
      "outcome=truncate",
      "reason=body-budget",
      "repo=" .. tostring(repo or ""),
      "tick=" .. tostring(tick or ""),
      "available=" .. tostring(remaining),
      "needed=" .. tostring(#neutralized),
    })
    neutralized = base_ids.truncate_utf8(neutralized, remaining)
  end
  proposal.body = body .. prefix .. neutralized
  if #proposal.body > M._max_body_len then
    error("github-devloop: proposal board digest exceeds bounded body")
  end
  return proposal
end

return C
