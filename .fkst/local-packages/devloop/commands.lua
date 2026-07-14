local S = {}

local modules = {
  "devloop.commands.support",
  "devloop.commands.validators",
  "devloop.commands.issue_reads",
  "devloop.commands.observe_lists",
  "devloop.commands.prs",
  "devloop.commands.git_ops",
}

function S.install(M)
  for _, module_name in ipairs(modules) do
    require(module_name).install(M)
  end
end

return S
