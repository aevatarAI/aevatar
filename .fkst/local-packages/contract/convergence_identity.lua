local I = {}

local function require_value(value, field)
  if value == nil or tostring(value) == "" then
    error("contract.convergence_identity: missing " .. tostring(field))
  end
  return tostring(value)
end

local function nonnegative_integer(value, field)
  if value == nil then
    return 0
  end
  local number = tonumber(value)
  if number == nil or number < 0 or number ~= math.floor(number) then
    error("contract.convergence_identity: invalid " .. tostring(field))
  end
  return number
end

local function lane_value(opts)
  return require_value(type(opts) == "table" and opts.angle_lane or nil, "angle_lane")
end

function I.from_parts(role, proposal_id, dedup_key, opts)
  role = require_value(role, "role")
  proposal_id = require_value(proposal_id, "proposal_id")
  return {
    process = {
      role = role,
      proposal_id = proposal_id,
    },
    role = role,
    proposal_id = proposal_id,
    generation = nonnegative_integer(type(opts) == "table" and opts.generation or nil, "generation"),
    round = nonnegative_integer(type(opts) == "table" and opts.round or nil, "round"),
    angle_lane = lane_value(opts),
    dedup_key = require_value(dedup_key, "dedup_key"),
  }
end

function I.from_proposal(role, proposal, opts)
  if type(proposal) ~= "table" then
    error("contract.convergence_identity: missing proposal")
  end
  opts = opts or {}
  local generation = nonnegative_integer(opts.generation ~= nil and opts.generation or proposal.generation, "generation")
  local round = nonnegative_integer(opts.round ~= nil and opts.round or proposal.round, "round")
  local lane = lane_value(opts)
  role = require_value(role, "role")
  local proposal_id = require_value(proposal.proposal_id, "proposal_id")
  -- Current package liveness keys at process + generation + round + angle lane:
  -- the three consensus angles are distinct lanes and can run in parallel, while
  -- redelivery of the same lane defers to the existing live run. The future
  -- substrate live_run_key admission is the atomic version of this same shape.
  return I.from_parts(
    role,
    proposal_id,
    "convergence:" .. role .. ":" .. proposal_id .. ":g" .. tostring(generation) .. ":r" .. tostring(round) .. ":" .. lane,
    {
      generation = generation,
      round = round,
      angle_lane = lane,
    }
  )
end

return I
