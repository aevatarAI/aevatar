local env = require("workflow.env")
local t = fkst.test

local allowed_env = {
  FKST_OUTPUT_LANG = true,
}

local function read_env_command(name)
  if not allowed_env[name] then
    error("env name is not allowed")
  end
  return 'printf %s "$' .. name .. '"'
end

return {
  test_read_env_returns_present_stdout = function()
    local value = env.read_env("FKST_OUTPUT_LANG", function(cmd)
      t.eq(cmd, 'printf %s "$FKST_OUTPUT_LANG"')
      return { stdout = "en", stderr = "", exit_code = 0 }
    end, read_env_command)

    t.eq(value, "en")
  end,

  test_read_env_returns_nil_for_absent_empty_and_failed_values = function()
    t.is_nil(env.read_env("FKST_OUTPUT_LANG", function(_cmd)
      return { stdout = "", stderr = "", exit_code = 0 }
    end, read_env_command))

    t.is_nil(env.read_env("FKST_OUTPUT_LANG", function(_cmd)
      return { stdout = "en", stderr = "", exit_code = 1 }
    end, read_env_command))

    t.is_nil(env.read_env("FKST_OUTPUT_LANG", nil, read_env_command))
  end,

  test_read_env_returns_nil_when_exec_fails = function()
    t.is_nil(env.read_env("FKST_OUTPUT_LANG", function(_cmd)
      error("exec failed")
    end, read_env_command))
  end,

  test_read_env_preserves_command_builder_allowlist_errors = function()
    t.raises(function()
      env.read_env("HOME", function(_cmd)
        return { stdout = "", stderr = "", exit_code = 0 }
      end, read_env_command)
    end)
  end,

  test_read_env_binds_command_builder = function()
    local read_env = env.read_env(read_env_command)

    t.eq(read_env("FKST_OUTPUT_LANG", function(cmd)
      t.eq(cmd, 'printf %s "$FKST_OUTPUT_LANG"')
      return { stdout = "zh", stderr = "", exit_code = 0 }
    end), "zh")
  end,

  test_bound_read_env_preserves_erroring_exec_contract = function()
    local read_env = env.read_env(read_env_command, {
      missing_exec_error = "read_env requires exec_sync",
      propagate_exec_errors = true,
    })

    t.raises(function()
      read_env("FKST_OUTPUT_LANG", nil)
    end)

    t.raises(function()
      read_env("HOME", function(_cmd)
        return { stdout = "", stderr = "", exit_code = 0 }
      end)
    end)

    t.is_nil(read_env("FKST_OUTPUT_LANG", function(_cmd)
      return { stdout = "en", stderr = "", exit_code = 1 }
    end))
  end,
}
