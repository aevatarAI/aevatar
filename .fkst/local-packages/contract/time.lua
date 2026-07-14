-- contract.time: dependency-free timestamp helpers shared across packages.
local T = {}

function T.iso_timestamp_epoch_seconds(timestamp)
  local year, month, day, hour, minute, second = tostring(timestamp or ""):match(
    "^(%d%d%d%d)%-(%d%d)%-(%d%d)T(%d%d)[%-:](%d%d)[%-:](%d%d)Z$"
  )
  if year == nil then
    return nil
  end
  year = tonumber(year)
  month = tonumber(month)
  day = tonumber(day)
  hour = tonumber(hour)
  minute = tonumber(minute)
  second = tonumber(second)
  if month < 1 or month > 12
    or day < 1 or day > 31
    or hour > 23
    or minute > 59
    or second > 59 then
    return nil
  end

  local adjusted_year = year
  local adjusted_month = month
  if adjusted_month <= 2 then
    adjusted_year = adjusted_year - 1
    adjusted_month = adjusted_month + 12
  end
  local era = math.floor(adjusted_year / 400)
  local year_of_era = adjusted_year - era * 400
  local day_of_year = math.floor((153 * (adjusted_month - 3) + 2) / 5) + day - 1
  local day_of_era = year_of_era * 365
    + math.floor(year_of_era / 4)
    - math.floor(year_of_era / 100)
    + day_of_year
  local days_since_epoch = era * 146097 + day_of_era - 719468
  return days_since_epoch * 86400 + hour * 3600 + minute * 60 + second
end

return T
