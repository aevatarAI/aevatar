local S = {}
local contract_time = require("contract.time")

function S.install(M)
local stall_suspect_threshold_minutes = {
  thinking = 30,
  ready = 30,
  implementing = 90,
  ["pr-open"] = 30,
  reviewing = 60,
  fixing = 90,
  merging = 30,
}

function M.stall_suspect_age_minutes(version, now_seconds)
  local marker_updated_at = M.version_updated_at(version)
  if marker_updated_at == "" then
    return nil
  end
  local marker_seconds = contract_time.iso_timestamp_epoch_seconds(marker_updated_at)
  local current_seconds = tonumber(now_seconds)
  if marker_seconds == nil or current_seconds == nil then
    return nil
  end
  local age_seconds = current_seconds - marker_seconds
  if age_seconds < 0 then
    return nil
  end
  return math.floor(age_seconds / 60)
end

function M.stall_suspect_threshold_minutes(state)
  return stall_suspect_threshold_minutes[state]
end

end

return S
