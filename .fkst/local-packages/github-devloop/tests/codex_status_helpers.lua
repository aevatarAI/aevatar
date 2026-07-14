local H = {}

local function nonce()
  return tostring({}):gsub("[^%w._-]", "_")
end

local function json_string(value)
  return tostring(value)
    :gsub("\\", "\\\\")
    :gsub('"', '\\"')
    :gsub("\n", "\\n")
end

local function json_value(value)
  if type(value) == "number" then
    return tostring(value)
  end
  if type(value) == "boolean" then
    return value and "true" or "false"
  end
  if value == nil then
    return "null"
  end
  return '"' .. json_string(value) .. '"'
end

local function json_object(record)
  local parts = {}
  for key, value in pairs(record or {}) do
    table.insert(parts, '"' .. json_string(key) .. '":' .. json_value(value))
  end
  table.sort(parts)
  return "{" .. table.concat(parts, ",") .. "}"
end

local function log_dir(run_opts)
  local root = run_opts and run_opts.env and run_opts.env.FKST_RUNTIME_LOG_DIR
  if root == nil or root == "" then
    error("github-devloop test: FKST_RUNTIME_LOG_DIR is required to seed codex status")
  end
  return root .. "/codex"
end

local function live_run_timing()
  local started = now() - 60
  return os.date("!%Y-%m-%dT%H:%M:%SZ", started),
    started * 1000,
    (now() + 3600) * 1000
end

function H.seed_codex_run(run_opts, record)
  local dir = log_dir(run_opts)
  os.execute("mkdir -p " .. string.format("%q", dir))
  local path = dir .. "/" .. tostring(record.run_id or nonce()) .. ".log"
  local file = assert(io.open(path, "a"))
  file:write("CODEX_STATUS:" .. json_object(record) .. "\n")
  file:close()
  return path
end

function H.seed_implement_codex_run(run_opts, proposal_id, dedup_key, extra)
  local started_at, started_at_ms, lease_expires_at_ms = live_run_timing()
  local record = {
    run_id = nonce(),
    role = "implement",
    dept = "implement",
    proposal_id = proposal_id,
    dedup_key = dedup_key,
    status = "running",
    started_at = started_at,
    started_at_ms = started_at_ms,
    lease_expires_at_ms = lease_expires_at_ms,
    timeout_seconds = 3600,
    log_path = "/tmp/fkst-packages-test/codex.log",
    cmd_line = "codex exec -",
  }
  for key, value in pairs(extra or {}) do
    record[key] = value
  end
  H.seed_codex_run(run_opts, record)
  return record
end

function H.seed_role_codex_run(run_opts, role, proposal_id, dedup_key, extra)
  local started_at, started_at_ms, lease_expires_at_ms = live_run_timing()
  local record = {
    run_id = nonce(),
    role = role,
    dept = role,
    proposal_id = proposal_id,
    dedup_key = dedup_key,
    status = "running",
    started_at = started_at,
    started_at_ms = started_at_ms,
    lease_expires_at_ms = lease_expires_at_ms,
    timeout_seconds = 3600,
    log_path = "/tmp/fkst-packages-test/codex.log",
    cmd_line = "codex exec -",
  }
  for key, value in pairs(extra or {}) do
    record[key] = value
  end
  H.seed_codex_run(run_opts, record)
  return record
end

return H
