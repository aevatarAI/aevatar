local parsers_misc = require("devloop.parsers.misc")
local devloop_state = require("devloop.state")
local M = {}
local root_ref = nil

local function root()
  return root_ref or M
end

function M.dependency_wait_fact(comments, proposal_id)
  local core = root()
  if type(comments) ~= "table" then
    return nil
  end
  local current = devloop_state.current_state(comments, proposal_id)
  if type(current) ~= "table" or current.version == nil then
    return nil
  end
  local marker_pattern = "<!%-%- fkst:github%-devloop:dependency%-wait:v1.-%-%->"
  for _, comment in ipairs(parsers_misc._trusted_marker_comments(comments)) do
    for marker in parsers_misc._comment_body(comment):gmatch(marker_pattern) do
      local marker_proposal = marker:match('proposal="([^"]+)"')
      local marker_version = marker:match('version="([^"]*)"')
      if marker_proposal == tostring(proposal_id)
        and marker_version == tostring(current.version) then
        return {
          proposal_id = marker_proposal,
          version = marker_version,
          comment_created_at = parsers_misc._comment_created_at(comment),
        }
      end
    end
  end
  return nil
end

function M.install(root_module)
  root_ref = root_module
  root_module.dependency_wait_fact = M.dependency_wait_fact
end

return M
