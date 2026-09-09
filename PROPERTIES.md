# VRC FA26 Telemetry — SimHub property reference

All properties appear in the SimHub property picker under **VRC FA26 Telemetry**.
Raw values are sent unformatted; `*Pct`, `*Text` and `*Color` are convenience variants.

- `Connected`
- `Counter`

## ERS / hybrid

- `Strat` — cockpit STRAT
- `PuMode` — 1..14
- `PuModeName` — RACE / AD1 / Q / ...
- `DeploymentStrat`
- `DeploymentSplit`
- `KersInput`
- `KersCharge` — SoC 0..1
- `KersChargePct`
- `KersRegen`
- `KersRegenPct`
- `KersDeployMJ`
- `KersRegenMJ`
- `KersRegenLimitMJ`
- `KersRegenRemainingMJ`
- `SocDeltaPct` — lap popup "SOC Delta"
- `SocDeltaText`
- `SocDeltaColor`
- `ChargeEnergyLastPct`
- `KersChargeESOC`
- `MgukPowerKW`
- `MgukTorque`
- `MgukPowerBar`
- `MgukMaxPower`
- `MgukMaxPowerLimit`
- `MgukMaxPowerReduction`
- `MgukMaxPowerReset`
- `MgukMaxPowerResetSpeedThreshold`
- `IsOvertakeActive`
- `IsOvertakeActivePending`
- `IsBoostActive`
- `IsChargeActive` — wheel charge LEDs
- `OvertakeText`
- `BoostText`
- `IsPowerLimited` — PL
- `IsPowerLimitedPending` — PLP
- `PowerLimitedExitTorque`
- `PuTemperature`
- `PuTemperatureRounded`
- `PuTorque`

## Straight Mode (SLM)

- `SlmLatch` — 0..3
- `SlmLatchName`
- `SlmLatchColor` — wheel LED colour
- `SlmActive`
- `SlmAvailable`
- `SlmEnabledLap`

## Brakes (raw 0..1 + convenience %)

- `BrakeBias`
- `BrakeBiasPct` — cockpit BB (peak)
- `BrakeBiasText`
- `BrakeBiasLive`
- `BrakeBiasLivePct`
- `BrakeMigration`
- `BrakeMigrationPct`
- `BrakeBiasTargetDelta`
- `BrakeShapeMap`
- `BrakeBalanceMap`
- `BrakePressure`
- `Ebb`

## Differential

- `DiffEntry`
- `DiffMid`
- `DiffExit`
- `DiffPower`
- `DiffCoast`

## Engine maps / fuel

- `FuelMap`
- `PedalMap`
- `TorqueMap`
- `EngineBrake`
- `FuelLevelKg`
- `FuelLevelText`
- `FuelSavedLastLapG`
- `FuelSavedLastLapText`
- `FuelSavedLastLapColor`
- `FuelDeltaG`
- `FuelDeltaText`
- `FuelDeltaColor`
- `EngineLifeLeft`

## Torque / pedals

- `TorqueDemand`
- `TorqueDriverDemand`
- `TorqueOut`
- `ThrottleRawPct`
- `ThrottlePedalPct`

## Driver definitions / targets

- `TargetLapTimeMs`
- `TargetLapTime`
- `TargetStintLaps`
- `LapDeltaMode` — 0 prev, 1 best, 2 target
- `LapDeltaModeName`
- `LapTimeDelta`

## Car state

- `IsEngineRunning`
- `IsAntistallActive`
- `IsIgnitionStage1`
- `IsIgnitionStage2`
- `IsSteeringWheelAttached`
- `IsSteeringWheelBooted`
- `IsStarterCranking`
- `IsPitLimiterActive`
- `IsConstantSpeedLimiterActive`
- `IsBrakeMagicActive`
- `IsFirstGearShift`
- `PitSpeedLimit`
- `LaunchLoadPct`
- `DriverPreset1Active`
- `DriverPreset2Active`
- `DriverPresetActiveIndex`
- `StarterStatus`

## Plank / legality wear

- `PlankWear`
- `FrontLegalityWearMm`
- `MidLegalityWearMm`
- `RearLegalityWearMm`
- `WorstLegalityWearMm`
- `IsPlankIllegal`

## Aero / tyre configuration

- `AeroRearWingGurney`
- `AeroLouvers`
- `AeroFrontWingDamage`
- `TyreCompoundRange`
- `CompoundIndex`

## Tyre / brake temps (delta to optimum, exactly as the cockpit prints it)

- `TyreTempDeltaFL`
- `TyreTempDeltaFR`
- `TyreTempDeltaRL`
- `TyreTempDeltaRR`
- `TyreTempDeltaTextFL`
- `TyreTempDeltaTextFR`
- `TyreTempDeltaTextRL`
- `TyreTempDeltaTextRR`
- `TyreColorFL`
- `TyreColorFR`
- `TyreColorRL`
- `TyreColorRR`
- `BrakeDiscTempFL`
- `BrakeDiscTempFR`
- `BrakeDiscTempRL`
- `BrakeDiscTempRR`
- `BrakeColorFL`
- `BrakeColorFR`
- `BrakeColorRL`
- `BrakeColorRR`

## Display configuration

- `DisplayOption1` — 1 = brake temps page
- `DisplayOption2` — 1 = nearby drivers
- `DisplayOption3` — 1 = fuel row
- `DisplayBacklightBrightness`
- `LedShiftPattern`
- `LedBrightness`
- `LightState` — 0 off, 1 RISC, 2 rain, 3 safety

## Timing block

- `LapNumber`
- `SessionLaps`
- `LapsRemaining`
- `LapsRemainingText`
- `PreviousLapTime`
- `PreviousLapTimeMs`
- `PreviousLapTimeColor`
- `BestLapTime`
- `BestLapTimeMs`
- `EstimatedLapTime`
- `EstimatedLapTimeMs`
- `ReferenceLapTime`
- `ReferenceLapTimeMs`
- `PerfDelta`
- `PerfDeltaText`
- `PerfDeltaColor`
- `PerfDeltaLastLapText`
- `GapToCarAhead`
- `HeadwindKmh`
- `IsCaution`

## Nearby drivers panel

- `Nearby1Name`
- `Nearby1Delta`
- `Nearby1DeltaText`
- `Nearby2Name`
- `Nearby2Delta`
- `Nearby2DeltaText`
- `Nearby3Name`
- `Nearby3Delta`
- `Nearby3DeltaText`

## Popups (setting splashes + lap popup + banners)

- `SplashKind` — see SplashKind()
- `SplashId`
- `SplashLabel`
- `SplashValue`
- `SplashColor`
- `SplashTextColor`
- `SplashValueFontSize`
- `BannerKind`
- `BannerLabel`
- `BannerValue`
- `BannerColor`
- `BannerTextColor`
- `BannerValueFontSize`
- `LapPopupActive`
- `LapPopupPrevLapTime`
- `LapPopupBestLapTime`
- `LapPopupDelta`
- `LapPopupDeltaColor`

_190 properties._
