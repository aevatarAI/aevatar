-- contract.payload: small, dependency-free payload validators shared across packages.
local P = {}

function P.require_field(payload, name, ctx)
  local value = payload[name]
  if value == nil or value == "" then
    error(tostring(ctx) .. ": missing " .. name)
  end
  return value
end

return P
