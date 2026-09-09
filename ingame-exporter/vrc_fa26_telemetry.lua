-- VRC FA26 Telemetry exporter
-- Reads the car's private CAN/ECU channels (same source as the cockpit dash) and mirrors
-- them into a shared memory-mapped file for the SimHub plugin to read.
-- Read-only: does NOT modify any car file -> league checksums unaffected.
-- Contract: the fixed struct LAYOUT below is the channel reference; field order/types must
-- match the SimHub plugin (VrcData) exactly.

local MMF_NAME = 'AcTools.CSP.VRC_FA26.v1'
local CAR_PREFIX = 'vrc_formula_alpha_2026'

-- Fixed C-struct layout. Field ORDER + TYPES must match the SimHub plugin and the reader.
-- All fields are 4 bytes (int32_t / float) -> tight packing, no padding. bool -> int32 (0/1).
local LAYOUT = [[
  uint32_t counter;
  int32_t connected;

  int32_t strat;
  int32_t puMode;
  int32_t deploymentStrat;
  int32_t deploymentSplit;
  float kersInput;
  float kersCharge;
  float kersRegen;
  float kersDeployMJ;
  float kersRegenMJ;
  float kersRegenLimitMJ;
  float kersChargeDelta;
  float kersChargeLast;
  float kersChargeESOC;
  float rearMotorPowerKW;
  float rearMotorTorque;
  float mgukMaxPower;
  float mgukMaxPowerLimit;
  float mgukMaxPowerReduction;
  int32_t mgukMaxPowerReset;
  float mgukMaxPowerResetSpeedThreshold;
  int32_t isOvertakeActive;
  int32_t isOvertakeActivePending;
  int32_t isHybridBoostActive;
  int32_t isHybridAntiActive;
  int32_t isPowerLimited;
  int32_t isPowerLimitedPending;
  float powerLimitedExitTorque;
  float puTemperature;
  float puTorque;

  int32_t drsLatch;
  int32_t drsMode;
  int32_t drsAvailable;
  int32_t drsEnabledLap;

  float frontBias;
  float brakeBiasLive;
  float brakeMigration;
  float brakeBiasTargetDelta;
  int32_t brakeShapeMap;
  int32_t brakeBalanceMap;
  float brakePressure;
  float ebb;

  int32_t diffEntry;
  int32_t diffMid;
  int32_t diffExit;
  int32_t diffPower;
  int32_t diffCoast;

  int32_t fuelMap;
  int32_t pedalMap;
  int32_t torqueMap;
  int32_t engineBrake;
  float fuelSavedLastLap;
  float fuelDelta;
  float fuelLevel;
  float fuelKgPerLiter;
  float engineLifeLeft;

  float torqueDemand;
  float torqueDriverDemand;
  float torqueOut;
  float throttleRaw;
  float throttlePedal;

  int32_t targetLapTimeMs;
  int32_t targetStintLaps;
  int32_t lapDeltaMode;
  int32_t lapTimeDelta;

  int32_t isEngineRunning;
  int32_t isAntistallActive;
  int32_t isIgnitionStage1;
  int32_t isIgnitionStage2;
  int32_t isSteeringWheelAttached;
  int32_t isSteeringWheelBooted;
  int32_t isStarterCranking;
  int32_t isPitLimiterActive;
  int32_t isConstantSpeedLimiterActive;
  int32_t isBrakeMagicActive;
  int32_t isFirstGearShift;
  float pitSpeedLimit;
  float launchLoadPerc;
  int32_t driverPreset1Active;
  int32_t driverPreset2Active;
  int32_t driverPresetActiveIndex;

  float plankWear;
  float frontLegalityWear;
  float midLegalityWear;
  float rearLegalityWear;

  int32_t aeroRearWingGurney;
  int32_t aeroLouvers;
  int32_t aeroFrontWingDamage;
  int32_t tyreCompoundRange;
  int32_t compoundIndex;

  int32_t tyreTempDeltaFL;
  int32_t tyreTempDeltaFR;
  int32_t tyreTempDeltaRL;
  int32_t tyreTempDeltaRR;
  int32_t brakeDiscTempFL;
  int32_t brakeDiscTempFR;
  int32_t brakeDiscTempRL;
  int32_t brakeDiscTempRR;

  int32_t displayOption1;
  int32_t displayOption2;
  int32_t displayOption3;
  int32_t displayPopupTime;
  int32_t displayLapPopupTime;
  int32_t displayBacklightBrightness;
  int32_t ledShiftPattern;
  int32_t ledBrightness;
  int32_t lightState;

  int32_t lapCount;
  int32_t sessionLaps;
  int32_t lapsRemaining;
  int32_t previousLapTimeMs;
  int32_t bestLapTimeMs;
  int32_t estimatedLapTimeMs;
  int32_t referenceLapTimeMs;
  float perfDelta;
  float perfDeltaLastLapSaved;
  float gapToCarAhead;
  float headwindKmh;
  int32_t isCaution;

  int32_t nearby1Name;
  float nearby1Delta;
  int32_t nearby2Name;
  float nearby2Delta;
  int32_t nearby3Name;
  float nearby3Delta;
]]

local mmf = ac.writeMemoryMappedFile(MMF_NAME, LAYOUT)

local sim = ac.getSim()
local inputs = nil
local counter = 0

local clamp = math.clamp
local floor = math.floor
local round = math.round
local abs = math.abs

-- Reconnect to the car's private CAN channel map (name -> {index, isBool}).
-- 2026 stores the struct flat; 2025 wrapped it in an array. Handle both.
local function tryConnect()
  local id = ac.getCarID(0)
  if not id or not string.startsWith(id, CAR_PREFIX) then return false end

  local s = ac.load(id .. '_CAN')
  if type(s) ~= 'string' or s == '' then return false end

  local ok, parsed = pcall(stringify.parse, s)
  if not ok or type(parsed) ~= 'table' then return false end

  local root = parsed.inputs and parsed or (type(parsed[1]) == 'table' and parsed[1] or nil)
  if not root or type(root.inputs) ~= 'table' then return false end

  inputs = root.inputs
  return true
end

-- Read a live channel by name (0 if unavailable). Values are taken raw: a few channels are
-- flagged boolean in the map but actually carry a float (powerLimitedExitTorque), so the
-- flag is deliberately ignored here and the SimHub side rounds where it matters.
local function rd(cphys, name)
  local m = inputs and inputs[name]
  if not m then return 0 end
  return cphys.scriptControllerInputs[m[1]] or 0
end

-- Fuel display unit, same source as the cockpit (car.ini FUEL_EXT/KG_PER_LITER).
local fuelKgPerLiter = ac.INIConfig.carData(0, 'car.ini'):get('FUEL_EXT', 'KG_PER_LITER', 0.76)

-- Car ahead by race position -- exact replica of display/styles/style_0/data.lua.
local function findCarAheadIndex(playerCar)
  local racePosition = playerCar.racePosition
  if racePosition <= 1 then return nil end

  local target = racePosition - 1
  for i = 0, sim.carsCount - 1 do
    local other = ac.getCar(i)
    if other and other.racePosition == target then return other.index end
  end
  return nil
end

local function headwindSpeed(playerCar)
  local diff = abs(playerCar.compass % 360 - sim.windDirectionDeg % 360)
  if diff > 180 then diff = 360 - diff end
  return -round(sim.windSpeedKmh * math.cos(math.rad(diff)))
end

-- Nearby-driver timing gates -- exact replica of pages.lua (50 m gates, three slots:
-- 1 = car that crossed this gate just before us, 2/3 = cars that crossed just after).
local trackLengthM = sim.trackLengthM > 0 and sim.trackLengthM or 5000
local gateSpacing = 50 / trackLengthM
local gates = {}
local gateCount = 0
for pos = 0, 1, gateSpacing do
  gateCount = gateCount + 1
  gates[gateCount] = { pos = pos }
end

local nearbyIndex = {}
local nearbyDelta = {}
local splineLast = {}
for _, c in ac.iterateCars() do splineLast[c.index] = c.splinePosition end

local function setNearby(slot, driverIndex, delta)
  for other = 1, 3 do
    if other ~= slot and nearbyIndex[other] == driverIndex then
      nearbyIndex[other] = nil
      nearbyDelta[other] = nil
    end
  end
  nearbyIndex[slot] = driverIndex
  nearbyDelta[slot] = delta
end

local function gateAt(splinePosition)
  return gates[floor(splinePosition / gateSpacing) + 1]
end

local function crossed(splinePosition, last, gate)
  return gate and last ~= nil and splinePosition >= gate.pos and last < gate.pos
end

local function pushCrossing(gate, carIndex, t)
  gate.index2 = gate.index
  gate.lastCrossedTime2 = gate.lastCrossedTime
  gate.index = carIndex
  gate.lastCrossedTime = t
end

local function updateNearbyDrivers(playerCar)
  local playerIndex = playerCar.index
  local now = sim.time

  local gate = gateAt(playerCar.splinePosition)
  if crossed(playerCar.splinePosition, splineLast[playerIndex], gate) then
    if gate.index ~= nil and gate.index ~= playerIndex and gate.lastCrossedTime ~= nil then
      setNearby(1, gate.index, (gate.lastCrossedTime - now) / 1000)
    end
    pushCrossing(gate, playerIndex, now)
  end
  splineLast[playerIndex] = playerCar.splinePosition

  for _, other in ac.iterateCars() do
    if other.index ~= playerIndex then
      local sp = other.splinePosition
      local g = gateAt(sp)
      if crossed(sp, splineLast[other.index], g) then
        if g.index == playerIndex and g.lastCrossedTime ~= nil then
          setNearby(2, other.index, (now - g.lastCrossedTime) / 1000)
        elseif g.index2 == playerIndex and g.lastCrossedTime2 ~= nil then
          setNearby(3, other.index, (now - g.lastCrossedTime2) / 1000)
        end
        pushCrossing(g, other.index, now)
      end
      splineLast[other.index] = sp
    end
  end
end

-- Three-letter driver tag packed into an int32 (the cockpit shows upper(name):sub(1, 3)).
local function packName(driverIndex)
  if driverIndex == nil then return 0 end
  local name = ac.getDriverName(driverIndex)
  if not name or name == '' then return 0 end
  name = string.upper(name)
  local b1 = string.byte(name, 1) or 32
  local b2 = string.byte(name, 2) or 32
  local b3 = string.byte(name, 3) or 32
  return b1 + b2 * 256 + b3 * 65536
end

-- 2 Hz bookkeeping, matching the cockpit's slow refresh.
local slowAccumulator = 0
local perfDeltaLastLapSaved = 0
local perfDelta = 0

local function tick(dt)
  local car = ac.getCar(0)
  local cphys = ac.getCarPhysics(0)
  if not car or not cphys then return end
  if not inputs then tryConnect() end

  counter = counter + 1
  mmf.counter = counter
  mmf.connected = inputs and 1 or 0

  -- ERS / hybrid
  mmf.strat = round((car.mgukDelivery or 0) + 1)             -- cockpit STRAT
  mmf.puMode = round(rd(cphys, 'puMode'))                    -- 1..14, see plugin PuModeName
  mmf.deploymentStrat = round(rd(cphys, 'deploymentStrat'))
  mmf.deploymentSplit = round(rd(cphys, 'deploymentSplit'))
  mmf.kersInput = rd(cphys, 'kersInput')
  mmf.kersCharge = car.kersCharge or 0
  mmf.kersRegen = rd(cphys, 'kersRegen')
  mmf.kersDeployMJ = rd(cphys, 'kersDeployMJ')
  mmf.kersRegenMJ = rd(cphys, 'kersRegenMJ')
  mmf.kersRegenLimitMJ = rd(cphys, 'kersRegenLimitMJ')
  mmf.kersChargeDelta = rd(cphys, 'kersChargeDelta')
  mmf.kersChargeLast = rd(cphys, 'kersChargeLast')
  mmf.kersChargeESOC = rd(cphys, 'kersChargeESOC')
  mmf.rearMotorPowerKW = rd(cphys, 'rearMotorPowerKW')
  mmf.rearMotorTorque = rd(cphys, 'rearMotorTorque')
  mmf.mgukMaxPower = rd(cphys, 'mgukMaxPower')
  mmf.mgukMaxPowerLimit = rd(cphys, 'mgukMaxPowerLimit')
  mmf.mgukMaxPowerReduction = rd(cphys, 'mgukMaxPowerReduction')
  mmf.mgukMaxPowerReset = round(rd(cphys, 'mgukMaxPowerReset'))
  mmf.mgukMaxPowerResetSpeedThreshold = rd(cphys, 'mgukMaxPowerResetSpeedThreshold')
  mmf.isOvertakeActive = round(rd(cphys, 'isOvertakeActive'))
  mmf.isOvertakeActivePending = round(rd(cphys, 'isOvertakeActivePending'))
  mmf.isHybridBoostActive = round(rd(cphys, 'isHybridBoostActive'))
  mmf.isHybridAntiActive = round(rd(cphys, 'isHybridAntiActive'))
  mmf.isPowerLimited = round(rd(cphys, 'isPowerLimited'))
  mmf.isPowerLimitedPending = round(rd(cphys, 'isPowerLimitedPending'))
  mmf.powerLimitedExitTorque = rd(cphys, 'powerLimitedExitTorque')
  mmf.puTemperature = rd(cphys, 'puTemperature')
  mmf.puTorque = rd(cphys, 'puTorque')

  -- straight mode (SLM): latch 0 = off, 1 = available, 2 = pre-latched, 3 = available late
  mmf.drsLatch = round(rd(cphys, 'drsLatch'))
  mmf.drsMode = round(rd(cphys, 'drsMode'))
  mmf.drsAvailable = round(rd(cphys, 'drsAvailable'))
  mmf.drsEnabledLap = round(rd(cphys, 'drsEnabledLap'))

  -- brakes (raw 0..1; SimHub multiplies x100 for %)
  mmf.frontBias = rd(cphys, 'frontBias')                     -- = cockpit BB (peak)
  mmf.brakeBiasLive = rd(cphys, 'brakeBiasLive')
  mmf.brakeMigration = rd(cphys, 'brakeMigration')
  mmf.brakeBiasTargetDelta = rd(cphys, 'brakeBiasTargetDelta')
  mmf.brakeShapeMap = round(rd(cphys, 'brakeShapeMap'))
  mmf.brakeBalanceMap = round(rd(cphys, 'brakeBalanceMap'))
  mmf.brakePressure = rd(cphys, 'brakePressure')
  mmf.ebb = rd(cphys, 'ebb')

  -- differential (cockpit shows setting + 1)
  mmf.diffEntry = round(rd(cphys, 'differentialEntrySetting') + 1)
  mmf.diffMid = round(rd(cphys, 'differentialMidSetting') + 1)
  mmf.diffExit = round(rd(cphys, 'differentialExitHispdSetting') + 1)
  mmf.diffPower = round(rd(cphys, 'differentialPower'))
  mmf.diffCoast = round(rd(cphys, 'differentialCoast'))

  -- engine maps / fuel (fuel channels are litres; x KG_PER_LITER x1000 = grams, as in-game)
  mmf.fuelMap = round(rd(cphys, 'fuelMap'))
  mmf.pedalMap = round(rd(cphys, 'pedalMap'))
  mmf.torqueMap = round(rd(cphys, 'torqueMap'))
  mmf.engineBrake = round(rd(cphys, 'engineBrakeSetting') + 1)
  mmf.fuelSavedLastLap = rd(cphys, 'fuelSavedLastLap')
  mmf.fuelDelta = rd(cphys, 'fuelDelta')
  mmf.fuelLevel = car.fuel or 0
  mmf.fuelKgPerLiter = fuelKgPerLiter
  mmf.engineLifeLeft = car.engineLifeLeft or 1

  -- torque / pedals (throttle raw 0..1; SimHub x100)
  mmf.torqueDemand = rd(cphys, 'torqueDemand')
  mmf.torqueDriverDemand = rd(cphys, 'torqueDriverDemand')
  mmf.torqueOut = rd(cphys, 'torqueOut')
  mmf.throttleRaw = rd(cphys, 'throttleRaw')
  mmf.throttlePedal = rd(cphys, 'throttlePedal')

  -- driver definitions / targets
  local targetLapTimeMs = round(rd(cphys, 'driverTargetLapTime') * 10)
  local lapDeltaMode = round(rd(cphys, 'lapDelta'))
  mmf.targetLapTimeMs = targetLapTimeMs
  mmf.targetStintLaps = round(rd(cphys, 'driverTargetStintLaps'))
  mmf.lapDeltaMode = lapDeltaMode
  mmf.lapTimeDelta = round(rd(cphys, 'lapTimeDelta'))

  -- car state (0/1)
  mmf.isEngineRunning = round(rd(cphys, 'isEngineRunning'))
  mmf.isAntistallActive = round(rd(cphys, 'isAntistallActive'))
  mmf.isIgnitionStage1 = round(rd(cphys, 'isIgnitionStage1'))
  mmf.isIgnitionStage2 = round(rd(cphys, 'isIgnitionStage2'))
  mmf.isSteeringWheelAttached = round(rd(cphys, 'isSteeringWheelAttached'))
  mmf.isSteeringWheelBooted = round(rd(cphys, 'isSteeringWheelBooted'))
  mmf.isStarterCranking = round(rd(cphys, 'isStarterCranking'))
  mmf.isPitLimiterActive = round(rd(cphys, 'isPitLimiterActive'))
  mmf.isConstantSpeedLimiterActive = round(rd(cphys, 'isConstantSpeedLimiterActive'))
  mmf.isBrakeMagicActive = round(rd(cphys, 'isBrakeMagicActive'))
  mmf.isFirstGearShift = round(rd(cphys, 'isFirstGearShift'))
  mmf.pitSpeedLimit = rd(cphys, 'pitSpeedLimit')
  mmf.launchLoadPerc = rd(cphys, 'launchLoadPerc')
  mmf.driverPreset1Active = round(rd(cphys, 'driverPreset1Active'))
  mmf.driverPreset2Active = round(rd(cphys, 'driverPreset2Active'))
  mmf.driverPresetActiveIndex = round(rd(cphys, 'driverPresetActiveIndex'))

  -- plank / legality wear (legality channels x1000 = mm, per the car's own telemetry defs)
  mmf.plankWear = rd(cphys, 'plankWear')
  mmf.frontLegalityWear = rd(cphys, 'frontLegalityWear')
  mmf.midLegalityWear = rd(cphys, 'midLegalityWear')
  mmf.rearLegalityWear = rd(cphys, 'rearLegalityWear')

  -- aero / tyre configuration
  mmf.aeroRearWingGurney = round(rd(cphys, 'aeroRearWingGurney'))
  mmf.aeroLouvers = round(rd(cphys, 'aeroLouvers'))
  mmf.aeroFrontWingDamage = round(rd(cphys, 'aeroFrontWingDamage'))
  mmf.tyreCompoundRange = round(rd(cphys, 'tyreCompoundRange'))
  mmf.compoundIndex = car.compoundIndex or 0

  -- tyres: the car publishes the delta to optimum directly (same number the cockpit prints
  -- as %+03d). Brake disc temps come from stock AC wheel data.
  local wheels = car.wheels
  local function discTemp(i)
    if not wheels or not wheels[i] then return 0 end
    return round(wheels[i].discTemperature or 0)
  end
  local function tyreDelta(name)
    local v = rd(cphys, name)
    v = v >= 0 and floor(v) or -floor(-v)  -- truncate toward zero, like the cockpit
    return clamp(v, -99, 99)
  end
  mmf.tyreTempDeltaFL = tyreDelta('tyrePracticalTempFL')
  mmf.tyreTempDeltaFR = tyreDelta('tyrePracticalTempFR')
  mmf.tyreTempDeltaRL = tyreDelta('tyrePracticalTempRL')
  mmf.tyreTempDeltaRR = tyreDelta('tyrePracticalTempRR')
  mmf.brakeDiscTempFL = discTemp(0)
  mmf.brakeDiscTempFR = discTemp(1)
  mmf.brakeDiscTempRL = discTemp(2)
  mmf.brakeDiscTempRR = discTemp(3)

  -- display configuration (popup durations are x0.1 seconds, as in Popup:setActive)
  mmf.displayOption1 = round(rd(cphys, 'displayOption1'))
  mmf.displayOption2 = round(rd(cphys, 'displayOption2'))
  mmf.displayOption3 = round(rd(cphys, 'displayOption3'))
  mmf.displayPopupTime = round(rd(cphys, 'displayPopupTime'))
  mmf.displayLapPopupTime = round(rd(cphys, 'displayLapPopupTime'))
  mmf.displayBacklightBrightness = round(rd(cphys, 'displayBacklightBrightness'))
  mmf.ledShiftPattern = round(rd(cphys, 'ledShiftPattern'))
  mmf.ledBrightness = round(rd(cphys, 'ledBrightness'))
  mmf.lightState = round(rd(cphys, 'lightState'))

  -- timing block, replicating data.lua
  local lapCount = (car.lapCount or 0) + 1
  local sessionLaps = 0
  local ok, session = pcall(ac.getSession, sim.currentSessionIndex)
  if ok and session and session.laps then sessionLaps = session.laps end

  mmf.lapCount = lapCount
  mmf.sessionLaps = sessionLaps
  mmf.lapsRemaining = sessionLaps > 0 and (sessionLaps - (car.lapCount or 0)) or 0
  mmf.previousLapTimeMs = car.previousLapTimeMs or 0
  mmf.bestLapTimeMs = car.bestLapTimeMs or 0

  local estimatedMs = (car.bestLapTimeMs or 0) + (car.performanceMeter or 0) * 1000
  local comparisonMs = car.previousLapTimeMs or 0
  if lapDeltaMode == 2 then
    comparisonMs = targetLapTimeMs
  elseif lapDeltaMode == 1 then
    comparisonMs = car.bestLapTimeMs or 0
  end

  perfDelta = clamp((estimatedMs - comparisonMs) * 0.001, -99.99, 99.99)
  mmf.estimatedLapTimeMs = round(estimatedMs)
  mmf.referenceLapTimeMs = round(comparisonMs)
  mmf.perfDelta = perfDelta

  -- the cockpit freezes the delta shown in the lap popup just before the line (2 Hz check)
  slowAccumulator = slowAccumulator + (dt or 0)
  if slowAccumulator >= 0.5 then
    slowAccumulator = slowAccumulator % 0.5
    if car.splinePosition > 0.99 then perfDeltaLastLapSaved = perfDelta end
  end
  mmf.perfDeltaLastLapSaved = perfDeltaLastLapSaved

  local aheadIndex = findCarAheadIndex(car)
  if sim.isSessionStarted and car.racePosition > 1 and aheadIndex then
    mmf.gapToCarAhead = clamp(ac.getGapBetweenCars(car.index, aheadIndex), 0, 99.99)
  else
    mmf.gapToCarAhead = 0
  end

  mmf.headwindKmh = headwindSpeed(car)

  -- race-flag caution: the source the in-game wheel uses for the side yellow LED.
  -- Not in AC shared memory, so SimHub can't see it natively -> publish it here.
  mmf.isCaution = (ac.FlagType and sim.raceFlagType == ac.FlagType.Caution) and 1 or 0

  -- nearby drivers panel (displayOption2)
  updateNearbyDrivers(car)
  mmf.nearby1Name = packName(nearbyIndex[1])
  mmf.nearby1Delta = nearbyDelta[1] or 0
  mmf.nearby2Name = packName(nearbyIndex[2])
  mmf.nearby2Delta = nearbyDelta[2] or 0
  mmf.nearby3Name = packName(nearbyIndex[3])
  mmf.nearby3Delta = nearbyDelta[3] or 0
end

-- Run every frame whether or not the window is open: script.update covers the headless
-- case; windowMain covers builds that only tick while a window is shown. (Double-ticking a
-- frame is harmless - values are identical, counter is only a liveness signal.)
function script.update(dt)
  tick(dt)
end

function script.windowMain(dt)
  tick(dt)
  ui.text('MMF: ' .. MMF_NAME)
  ui.text(string.format('counter:   %d', counter))
  ui.text(string.format('connected: %s', tostring(mmf.connected == 1)))
  ui.separator()
  ui.text(string.format('STRAT %d   MODE %d', mmf.strat, mmf.puMode))
  ui.text(string.format('BB %.1f%%  live %.1f%%', mmf.frontBias * 100, mmf.brakeBiasLive * 100))
  ui.text(string.format('Diff E/M/X  %d / %d / %d', mmf.diffEntry, mmf.diffMid, mmf.diffExit))
  ui.text(string.format('EB %d   SLM latch %d', mmf.engineBrake, mmf.drsLatch))
  ui.text(string.format('MGU-K %.0f kW  (max %.0f)', mmf.rearMotorPowerKW, mmf.mgukMaxPower))
  ui.text(string.format('SoC %.1f%%   PU temp %.0f', mmf.kersCharge * 100, mmf.puTemperature))
  ui.text(string.format('Tyres %+d %+d %+d %+d',
    mmf.tyreTempDeltaFL, mmf.tyreTempDeltaFR, mmf.tyreTempDeltaRL, mmf.tyreTempDeltaRR))
  ui.separator()
  ui.text('Leave this open (or minimized) while driving.')
end
