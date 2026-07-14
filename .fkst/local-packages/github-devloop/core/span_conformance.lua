local S = {}
local hidden_state_conformance = require("devloop.hidden_state_conformance")
local m_rrc = require("devloop.restart_responsibility_contract")

local START_WORDS = {
  start = true,
  starts = true,
  started = true,
  begin = true,
  begins = true,
  began = true,
  beginning = true,
}

local function sorted_keys(map)
  local keys = {}
  for key, _ in pairs(map or {}) do
    table.insert(keys, key)
  end
  table.sort(keys)
  return keys
end

local function line_number(source, index)
  local line = 1
  for pos = 1, math.max(1, (index or 1) - 1) do
    if source:sub(pos, pos) == "\n" then
      line = line + 1
    end
  end
  return line
end

local function unescape_lua_string(value)
  return tostring(value or ""):gsub('\\"', '"'):gsub("\\'", "'"):gsub("\\n", "\n")
end

local function is_word_char(ch)
  return ch ~= nil and ch:match("[%w_]") ~= nil
end

local function has_start_word(text)
  local lower = tostring(text or ""):lower()
  local pos = 1
  while pos <= #lower do
    local first, last, word = lower:find("([%w_]+)", pos)
    if first == nil then
      return false
    end
    if START_WORDS[word] then
      local before = first > 1 and lower:sub(first - 1, first - 1) or nil
      local after = last < #lower and lower:sub(last + 1, last + 1) or nil
      if not is_word_char(before) and not is_word_char(after) then
        return true
      end
    end
    pos = last + 1
  end
  return false
end

local function parse_quoted_at(source, pos)
  local quote = source:sub(pos, pos)
  if quote ~= '"' and quote ~= "'" then
    return nil
  end
  local out = {}
  local index = pos + 1
  while index <= #source do
    local ch = source:sub(index, index)
    if ch == "\\" and index < #source then
      table.insert(out, source:sub(index, index + 1))
      index = index + 2
    elseif ch == quote then
      return table.concat(out), index + 1
    else
      table.insert(out, ch)
      index = index + 1
    end
  end
  return nil
end

local function quoted_strings(source)
  local strings = {}
  local pos = 1
  while pos <= #source do
    local next_quote = source:find("[\"']", pos)
    if next_quote == nil then
      break
    end
    local value, next_pos = parse_quoted_at(source, next_quote)
    if value ~= nil then
      table.insert(strings, { value = value, start = next_quote })
      pos = next_pos
    else
      pos = next_quote + 1
    end
  end
  return strings
end

local function key_value_strings(source)
  local values = {}
  local pos = 1
  while pos <= #source do
    local start_pos, end_pos, key = source:find("([A-Za-z0-9_]+)%s*=%s*", pos)
    if start_pos == nil then
      break
    end
    local value, next_pos = parse_quoted_at(source, end_pos + 1)
    if value ~= nil then
      values[key] = unescape_lua_string(value)
      pos = next_pos
    else
      pos = end_pos + 1
    end
  end
  return values
end

local function comment_strings(sources)
  local values = {}
  for _, path in ipairs(sorted_keys(sources)) do
    if path == "libraries/devloop/strings.lua" or path:sub(-#"core/strings.lua") == "core/strings.lua" then
      for key, value in pairs(key_value_strings(sources[path])) do
        values[key] = value
      end
    end
  end
  return values
end

local function function_blocks(source)
  local declarations = {}
  local pos = 1
  local line_no = 1
  while pos <= #source + 1 do
    local line_end = source:find("\n", pos, true) or (#source + 1)
    local line = source:sub(pos, line_end - 1)
    local local_name, local_params, local_end =
      line:match("^%s*local%s+function%s+([A-Za-z_][A-Za-z0-9_%.:]*)%s*%(([^)]*)%)()")
    local name = local_name
    local params = local_params
    local header_end = local_end
    if name == nil then
      local direct_name, direct_params, direct_end =
        line:match("^%s*function%s+([A-Za-z_][A-Za-z0-9_%.:]*)%s*%(([^)]*)%)()")
      name = direct_name
      params = direct_params
      header_end = direct_end
    end
    if name ~= nil then
      table.insert(declarations, {
        name = name,
        params = params or "",
        start = pos,
        body_start = pos + header_end - 1,
        start_line = line_no,
      })
    end
    pos = line_end + 1
    line_no = line_no + 1
  end

  local blocks = {}
  for index, declaration in ipairs(declarations) do
    local next_start = declarations[index + 1] and declarations[index + 1].start or (#source + 1)
    table.insert(blocks, {
      name = declaration.name,
      params = declaration.params,
      body = source:sub(declaration.body_start, next_start - 1),
      start_line = declaration.start_line,
    })
  end
  return blocks
end

local function has_head_sha_dependency(block)
  for param in tostring(block.params or ""):gmatch("[^,]+") do
    if param:match("^%s*(.-)%s*$") == "head_sha" then
      return true
    end
  end
  return tostring(block.body or ""):find("head_sha", 1, true) ~= nil
end

local function comment_string_calls(body)
  local calls = {}
  local pos = 1
  while pos <= #body do
    local start_pos, end_pos = body:find("%f[%w_]comment_string%s*%(", pos)
    if start_pos == nil then
      break
    end
    local arg_start = end_pos + 1
    while body:sub(arg_start, arg_start):match("%s") do
      arg_start = arg_start + 1
    end
    local _, first_end = body:find("[A-Za-z_][A-Za-z0-9_%.:]*%s*,%s*", arg_start)
    if first_end ~= nil then
      arg_start = first_end + 1
      while body:sub(arg_start, arg_start):match("%s") do
        arg_start = arg_start + 1
      end
    end
    local value, next_pos = parse_quoted_at(body, arg_start)
    if value ~= nil and value:match("^[A-Za-z0-9_]+$") then
      local close_pos = next_pos
      while body:sub(close_pos, close_pos):match("%s") do
        close_pos = close_pos + 1
      end
      if body:sub(close_pos, close_pos) == ")" then
        table.insert(calls, value)
      end
      pos = next_pos
    else
      pos = end_pos + 1
    end
  end
  return calls
end

local function completion_fact_name_messages(sources)
  local strings = comment_strings(sources)
  local messages = {}
  for _, path in ipairs(sorted_keys(sources)) do
    if path:sub(-4) == ".lua" then
      for _, block in ipairs(function_blocks(sources[path])) do
        if block.name:find("comment_request", 1, true) ~= nil and has_head_sha_dependency(block) then
          for _, key in ipairs(comment_string_calls(block.body)) do
            local text = strings[key] or key
            if has_start_word(key) or has_start_word(text) then
              table.insert(messages, string.format(
                "%s:%d %s completion/output comment uses start wording key %q while requiring post-work field head_sha",
                path,
                block.start_line,
                block.name,
                key
              ))
            end
          end
          for _, literal in ipairs(quoted_strings(block.body)) do
            if has_start_word(unescape_lua_string(literal.value)) then
              table.insert(messages, string.format(
                "%s:%d %s completion/output comment uses start wording literal while requiring post-work field head_sha",
                path,
                block.start_line,
                block.name
              ))
              break
            end
          end
        end
      end
    end
  end
  return messages
end

local function short_function_name(name)
  local value = tostring(name or "")
  return value:match("([^%.:]+)$") or value
end

local function function_index(sources)
  local index = {}
  for _, path in ipairs(sorted_keys(sources)) do
    for _, block in ipairs(function_blocks(sources[path])) do
      local short = short_function_name(block.name)
      index[short] = index[short] or {}
      table.insert(index[short], block)
    end
  end
  return index
end

local function call_names(body)
  local calls = {}
  for start_pos, name in tostring(body or ""):gmatch("()([A-Za-z_][A-Za-z0-9_%.:]*)%s*%(") do
    if tostring(body):sub(math.max(1, start_pos - 9), start_pos - 1) ~= "function " then
      table.insert(calls, short_function_name(name))
    end
  end
  return calls
end

local function marker_helper_name(durable_start_marker)
  local family = tostring(durable_start_marker or ""):match("^([^%s:]+)")
  if family == nil or family == "state" then
    return nil
  end
  return family:gsub("-", "_") .. "_marker"
end

local function state_marker_value(durable_start_marker)
  local prefix = "state:v1 "
  local marker = tostring(durable_start_marker or "")
  if marker:sub(1, #prefix) ~= prefix then
    return nil
  end
  local value = marker:sub(#prefix + 1):match("^%s*(.-)%s*$")
  return value ~= "" and value or nil
end

local function body_mentions_marker(body, durable_start_marker)
  body = tostring(body or "")
  if body:find(tostring(durable_start_marker or ""), 1, true) ~= nil then
    return true
  end
  local helper = marker_helper_name(durable_start_marker)
  if helper ~= nil and body:find("%f[%w_]" .. helper .. "%s*%(") ~= nil then
    return true
  end
  local state = state_marker_value(durable_start_marker)
  if state == nil then
    return false
  end
  if body:find("state_marker%s*%([^%)]*[\"']" .. state .. "[\"']") ~= nil then
    return true
  end
  if body:find("has_state_marker%s*%([^%)]*[\"']" .. state .. "[\"']") ~= nil then
    return true
  end
  if body:find("current_entity_state", 1, true) == nil then
    return false
  end
  return body:find("%f[%w_][A-Za-z_][A-Za-z0-9_]*%.state%s*[=~]=%s*[\"']" .. state .. "[\"']") ~= nil
end

local function function_binds_marker(functions, function_name, durable_start_marker)
  local pending = { function_name }
  local seen = {}
  while #pending > 0 do
    local current = table.remove(pending)
    if not seen[current] then
      seen[current] = true
      for _, block in ipairs(functions[current] or {}) do
        if body_mentions_marker(block.body, durable_start_marker) then
          return true
        end
        for _, callee in ipairs(call_names(block.body)) do
          if not seen[callee] and functions[callee] ~= nil then
            table.insert(pending, callee)
          end
        end
      end
    end
  end
  return false
end

local function worker_rows(transition_sources)
  local rows = {}
  for _, path in ipairs(sorted_keys(transition_sources)) do
    local source = transition_sources[path]
    for start_pos, _quote, state in source:gmatch("()from_state%s*=%s*([\"'])(.-)%2.-state_kind%s*=%s*([\"'])worker%4") do
      table.insert(rows, { state = state, path = path, line = line_number(source, start_pos), start = start_pos })
    end
  end
  return rows
end

local function span_contracts(transition_sources)
  local contracts = {}
  for _, row in ipairs(worker_rows(transition_sources)) do
    local source = transition_sources[row.path]
    local contract_start, body_start = source:find("span_contract%s*=%s*span_contract%s*%(%s*%{", row.start)
    if contract_start ~= nil then
      local body_end = source:find("%}%s*%)", body_start + 1)
      local body = body_end ~= nil and source:sub(body_start + 1, body_end - 1) or source:sub(body_start + 1)
      local fields = key_value_strings(body)
      if fields.department and fields.durable_start_marker and fields.spawn_predecessor then
        contracts[row.state] = {
          state = row.state,
          department = fields.department,
          durable_start_marker = fields.durable_start_marker,
          spawn_predecessor = fields.spawn_predecessor,
          spawn_function = fields.spawn_function,
          path = row.path,
          line = line_number(source, contract_start),
        }
      end
    end
    local real_execution_start, real_execution_body_start = source:find("real_execution%s*=%s*%{", row.start)
    if contracts[row.state] ~= nil and real_execution_start ~= nil then
      local real_execution_body_end = source:find("%}%s*,?%s*%}%s*,?%s*%)", real_execution_body_start + 1)
        or source:find("%}%s*,?%s*%}%s*,?", real_execution_body_start + 1)
      local body = real_execution_body_end ~= nil
        and source:sub(real_execution_body_start + 1, real_execution_body_end - 1)
        or source:sub(real_execution_body_start + 1)
      local fields = key_value_strings(body)
      if fields.primitive == "fkst.codex_runs" then
        contracts[row.state].dispatch_live_run_role = fields.role
      end
    end
  end
  return contracts
end

local function department_spawn_sources(department_sources, department)
  local needle = "/departments/" .. tostring(department or "") .. "/"
  local selected = {}
  for path, source in pairs(department_sources or {}) do
    if path:find(needle, 1, true) ~= nil then
      selected[path] = source
    end
  end
  return selected
end

local function predecessor_call_before(source, function_name, index)
  local found = -1
  local pos = 1
  while pos < index do
    local start_pos, end_pos = source:find(function_name, pos, true)
    if start_pos == nil or start_pos >= index then
      break
    end
    local before = start_pos > 1 and source:sub(start_pos - 1, start_pos - 1) or ""
    local after_pos = end_pos + 1
    while source:sub(after_pos, after_pos):match("%s") do
      after_pos = after_pos + 1
    end
    if not is_word_char(before) and source:sub(after_pos, after_pos) == "("
      and source:sub(math.max(1, start_pos - 9), start_pos - 1) ~= "function " then
      found = start_pos
    end
    pos = end_pos + 1
  end
  return found
end

local function function_contains_spawn(source, function_name)
  for _, block in ipairs(function_blocks(source)) do
    if short_function_name(block.name) == function_name and block.body:find("%f[%w_]spawn_codex_sync%s*%(") ~= nil then
      return true
    end
  end
  return false
end

local function function_call_positions(source, function_name)
  local positions = {}
  local pos = 1
  while pos <= #source do
    local start_pos, end_pos = source:find(function_name, pos, true)
    if start_pos == nil then
      break
    end
    local before = start_pos > 1 and source:sub(start_pos - 1, start_pos - 1) or ""
    local after_pos = end_pos + 1
    while source:sub(after_pos, after_pos):match("%s") do
      after_pos = after_pos + 1
    end
    if not is_word_char(before) and source:sub(after_pos, after_pos) == "("
      and source:sub(math.max(1, start_pos - 9), start_pos - 1) ~= "function " then
      table.insert(positions, start_pos)
    end
    pos = end_pos + 1
  end
  return positions
end

local function spawn_positions(source)
  local positions = {}
  for start_pos in source:gmatch("()%f[%w_]spawn_codex_sync%s*%(") do
    table.insert(positions, start_pos)
  end
  return positions
end

local function spawn_start_messages(transition_sources, department_sources, support_sources)
  local contracts = span_contracts(transition_sources)
  local functions = function_index(support_sources or department_sources)
  local messages = {}
  for _, row in ipairs(worker_rows(transition_sources)) do
    local contract = contracts[row.state]
    if contract ~= nil and contract.department:sub(1, #"external:") ~= "external:" then
      if not function_binds_marker(functions, contract.spawn_predecessor, contract.durable_start_marker) then
        table.insert(messages, string.format(
          "%s:%d span start predecessor %q does not bind durable start marker %q",
          contract.path,
          contract.line,
          contract.spawn_predecessor,
          contract.durable_start_marker
        ))
      end
      local sources = department_spawn_sources(department_sources, contract.department)
      if next(sources) == nil then
        table.insert(messages, string.format(
          "%s:%d span_contract department %q has no scanned department source",
          contract.path,
          contract.line,
          contract.department
        ))
      else
        local saw_spawn = false
        for _, source_path in ipairs(sorted_keys(sources)) do
          local source = sources[source_path]
          if contract.spawn_function ~= nil then
            if function_contains_spawn(source, contract.spawn_function) then
              saw_spawn = true
              for _, call_pos in ipairs(function_call_positions(source, contract.spawn_function)) do
                if predecessor_call_before(source, contract.spawn_predecessor, call_pos) < 0 then
                  table.insert(messages, string.format(
                    "%s:%d %s call must be preceded by span start predecessor %q for durable start marker %q",
                    source_path,
                    line_number(source, call_pos),
                    contract.spawn_function,
                    contract.spawn_predecessor,
                    contract.durable_start_marker
                  ))
                end
              end
            end
          else
            for _, spawn_pos in ipairs(spawn_positions(source)) do
              saw_spawn = true
              if predecessor_call_before(source, contract.spawn_predecessor, spawn_pos) < 0 then
                table.insert(messages, string.format(
                  "%s:%d spawn_codex_sync must be preceded by span start predecessor %q for durable start marker %q",
                  source_path,
                  line_number(source, spawn_pos),
                  contract.spawn_predecessor,
                  contract.durable_start_marker
                ))
              end
            end
          end
        end
        if not saw_spawn then
          table.insert(messages, string.format(
            "%s:%d span_contract department %q has no spawn_codex_sync call",
            contract.path,
            contract.line,
            contract.department
          ))
        end
      end
    end
  end
  return messages
end

local function dispatch_live_run_dedup_messages(transition_sources, department_sources)
  local contracts = span_contracts(transition_sources)
  local messages = {}
  local required_roles = {
    implement = true,
    fix = true,
  }
  for _, row in ipairs(worker_rows(transition_sources)) do
    local contract = contracts[row.state]
    if contract ~= nil
      and required_roles[tostring(contract.dispatch_live_run_role or "")] == true
      and contract.spawn_function ~= nil then
      local sources = department_spawn_sources(department_sources, contract.department)
      for _, source_path in ipairs(sorted_keys(sources)) do
        local source = sources[source_path]
        for _, call_pos in ipairs(function_call_positions(source, contract.spawn_function)) do
          if predecessor_call_before(source, "dispatch_live_run_dedup", call_pos) < 0 then
            table.insert(messages, string.format(
              "%s:%d %s call must be preceded by dispatch_live_run_dedup for role %q before long-running codex dispatch",
              source_path,
              line_number(source, call_pos),
              contract.spawn_function,
              contract.dispatch_live_run_role
            ))
          end
        end
      end
    end
  end
  return messages
end

local SCANNED_SOURCE_FILES = {
  "libraries/devloop/autonomy_ledger.lua",
  "libraries/devloop/base.lua",
  "libraries/devloop/claims.lua",
  "libraries/devloop/commands.lua",
  "libraries/devloop/commands/dashboard.lua",
  "libraries/devloop/commands/git_ops.lua",
  "libraries/devloop/commands/issue_reads.lua",
  "libraries/devloop/commands/labels.lua",
  "libraries/devloop/commands/observe_lists.lua",
  "libraries/devloop/commands/prs.lua",
  "libraries/devloop/commands/support.lua",
  "libraries/devloop/commands/validators.lua",
  "libraries/devloop/comment_handoff.lua",
  "libraries/devloop/config.lua",
  "libraries/devloop/conflict_telemetry.lua",
  "libraries/devloop/context_bundle.lua",
  "libraries/devloop/convergence.lua",
  "libraries/devloop/convergence/attempts.lua",
  "libraries/devloop/convergence/reconcile.lua",
  "libraries/devloop/convergence/rounds.lua",
  "libraries/devloop/convergence/shared.lua",
  "libraries/devloop/decompose.lua",
  "libraries/devloop/entity.lua",
  "libraries/devloop/entity_list_cache.lua",
  "libraries/devloop/forks.lua",
  "libraries/devloop/gate.lua",
  "libraries/devloop/git_mechanics.lua",
  "libraries/devloop/github_proxy_entity_view.lua",
  "libraries/devloop/github_risk.lua",
  "libraries/devloop/liveness.lua",
  "libraries/devloop/liveness/signal.lua",
  "libraries/devloop/liveness/timeout.lua",
  "libraries/devloop/liveness_scan.lua",
  "libraries/devloop/logging.lua",
  "libraries/devloop/markers.lua",
  "libraries/devloop/markers/builders.lua",
  "libraries/devloop/markers/facts.lua",
  "libraries/devloop/markers/shared.lua",
  "libraries/devloop/merge_batch.lua",
  "libraries/devloop/merge_gate_wait.lua",
  "libraries/devloop/merge_queue.lua",
  "libraries/devloop/operator_commands.lua",
  "libraries/devloop/parsers.lua",
  "libraries/devloop/parsers/issue.lua",
  "libraries/devloop/parsers/misc.lua",
  "libraries/devloop/parsers/pr.lua",
  "libraries/devloop/parsers/shared.lua",
  "libraries/devloop/payloads.lua",
  "libraries/devloop/payloads/board.lua",
  "libraries/devloop/payloads/builders.lua",
  "libraries/devloop/payloads/predicates.lua",
  "libraries/devloop/payloads/shared.lua",
  "libraries/devloop/pr_safety.lua",
  "libraries/devloop/prompts.lua",
  "libraries/devloop/queue.lua",
  "libraries/devloop/queue_starvation.lua",
  "libraries/devloop/replayer.lua",
  "libraries/devloop/requests.lua",
  "libraries/devloop/requests/bodies.lua",
  "libraries/devloop/requests/labels.lua",
  "libraries/devloop/requests/lifecycle.lua",
  "libraries/devloop/requests/review.lua",
  "libraries/devloop/requests/shared.lua",
  "libraries/devloop/restart.lua",
  "libraries/devloop/restart/issue/pr_partition_contract.lua",
  "libraries/devloop/restart/issue/transitions/awaiting_pr.lua",
  "libraries/devloop/restart/issue/transitions/blocked.lua",
  "libraries/devloop/restart/issue/transitions/dependency_wait.lua",
  "libraries/devloop/restart/issue/transitions/impl_failed.lua",
  "libraries/devloop/restart/issue/transitions/implementing.lua",
  "libraries/devloop/restart/issue/transitions/index.lua",
  "libraries/devloop/restart/issue/transitions/merged.lua",
  "libraries/devloop/restart/issue/transitions/ready.lua",
  "libraries/devloop/restart/issue/transitions/thinking.lua",
  "libraries/devloop/restart/issue_lifecycle.lua",
  "libraries/devloop/restart_actionable_epoch.lua",
  "libraries/devloop/restart_responsibility_contract.lua",
  "libraries/devloop/rounds.lua",
  "libraries/devloop/state.lua",
  "libraries/devloop/strings.lua",
  "libraries/devloop/sweep_bounds.lua",
  "libraries/devloop/validators.lua",
  "libraries/devloop/validators/fixing.lua",
  "libraries/devloop/validators/intake_candidate.lua",
  "libraries/devloop/validators/issue.lua",
  "libraries/devloop/validators/merge_ready.lua",
  "libraries/devloop/validators/pr.lua",
  "libraries/devloop/validators/pr_review_unresolved.lua",
  "libraries/devloop/validators/ready.lua",
  "libraries/devloop/validators/result.lua",
  "libraries/devloop/validators/review_meta.lua",
  "libraries/devloop/validators/review_result.lua",
  "libraries/devloop/validators/reviewing.lua",
  "libraries/devloop/validators/unresolved.lua",
  "libraries/devloop/validators/validate_proposal.lua",
  "packages/github-devloop-pr/core.lua",
  "packages/github-devloop-pr/core/devloop_wiring.lua",
  "packages/github-devloop-pr/core/merge_ci_wait.lua",
  "packages/github-devloop-pr/core/merge_executor.lua",
  "packages/github-devloop-pr/core/merge_runtime_files.lua",
  "packages/github-devloop-pr/core/pr_label_requests.lua",
  "packages/github-devloop-pr/core/pr_review_replayer.lua",
  "packages/github-devloop-pr/core/restart/liveness_signal_producers/index.lua",
  "packages/github-devloop-pr/core/restart/liveness_signal_producers/merge_gate_wait.lua",
  "packages/github-devloop-pr/core/restart/liveness_signal_producers/review_converge_round.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/fix_reflection.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/index.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/merge_gate.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/merge_gate_wait.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/merge_ready.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/merged.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/merging.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/pr_delegation.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/pr_link.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/review_carry_over.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/review_converge_round.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/review_meta.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/review_result.lua",
  "packages/github-devloop-pr/core/restart/marker_fields/state.lua",
  "packages/github-devloop-pr/core/restart/required_replay_payload_fields/fixing.lua",
  "packages/github-devloop-pr/core/restart/required_replay_payload_fields/index.lua",
  "packages/github-devloop-pr/core/restart/transitions/blocked.lua",
  "packages/github-devloop-pr/core/restart/transitions/closed_unmerged.lua",
  "packages/github-devloop-pr/core/restart/transitions/fixing.lua",
  "packages/github-devloop-pr/core/restart/transitions/index.lua",
  "packages/github-devloop-pr/core/restart/transitions/merge_ready.lua",
  "packages/github-devloop-pr/core/restart/transitions/merged.lua",
  "packages/github-devloop-pr/core/restart/transitions/merging.lua",
  "packages/github-devloop-pr/core/restart/transitions/pr_open.lua",
  "packages/github-devloop-pr/core/restart/transitions/review_meta.lua",
  "packages/github-devloop-pr/core/restart/transitions/reviewing.lua",
  "packages/github-devloop-pr/core/review_carry_over.lua",
  "packages/github-devloop-pr/core/review_meta_requests.lua",
  "packages/github-devloop-pr/core/review_redrive.lua",
  "packages/github-devloop-pr/departments/comment_handoff/main.lua",
  "packages/github-devloop-pr/departments/fix/main.lua",
  "packages/github-devloop-pr/departments/liveness_scan/main.lua",
  "packages/github-devloop-pr/departments/merge/main.lua",
  "packages/github-devloop-pr/departments/merge_queue/main.lua",
  "packages/github-devloop-pr/departments/observe_pr/main.lua",
  "packages/github-devloop-pr/departments/reconcile/main.lua",
  "packages/github-devloop-pr/departments/review_loop/main.lua",
  "packages/github-devloop-pr/departments/review_meta/main.lua",
  "packages/github-devloop-pr/departments/review_pr/main.lua",
  "packages/github-devloop-pr/departments/review_result/main.lua",
  "packages/github-devloop-pr/prompts/fix.lua",
  "packages/github-devloop-pr/prompts/fix_reflection.lua",
  "packages/github-devloop-pr/prompts/review_meta.lua",
  "packages/github-devloop-pr/raisers/liveness_poll.lua",
  "packages/github-devloop-pr/raisers/merge_queue_poll.lua",
  "packages/github-devloop/core.lua",
  "packages/github-devloop/core/awaiting_pr_replayer.lua",
  "packages/github-devloop/core/dependencies.lua",
  "packages/github-devloop/core/devloop_wiring.lua",
  "packages/github-devloop/core/" .. "gates/child_start_visible.lua",
  "packages/github-devloop/core/github_graphql.lua",
  "packages/github-devloop/core/impl_failure.lua",
  "packages/github-devloop/core/implement_attempt.lua",
  "packages/github-devloop/core/liveness_bounds.lua",
  "packages/github-devloop/core/pr_delegation.lua",
  "packages/github-devloop/core/ratchet_slice_ledger.lua",
  "packages/github-devloop/core/ready_split.lua",
  "packages/github-devloop/core/reconcile_requests.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/child_state.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/converge_round.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/dependency_wait.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/implement_attempt.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/index.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/merge_gate_wait.lua",
  "packages/github-devloop/core/restart/liveness_signal_producers/review_converge_round.lua",
  "packages/github-devloop/core/restart/marker_fields/autonomy_result.lua",
  "packages/github-devloop/core/restart/marker_fields/converge_round.lua",
  "packages/github-devloop/core/restart/marker_fields/decomposed.lua",
  "packages/github-devloop/core/restart/marker_fields/dependency_cycle.lua",
  "packages/github-devloop/core/restart/marker_fields/dependency_release.lua",
  "packages/github-devloop/core/restart/marker_fields/dependency_unresolvable.lua",
  "packages/github-devloop/core/restart/marker_fields/dependency_void.lua",
  "packages/github-devloop/core/restart/marker_fields/dependency_wait.lua",
  "packages/github-devloop/core/restart/marker_fields/dependency_waiver.lua",
  "packages/github-devloop/core/restart/marker_fields/fix_reflection.lua",
  "packages/github-devloop/core/restart/marker_fields/impl_failure.lua",
  "packages/github-devloop/core/restart/marker_fields/implement_attempt.lua",
  "packages/github-devloop/core/restart/marker_fields/implementing.lua",
  "packages/github-devloop/core/restart/marker_fields/index.lua",
  "packages/github-devloop/core/restart/marker_fields/merge_gate.lua",
  "packages/github-devloop/core/restart/marker_fields/merge_gate_wait.lua",
  "packages/github-devloop/core/restart/marker_fields/merge_ready.lua",
  "packages/github-devloop/core/restart/marker_fields/merged.lua",
  "packages/github-devloop/core/restart/marker_fields/merging.lua",
  "packages/github-devloop/core/restart/marker_fields/pr_delegation.lua",
  "packages/github-devloop/core/restart/marker_fields/pr_link.lua",
  "packages/github-devloop/core/restart/marker_fields/review_carry_over.lua",
  "packages/github-devloop/core/restart/marker_fields/review_converge_round.lua",
  "packages/github-devloop/core/restart/marker_fields/review_meta.lua",
  "packages/github-devloop/core/restart/marker_fields/review_result.lua",
  "packages/github-devloop/core/restart/marker_fields/state.lua",
  "packages/github-devloop/core/restart/required_replay_payload_fields/fixing.lua",
  "packages/github-devloop/core/restart/required_replay_payload_fields/index.lua",
  "packages/github-devloop/departments/comment_handoff/main.lua",
  "packages/github-devloop/departments/consensus_result/main.lua",
  "packages/github-devloop/departments/execute_start/main.lua",
  "packages/github-devloop/departments/implement/main.lua",
  "packages/github-devloop/departments/implement/pr_child_handoff.lua",
  "packages/github-devloop/departments/implement/slice_gate.lua",
  "packages/github-devloop/departments/implement/substrate_pin.lua",
  "packages/github-devloop/departments/implement/transitions.lua",
  "packages/github-devloop/departments/implement/worktree.lua",
  "packages/github-devloop/departments/liveness_scan/main.lua",
  "packages/github-devloop/departments/loop/main.lua",
  "packages/github-devloop/departments/observe_issue/main.lua",
  "packages/github-devloop/departments/reconcile/main.lua",
  "packages/github-devloop/departments/test_board_digest_probe/main.lua",
  "packages/github-devloop/departments/test_cache_seed/main.lua",
  "packages/github-devloop/departments/test_context_bundle_probe/main.lua",
  "packages/github-devloop/prompts/fix.lua",
  "packages/github-devloop/prompts/fix_reflection.lua",
  "packages/github-devloop/prompts/implement.lua",
  "packages/github-devloop/prompts/review_meta.lua",
  "packages/github-devloop/raisers/liveness_poll.lua",
}

local function normalize_path(path)
  return tostring(path or ""):gsub("\\", "/"):gsub("/+$", "")
end

local function strip_suffix(path, suffix)
  local value = normalize_path(path)
  if value:sub(-#suffix) == suffix then
    return value:sub(1, #value - #suffix)
  end
  return nil
end

local function debug_source_path(fn)
  if type(debug) ~= "table" or type(debug.getinfo) ~= "function" then
    return nil
  end
  local info = debug.getinfo(fn or 1, "S")
  local source = info and info.source or ""
  if source:sub(1, 1) == "@" then
    return normalize_path(source:sub(2))
  end
  return nil
end

local function current_package_root()
  return strip_suffix(debug_source_path(1), "/core/span_conformance.lua")
end

local function devloop_library_root()
  local ok, base = pcall(require, "devloop.base")
  if not ok or type(base) ~= "table" then
    return nil
  end
  return strip_suffix(debug_source_path(base.install), "/base.lua")
end

local function sibling_package_root(owner_root, package_name)
  local parent = normalize_path(owner_root):match("^(.*)/[^/]+$")
  if parent == nil then
    return nil
  end
  return parent .. "/" .. package_name
end

local function path_suffix(path, prefix)
  if path:sub(1, #prefix) == prefix then
    return path:sub(#prefix + 1)
  end
  return nil
end

local function source_roots()
  local owner_root = current_package_root()
  return {
    devloop = devloop_library_root(),
    github_devloop = owner_root,
    github_devloop_pr = owner_root and sibling_package_root(owner_root, "github-devloop-pr") or nil,
  }
end

local function actual_source_path(roots, source_path)
  local suffix = path_suffix(source_path, "libraries/devloop/")
  if suffix ~= nil and roots.devloop ~= nil then
    return roots.devloop .. "/" .. suffix
  end
  suffix = path_suffix(source_path, "packages/github-devloop-pr/")
  if suffix ~= nil and roots.github_devloop_pr ~= nil then
    return roots.github_devloop_pr .. "/" .. suffix
  end
  suffix = path_suffix(source_path, "packages/github-devloop/")
  if suffix ~= nil and roots.github_devloop ~= nil then
    return roots.github_devloop .. "/" .. suffix
  end
  return source_path
end

local function collect_source_paths()
  local paths = {}
  for _, path in ipairs(SCANNED_SOURCE_FILES) do
    table.insert(paths, path)
  end
  return paths
end

local function read_sources(paths)
  local sources = {}
  local roots = source_roots()
  for _, path in ipairs(paths or {}) do
    local actual_path = actual_source_path(roots, path)
    if file.exists(actual_path) then
      sources[path] = file.read(actual_path)
    end
  end
  return sources
end

local function partition_sources(sources)
  local transition_sources = {}
  local department_sources = {}
  for path, source in pairs(sources or {}) do
    if path:find("/core/restart/transitions/", 1, true) ~= nil
      or path:find("/restart/issue/transitions/", 1, true) ~= nil then
      transition_sources[path] = source
    end
    if path:find("/departments/", 1, true) ~= nil then
      department_sources[path] = source
    end
  end
  return transition_sources, department_sources
end

local function record(id, message)
  return { id = id, message = message }
end

local function span_declaration_errors(core)
  local out = {}
  for _, message in ipairs(m_rrc.strict_restart_responsibility_contract_errors(core, core.restart_transition_table())) do
    if tostring(message):find("span_contract", 1, true) ~= nil then
      table.insert(out, record("gspan.span-contract", tostring(message)))
    end
  end
  for _, message in ipairs(hidden_state_conformance.hidden_state_conformance_errors(core)) do
    table.insert(out, record("gspan.hidden-state", tostring(message)))
  end
  return out
end

function S.errors_from_sources(sources)
  local out = {}
  local transition_sources, department_sources = partition_sources(sources)
  for _, message in ipairs(completion_fact_name_messages(sources)) do
    table.insert(out, record("gspan.wording", message))
  end
  for _, message in ipairs(spawn_start_messages(transition_sources, department_sources, sources)) do
    table.insert(out, record("gspan.spawn-order", message))
  end
  for _, message in ipairs(dispatch_live_run_dedup_messages(transition_sources, department_sources)) do
    table.insert(out, record("gspan.dispatch-live-run-dedup", message))
  end
  return out
end

function S.source_paths()
  return collect_source_paths()
end

function S.errors(core, paths)
  local out = span_declaration_errors(core)
  for _, error_record in ipairs(S.errors_from_sources(read_sources(paths or S.source_paths()))) do
    table.insert(out, error_record)
  end
  return out
end

function S.install(M)
  function M.span_conformance_errors()
    return S.errors(M)
  end
end

S._completion_fact_name_messages = completion_fact_name_messages
S._spawn_start_messages = spawn_start_messages
S._dispatch_live_run_dedup_messages = dispatch_live_run_dedup_messages
S._span_declaration_errors = span_declaration_errors

return S
