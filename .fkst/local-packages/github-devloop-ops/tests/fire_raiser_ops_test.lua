local t = fkst.test

local function shell_quote(value)
  return "'" .. tostring(value):gsub("'", "'\"'\"'") .. "'"
end

local function read_command(command)
  local handle = assert(io.popen(command .. " 2>&1"))
  local output = handle:read("*a")
  local ok = handle:close()
  if ok == false or ok == nil then
    error("github-devloop-ops fire_raiser fixture command failed: " .. tostring(command) .. "\n" .. tostring(output))
  end
  return output
end

local function run_command(command)
  read_command(command)
end

local function repo_root()
  return (read_command("pwd"):gsub("%s+$", ""))
end

local function temp_root(name)
  return (read_command("mktemp -d " .. shell_quote("/tmp/fkst-devloop-ops-fire-raiser-" .. tostring(name) .. ".XXXXXX")):gsub("%s+$", ""))
end

local function copy_dir(src, dst)
  run_command("mkdir -p " .. shell_quote(dst))
  run_command("cp -R " .. shell_quote(src) .. "/. " .. shell_quote(dst) .. "/")
end

local function remove_dir(path)
  run_command("rm -rf " .. shell_quote(path))
end

local function write_file(path, body)
  file.write(path, body)
end

local function copy_package_without_tests(source, root, package_name)
  copy_dir(source .. "/packages/" .. package_name, root .. "/packages/" .. package_name)
  remove_dir(root .. "/packages/" .. package_name .. "/tests")
end

local function write_stub_package(root, package_name)
  run_command("mkdir -p " .. shell_quote(root .. "/packages/" .. package_name))
  write_file(root .. "/packages/" .. package_name .. "/fkst.toml", [[
kind = "package"
name = "]] .. package_name .. [["

[code]
root = "."

[lib_deps]
libraries = ["workflow"]
]])
end

local function write_stub_department(root, package_name, department_name, spec)
  local dir = root .. "/packages/" .. package_name .. "/departments/" .. department_name
  run_command("mkdir -p " .. shell_quote(dir))
  write_file(dir .. "/main.lua", [[
local saga = require("workflow.saga")

local spec = ]] .. spec .. [[

return saga.department(spec, {
  name = "]] .. department_name .. [[",
  done = function(_event)
    return false
  end,
  act = function(_event)
  end,
})
]])
end

local function setup_stub_siblings(root)
  write_stub_package(root, "github-devloop")
  write_stub_department(root, "github-devloop", "comment_handoff", [[{
  consumes = { "devloop_comment_written" },
  produces = {},
  published_seam = { "devloop_comment_written" },
  stall_window = "30s",
}]])

  write_stub_package(root, "github-proxy")
  write_stub_department(root, "github-proxy", "github_issue_create", [[{
  consumes = { "github_issue_create_request" },
  produces = {},
  published_seam = { "github_issue_create_request" },
  stall_window = "30s",
}]])
  write_stub_department(root, "github-proxy", "github_comment", [[{
  consumes = { "github_issue_comment_request" },
  produces = { "github_comment_written" },
  published_seam = { "github_issue_comment_request" },
  stall_window = "30s",
}]])
  write_stub_department(root, "github-proxy", "github_pr_comment", [[{
  consumes = { "github_pr_comment_request" },
  produces = { "github_comment_written" },
  published_seam = { "github_pr_comment_request" },
  stall_window = "30s",
}]])
  write_stub_department(root, "github-proxy", "github_issue_label", [[{
  consumes = { "github_issue_label_request" },
  produces = {},
  published_seam = { "github_issue_label_request" },
  stall_window = "30s",
}]])

  write_stub_package(root, "consensus")
  write_stub_department(root, "consensus", "decide", [[{
  consumes = { "proposal" },
  produces = { "consensus_reached", "consensus_converge" },
  published_seam = { "proposal" },
  stall_window = "30s",
}]])

  write_stub_package(root, "github-devloop-decompose")
  write_stub_department(root, "github-devloop-decompose", "decompose", [[{
  consumes = { "devloop_decompose" },
  produces = {},
  published_seam = { "devloop_decompose" },
  stall_window = "30s",
}]])
end

local function copy_test_helper(source, root, package_name, helper_name)
  run_command("mkdir -p " .. shell_quote(root .. "/packages/" .. package_name .. "/tests"))
  run_command("cp " .. shell_quote(source .. "/packages/" .. package_name .. "/tests/" .. helper_name)
    .. " " .. shell_quote(root .. "/packages/" .. package_name .. "/tests/" .. helper_name))
end

local function setup_workspace(name, child_test)
  local root = temp_root(name)
  local source = repo_root()
  write_file(root .. "/fkst.workspace.toml", '[workspace]\nunits = ["packages/*", "libraries/*"]\n')
  for _, lib in ipairs({ "contract", "workflow", "testkit", "forge", "devloop" }) do
    copy_dir(source .. "/libraries/" .. lib, root .. "/libraries/" .. lib)
  end
  copy_package_without_tests(source, root, "github-devloop-ops")
  setup_stub_siblings(root)
  run_command("mkdir -p " .. shell_quote(root .. "/packages/github-devloop-ops/tests"))
  copy_test_helper(source, root, "github-devloop-ops", "entity_read_mock_helpers.lua")
  write_file(root .. "/packages/github-devloop-ops/tests/fire_raiser_child_test.lua", child_test)
  return root
end

local function framework_bin()
  local bin = os.getenv("BIN") or "/Users/auric/fkst-substrate/target/debug/fkst-framework"
  if bin == "" then
    error("github-devloop-ops fire_raiser fixture requires BIN")
  end
  return bin
end

local function run_child(root)
  local command = table.concat({
    "FKST_RUNTIME_ROOT=" .. shell_quote(root .. "/runtime"),
    "FKST_DURABLE_ROOT=" .. shell_quote(root .. "/durable"),
    shell_quote(framework_bin()),
    "test",
    "--project-root",
    shell_quote(root .. "/packages/github-devloop-ops"),
    "--package-root",
    shell_quote(root .. "/packages/github-devloop-ops"),
    "--package-root",
    shell_quote(root .. "/packages/github-devloop"),
    "--package-root",
    shell_quote(root .. "/packages/github-proxy"),
    "--package-root",
    shell_quote(root .. "/packages/consensus"),
    "--package-root",
    shell_quote(root .. "/packages/github-devloop-decompose"),
  }, " ")
  return read_command(command)
end

local function fire_raiser_child(body)
  return [=[
local t = fkst.test
local core = require("core")
local entity_read_mocks = require("tests.entity_read_mock_helpers")
local gh_argv = require("testkit.gh_argv_mock")
gh_argv.install(t, core)

local function opts(name)
  return {
    env = {
      FKST_RUNTIME_ROOT = "/tmp/fkst-packages-test/github-devloop-ops/fire-raiser-child/" .. tostring(now()) .. "/" .. tostring(name),
      FKST_GITHUB_REPO = "owner/repo",
      FKST_GITHUB_BOT_LOGIN = "fkst-test-bot",
      FKST_GITHUB_WRITE = "",
      FKST_DEVLOOP_UPSTREAM_BRANCH = "dev",
      FKST_DEVLOOP_INTEGRATION_BRANCH = "integration/dev",
    },
  }
end

local function mock_env(reads)
  reads = reads or 8
  for _ = 1, reads do
    t.mock_command('printf %s "$FKST_GITHUB_REPO"', { stdout = "owner/repo", stderr = "", exit_code = 0 })
    t.mock_command('printf %s "$FKST_GITHUB_BOT_LOGIN"', { stdout = "fkst-test-bot", stderr = "", exit_code = 0 })
    t.mock_command('printf %s "$FKST_GITHUB_WRITE"', { stdout = "", stderr = "", exit_code = 0 })
  end
  t.mock_command('printf %s "$FKST_DEVLOOP_UPSTREAM_BRANCH"', { stdout = "dev", stderr = "", exit_code = 0 })
  t.mock_command('printf %s "$FKST_DEVLOOP_INTEGRATION_BRANCH"', { stdout = "integration/dev", stderr = "", exit_code = 0 })
  t.mock_command('printf %s "$FKST_DEVLOOP_ROLLUP_MERGE"', { stdout = "", stderr = "", exit_code = 0 })
  for _, name in ipairs({ "GH_TOKEN", "GITHUB_TOKEN" }) do
    t.mock_command('if [ -n "${' .. name .. ':-}" ]; then printf present; fi', { stdout = "", stderr = "", exit_code = 0 })
  end
end

local function mock_ensure_repo_reads()
  t.mock_command("gh api --paginate --slurp 'repos/owner/repo/labels?per_page=100'", {
    stdout = "[" .. "[]" .. "]\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("gh api --paginate --slurp 'repos/owner/repo/issues?state=open&per_page=100'", {
    stdout = "[" .. "[]" .. "]\n",
    stderr = "",
    exit_code = 0,
  })
  t.mock_command("git fetch 'origin' 'integration/dev'", {
    stdout = "",
    stderr = "fatal: couldn't find remote ref integration/dev\n",
    exit_code = 1,
  })
  t.mock_command("git rev-parse --verify refs/remotes/'origin'/'integration/dev'^{commit}", {
    stdout = "",
    stderr = "fatal: needed a single revision\n",
    exit_code = 1,
  })
end

local function observe_issue_list_command(label)
  return core.gh_issue_list_observe_cmd("owner/repo", label, 1, true)
end

local function mock_observability_empty_reads()
  t.mock_command(observe_issue_list_command(core._enabled_label), { stdout = "[]\n", stderr = "", exit_code = 0 })
  for _, state in ipairs(core.issue_state_order()) do
    t.mock_command(observe_issue_list_command(core.state_label(state)), { stdout = "[]\n", stderr = "", exit_code = 0 })
  end
  t.mock_command(core.gh_pr_list_observe_cmd("owner/repo", 1, true), { stdout = "[]\n", stderr = "", exit_code = 0 })
  t.mock_command(core.gh_pr_list_recent_merged_cmd("owner/repo", core.observability_limits().entity_cap), { stdout = "[]\n", stderr = "", exit_code = 0 })
  t.mock_command(core.gh_issue_list_recent_closed_cmd("owner/repo", core.observability_limits().entity_cap), { stdout = "[]\n", stderr = "", exit_code = 0 })
  t.mock_command(core.gh_dashboard_label_get_cmd("owner/repo", core.dashboard_label()), {
    stdout = '{"name":"fkst-dashboard"}\n',
    stderr = "",
    exit_code = 0,
  })
  t.mock_command(core.gh_dashboard_issue_list_cmd("owner/repo", core.dashboard_label()), {
    stdout = "[" .. "[]" .. "]\n",
    stderr = "",
    exit_code = 0,
  })
  entity_read_mocks.mock_issue_view_selector(t, {
    number = 0,
    title = "unused",
    comments = {},
  }, "title,body,comments,state,stateReason,assignees,author")
end

return {
]=] .. body .. [=[
}
]=]
end

return {
  test_fire_raiser_ops_poll_raisers_route_real_ticks = function()
    local root = setup_workspace("ops", fire_raiser_child([[
  test_fire_raiser_doctor_poll_routes_real_tick_to_doctor = function()
    mock_env()
    t.mock_command(core.gh_issue_list_observe_cmd("owner/repo", core._enabled_label, 1, true), { stdout = "[]\\n", stderr = "", exit_code = 0 })
    t.mock_command("gh api --paginate --slurp 'repos/owner/repo/issues?state=open&per_page=100'", { stdout = "[" .. "[]" .. "]\n", stderr = "", exit_code = 0 })
    t.mock_command(core.gh_pr_list_observe_cmd("owner/repo", 1, true), { stdout = "[]\\n", stderr = "", exit_code = 0 })
    t.mock_command("gh api --paginate --slurp 'repos/owner/repo/pulls?state=open&per_page=100'", { stdout = "[" .. "[]" .. "]\n", stderr = "", exit_code = 0 })

    local trace = t.fire_raiser("doctor_poll")

    t.eq(trace.source_ref.kind, "cron")
    t.eq(trace.source_payload.raiser, "github-devloop-ops.doctor_poll")
    t.eq(trace.routed_to[1], "github-devloop-ops.doctor")
    if trace.consumer_result.status ~= "accepted" then
      error(trace.consumer_result.message or "fire_raiser consumer failed")
    end
    t.eq(trace.consumer_result.status, "accepted")
    t.eq(#trace.raised, 0)
  end,

  test_fire_raiser_ensure_repo_poll_routes_real_tick_to_ensure_repo = function()
    mock_env()
    mock_ensure_repo_reads()

    local trace = t.fire_raiser("ensure_repo_poll")

    t.eq(trace.source_ref.kind, "cron")
    t.eq(trace.source_payload.raiser, "github-devloop-ops.ensure_repo_poll")
    t.eq(trace.routed_to[1], "github-devloop-ops.ensure_repo")
    if trace.consumer_result.status ~= "accepted" then
      error(trace.consumer_result.message or "fire_raiser consumer failed")
    end
    t.eq(trace.consumer_result.status, "accepted")
    t.eq(#trace.raised, 0)
  end,

  test_fire_raiser_observability_poll_routes_real_tick_to_observability = function()
    mock_env(16)
    mock_observability_empty_reads()

    local trace = t.fire_raiser("observability_poll")

    t.eq(trace.source_ref.kind, "cron")
    t.eq(trace.source_payload.raiser, "github-devloop-ops.observability_poll")
    t.eq(trace.routed_to[1], "github-devloop-ops.observability")
    if trace.consumer_result.status ~= "accepted" then
      error(trace.consumer_result.message or "fire_raiser consumer failed")
    end
    t.eq(trace.consumer_result.status, "accepted")
    t.eq(#trace.raised, 0)
  end,
]]))
    local output = run_child(root)
    t.is_true(output:find("3 passed, 0 failed", 1, true) ~= nil, output)
  end,
}
