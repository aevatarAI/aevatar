local t = fkst.test

local function install_shared(model, resolved)
  return require("workflow.liveness.shared").install(model, resolved)
end

return {
  test_shared_uses_resolved_restart_package_name_value_for_defaulted_error_context = function()
    local ok, err = pcall(function()
      install_shared({}, {
        restart_package_name = "resolved-package",
      })
    end)

    t.eq(ok, false)
    t.is_true(tostring(err):find("resolved-package: missing resolved liveness_signal_producers", 1, true) ~= nil, tostring(err))
  end,

  test_shared_uses_resolved_restart_source_root_value_for_source_contains = function()
    local shared = install_shared({}, {
      restart_source_root = "packages/github-devloop/",
      liveness_signal_producers = {},
    })

    t.eq(shared.source_contains("core.lua", 'M.restart_package_name = "github-devloop"'), true)
  end,
}
