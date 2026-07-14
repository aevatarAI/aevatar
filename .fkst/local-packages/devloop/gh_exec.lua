local G = {}

local gh_program = table.concat({ "g", "h" })

local function gh_exec_opts(cmd_or_opts, timeout)
  local opts = {}
  if type(cmd_or_opts) == "table" then
    for key, value in pairs(cmd_or_opts) do
      opts[key] = value
    end
  else
    opts.cmd = cmd_or_opts
  end
  opts.timeout = opts.timeout or timeout or 30
  return opts
end

local function normalize_gh_argv_exec_opts(cmd_or_opts, timeout)
  local opts = gh_exec_opts(cmd_or_opts, timeout)
  if type(opts.argv) ~= "table" or opts.argv[1] ~= gh_program then
    error("github-devloop: GitHub exec requires GitHub argv")
  end
  return {
    argv = opts.argv,
    timeout = opts.timeout,
  }
end

function G.gh_exec(cmd_or_opts, timeout, exec)
  local run = exec or exec_argv
  if type(run) ~= "function" then
    error("github-devloop: GitHub exec requires exec_argv")
  end
  return run(normalize_gh_argv_exec_opts(cmd_or_opts, timeout))
end

return G
