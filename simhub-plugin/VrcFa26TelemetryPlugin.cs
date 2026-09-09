using System;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using GameReaderCommon;
using SimHub.Plugins;

namespace VrcFa26Telemetry
{
    // Reads the shared memory-mapped file published by the in-game CSP exporter app
    // (apps/lua/vrc_fa26_telemetry) and exposes the VRC FA26 private channels as SimHub
    // properties. Standard data (speed/rpm/gear/fuel/tyres) comes from SimHub itself.
    // Struct layout MUST match vrc_fa26_telemetry.lua.
    [PluginDescription("VRC Formula Alpha 2026 private CAN/ECU telemetry via CSP shared memory.")]
    [PluginAuthor("VRC tooling")]
    [PluginName("VRC FA26 Telemetry")]
    public class VrcFa26TelemetryPlugin : IPlugin, IDataPlugin
    {
        private const string MmfName = "Local\\AcTools.CSP.VRC_FA26.v1";

        public PluginManager PluginManager { get; set; }

        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _acc;
        private VrcData _d;
        private int _retry;

        // Monotonic clock for popup timing (Environment.TickCount wraps).
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _lastPreset1 = -1, _lastPreset2 = -1;
        private bool _presetInitialized;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- in-game colours (display/styles/style_0/config.lua), as #AARRGGBB ----
        private const string ColWhite = "#FFFFFFFF";
        private const string ColBlack = "#FF000000";
        private const string ColRed = "#FFFF0000";
        private const string ColDarkRed = "#FF801A1A";
        private const string ColOrange = "#FFFF4000";
        private const string ColRacingBlue = "#FF004DFF";
        private const string ColCoolBlue = "#FF001AFF";
        private const string ColMediumGray = "#FFB3B3B3";
        private const string ColRbrGreen = "#FF008033";
        private const string ColChargingGreen = "#FF00FF40";
        private const string ColPurple = "#FF8066FF";
        private const string ColWarningYellow = "#FFCCB31A";
        private const string ColLightBlue = "#FF338CFF";
        private const string ColTransparent = "#00000000";

        // PU mode labels, in cockpit order (display/styles/style_0/pages.lua).
        private static readonly string[] PuModeNames =
        {
            "RACE", "AD1", "AD2", "FS", "FW", "IN", "ES", "Q", "K2", "K2+", "SLO", "SC", "T4", "RS"
        };

        // Straight Mode latch, as shown by the three leftmost wheel LEDs.
        private static readonly string[] SlmLatchNames = { "OFF", "AVAILABLE", "PRE-LATCHED", "AVAILABLE LATE" };

        // ---- popup state ----
        private readonly Popup[] _splashers;
        private readonly Popup[] _banners;
        private readonly PopupController _splashController;
        private readonly PopupController _bannerController;

        private Popup _splash;
        private Popup _banner;

        public VrcFa26TelemetryPlugin()
        {
            // Order matters: the cockpit shows the first triggered popup in this list
            // (display/styles/style_0/popups.lua -> popups.splashers).
            _splashers = new[]
            {
                new Popup("off", 0, () => 0, () => _d.isSteeringWheelAttached == 0 || _d.isIgnitionStage1 == 0,
                          () => "", () => "", ColBlack, ColWhite, 100.0),
                new Popup("splash", 0, () => 0,
                          () => _d.isSteeringWheelAttached != 0 && _d.isIgnitionStage1 != 0 && _d.isSteeringWheelBooted == 0,
                          () => "", () => "", ColBlack, ColWhite, 100.0),
                new Popup("lap", 2, () => _d.lapCount, () => false, () => "LAP", () => _d.lapCount.ToString(Inv),
                          ColBlack, ColWhite, 100.0),
                new Popup("mgukDelivery", 1, () => _d.strat, () => false, () => "STRAT",
                          () => _d.strat.ToString(Inv), ColRbrGreen, ColWhite, 100.0),
                new Popup("puMode", 1, () => _d.puMode, () => false, () => "MODE",
                          () => PuModeName(_d.puMode), ColMediumGray, ColBlack, 100.0),
                new Popup("eb", 1, () => _d.engineBrake, () => false, () => "EB",
                          () => _d.engineBrake.ToString(Inv), ColOrange, ColWhite, 100.0),
                new Popup("diffEntry", 1, () => _d.diffEntry, () => false, () => "ENTRY",
                          () => _d.diffEntry.ToString(Inv), ColMediumGray, ColBlack, 100.0),
                new Popup("diffMid", 1, () => _d.diffMid, () => false, () => "MID",
                          () => _d.diffMid.ToString(Inv), ColMediumGray, ColBlack, 100.0),
                new Popup("diffExit", 1, () => _d.diffExit, () => false, () => "EXIT",
                          () => _d.diffExit.ToString(Inv), ColMediumGray, ColBlack, 100.0),
                new Popup("brakeBiasPeak", 1, () => BrakeBiasKey(), () => false, () => "BBal",
                          () => BrakeBiasText(), ColWhite, ColBlack, 100.0),
                new Popup("brakeShapeMap", 1, () => _d.brakeShapeMap, () => false, () => "BMIG",
                          () => _d.brakeShapeMap.ToString(Inv), ColRacingBlue, ColWhite, 100.0),
                new Popup("driverTargetStintLaps", 1, () => _d.targetStintLaps, () => false, () => "LAPS",
                          () => _d.targetStintLaps.ToString(Inv), ColMediumGray, ColBlack, 100.0),
                new Popup("driverTargetLapTime", 1, () => _d.targetLapTimeMs, () => false, () => "TAR LAP",
                          () => LapTime(_d.targetLapTimeMs), ColMediumGray, ColBlack, 100.0),
            };

            _banners = new[]
            {
                new Popup("engineLifeLeft", 0, () => 0, () => _d.engineLifeLeft < 0.1f, () => "FAIL",
                          () => "00x14", ColDarkRed, ColWhite, 100.0),
                new Popup("noFuel", 0, () => 0, () => _d.fuelLevel < 0.1f, () => "NO FUEL",
                          () => "", ColDarkRed, ColWhite, 100.0),
                new Popup("isAntistallActive", 0, () => 0, () => _d.isAntistallActive != 0, () => "ANTISTALL",
                          () => "CLUTCH IN", ColDarkRed, ColWhite, 50.0),
                new Popup("puTemperature", 0, () => 0, () => _d.puTemperature > 110f, () => "",
                          () => "TOO HOT", ColDarkRed, ColWhite, 160.0),
            };

            _splashController = new PopupController(_splashers);
            _bannerController = new PopupController(_banners);
        }

        public void Init(PluginManager pluginManager)
        {
            TryOpen();

            // diagnostics
            this.AttachDelegate("Connected", () => _d.connected);
            this.AttachDelegate("Counter", () => _d.counter);

            // ---- ERS / hybrid ----
            this.AttachDelegate("Strat", () => _d.strat);                              // cockpit STRAT
            this.AttachDelegate("PuMode", () => _d.puMode);                            // 1..14
            this.AttachDelegate("PuModeName", () => PuModeName(_d.puMode));            // RACE / AD1 / Q / ...
            this.AttachDelegate("DeploymentStrat", () => _d.deploymentStrat);
            this.AttachDelegate("DeploymentSplit", () => _d.deploymentSplit);
            this.AttachDelegate("KersInput", () => _d.kersInput);
            this.AttachDelegate("KersCharge", () => _d.kersCharge);                    // SoC 0..1
            this.AttachDelegate("KersChargePct", () => _d.kersCharge * 100f);
            this.AttachDelegate("KersRegen", () => _d.kersRegen);
            this.AttachDelegate("KersRegenPct", () => _d.kersRegen * 100f);
            this.AttachDelegate("KersDeployMJ", () => Guard(_d.kersDeployMJ));
            this.AttachDelegate("KersRegenMJ", () => Guard(_d.kersRegenMJ));
            this.AttachDelegate("KersRegenLimitMJ", () => Guard(_d.kersRegenLimitMJ));
            this.AttachDelegate("KersRegenRemainingMJ", () => Guard(_d.kersRegenLimitMJ - _d.kersRegenMJ));
            this.AttachDelegate("SocDeltaPct", () => Guard(_d.kersChargeDelta) * 100f); // lap popup "SOC Delta"
            this.AttachDelegate("SocDeltaText", () => Signed(Guard(_d.kersChargeDelta) * 100f, 1));
            this.AttachDelegate("SocDeltaColor", () => _d.kersChargeDelta < 0 ? ColRed : ColChargingGreen);
            this.AttachDelegate("ChargeEnergyLastPct", () => Guard(_d.kersChargeLast) * 100f);
            this.AttachDelegate("KersChargeESOC", () => Guard(_d.kersChargeESOC));
            this.AttachDelegate("MgukPowerKW", () => Guard(_d.rearMotorPowerKW));
            this.AttachDelegate("MgukTorque", () => Guard(_d.rearMotorTorque));
            this.AttachDelegate("MgukPowerBar", () => Clamp(Guard(_d.rearMotorPowerKW) / 350f, -1f, 1f));
            this.AttachDelegate("MgukMaxPower", () => Guard(_d.mgukMaxPower));
            this.AttachDelegate("MgukMaxPowerLimit", () => Guard(_d.mgukMaxPowerLimit));
            this.AttachDelegate("MgukMaxPowerReduction", () => Guard(_d.mgukMaxPowerReduction));
            this.AttachDelegate("MgukMaxPowerReset", () => _d.mgukMaxPowerReset);
            this.AttachDelegate("MgukMaxPowerResetSpeedThreshold", () => Guard(_d.mgukMaxPowerResetSpeedThreshold));
            this.AttachDelegate("IsOvertakeActive", () => _d.isOvertakeActive);
            this.AttachDelegate("IsOvertakeActivePending", () => _d.isOvertakeActivePending);
            this.AttachDelegate("IsBoostActive", () => _d.isHybridBoostActive);
            this.AttachDelegate("IsChargeActive", () => _d.isHybridAntiActive);        // wheel charge LEDs
            this.AttachDelegate("OvertakeText", () => _d.isOvertakeActive != 0 ? "OT" : "");
            this.AttachDelegate("BoostText", () => _d.isHybridBoostActive != 0 ? "BO" : "");
            this.AttachDelegate("IsPowerLimited", () => _d.isPowerLimited);            // PL
            this.AttachDelegate("IsPowerLimitedPending", () => _d.isPowerLimitedPending); // PLP
            this.AttachDelegate("PowerLimitedExitTorque", () => Guard(_d.powerLimitedExitTorque));
            this.AttachDelegate("PuTemperature", () => Guard(_d.puTemperature));
            this.AttachDelegate("PuTemperatureRounded", () => (int)Math.Round(Guard(_d.puTemperature)));
            this.AttachDelegate("PuTorque", () => Guard(_d.puTorque));

            // ---- Straight Mode (SLM) ----
            this.AttachDelegate("SlmLatch", () => _d.drsLatch);                        // 0..3
            this.AttachDelegate("SlmLatchName", () => SlmLatchName(_d.drsLatch));
            this.AttachDelegate("SlmLatchColor", () => SlmLatchColor(_d.drsLatch));    // wheel LED colour
            this.AttachDelegate("SlmActive", () => _d.drsMode);
            this.AttachDelegate("SlmAvailable", () => _d.drsAvailable);
            this.AttachDelegate("SlmEnabledLap", () => _d.drsEnabledLap);

            // ---- brakes (raw 0..1 + convenience %) ----
            this.AttachDelegate("BrakeBias", () => _d.frontBias);
            this.AttachDelegate("BrakeBiasPct", () => _d.frontBias * 100f);            // cockpit BB (peak)
            this.AttachDelegate("BrakeBiasText", () => BrakeBiasText());
            this.AttachDelegate("BrakeBiasLive", () => _d.brakeBiasLive);
            this.AttachDelegate("BrakeBiasLivePct", () => _d.brakeBiasLive * 100f);
            this.AttachDelegate("BrakeMigration", () => _d.brakeMigration);
            this.AttachDelegate("BrakeMigrationPct", () => _d.brakeMigration * 100f);
            this.AttachDelegate("BrakeBiasTargetDelta", () => _d.brakeBiasTargetDelta);
            this.AttachDelegate("BrakeShapeMap", () => _d.brakeShapeMap);
            this.AttachDelegate("BrakeBalanceMap", () => _d.brakeBalanceMap);
            this.AttachDelegate("BrakePressure", () => _d.brakePressure);
            this.AttachDelegate("Ebb", () => _d.ebb);

            // ---- differential ----
            this.AttachDelegate("DiffEntry", () => _d.diffEntry);
            this.AttachDelegate("DiffMid", () => _d.diffMid);
            this.AttachDelegate("DiffExit", () => _d.diffExit);
            this.AttachDelegate("DiffPower", () => _d.diffPower);
            this.AttachDelegate("DiffCoast", () => _d.diffCoast);

            // ---- engine maps / fuel ----
            this.AttachDelegate("FuelMap", () => _d.fuelMap);
            this.AttachDelegate("PedalMap", () => _d.pedalMap);
            this.AttachDelegate("TorqueMap", () => _d.torqueMap);
            this.AttachDelegate("EngineBrake", () => _d.engineBrake);
            this.AttachDelegate("FuelLevelKg", () => Guard(_d.fuelLevel * _d.fuelKgPerLiter));
            this.AttachDelegate("FuelLevelText", () => Guard(_d.fuelLevel * _d.fuelKgPerLiter).ToString("0.0", Inv));
            this.AttachDelegate("FuelSavedLastLapG", () => FuelGrams(_d.fuelSavedLastLap));
            this.AttachDelegate("FuelSavedLastLapText", () => Signed(FuelGrams(_d.fuelSavedLastLap), 0));
            this.AttachDelegate("FuelSavedLastLapColor",
                () => FuelGrams(_d.fuelSavedLastLap) < 0 ? ColRed : ColChargingGreen);
            this.AttachDelegate("FuelDeltaG", () => FuelGrams(_d.fuelDelta));
            this.AttachDelegate("FuelDeltaText", () => Signed(FuelGrams(_d.fuelDelta), 0));
            this.AttachDelegate("FuelDeltaColor", () => FuelGrams(_d.fuelDelta) < 0 ? ColRed : ColChargingGreen);
            this.AttachDelegate("EngineLifeLeft", () => Guard(_d.engineLifeLeft));

            // ---- torque / pedals ----
            this.AttachDelegate("TorqueDemand", () => Guard(_d.torqueDemand));
            this.AttachDelegate("TorqueDriverDemand", () => Guard(_d.torqueDriverDemand));
            this.AttachDelegate("TorqueOut", () => Guard(_d.torqueOut));
            this.AttachDelegate("ThrottleRawPct", () => _d.throttleRaw * 100f);
            this.AttachDelegate("ThrottlePedalPct", () => _d.throttlePedal * 100f);

            // ---- driver definitions / targets ----
            this.AttachDelegate("TargetLapTimeMs", () => _d.targetLapTimeMs);
            this.AttachDelegate("TargetLapTime", () => LapTime(_d.targetLapTimeMs));
            this.AttachDelegate("TargetStintLaps", () => _d.targetStintLaps);
            this.AttachDelegate("LapDeltaMode", () => _d.lapDeltaMode);                // 0 prev, 1 best, 2 target
            this.AttachDelegate("LapDeltaModeName", () => LapDeltaModeName(_d.lapDeltaMode));
            this.AttachDelegate("LapTimeDelta", () => _d.lapTimeDelta);

            // ---- car state ----
            this.AttachDelegate("IsEngineRunning", () => _d.isEngineRunning);
            this.AttachDelegate("IsAntistallActive", () => _d.isAntistallActive);
            this.AttachDelegate("IsIgnitionStage1", () => _d.isIgnitionStage1);
            this.AttachDelegate("IsIgnitionStage2", () => _d.isIgnitionStage2);
            this.AttachDelegate("IsSteeringWheelAttached", () => _d.isSteeringWheelAttached);
            this.AttachDelegate("IsSteeringWheelBooted", () => _d.isSteeringWheelBooted);
            this.AttachDelegate("IsStarterCranking", () => _d.isStarterCranking);
            this.AttachDelegate("IsPitLimiterActive", () => _d.isPitLimiterActive);
            this.AttachDelegate("IsConstantSpeedLimiterActive", () => _d.isConstantSpeedLimiterActive);
            this.AttachDelegate("IsBrakeMagicActive", () => _d.isBrakeMagicActive);
            this.AttachDelegate("IsFirstGearShift", () => _d.isFirstGearShift);
            this.AttachDelegate("PitSpeedLimit", () => Guard(_d.pitSpeedLimit));
            this.AttachDelegate("LaunchLoadPct", () => Guard(_d.launchLoadPerc) * 100f);
            this.AttachDelegate("DriverPreset1Active", () => _d.driverPreset1Active);
            this.AttachDelegate("DriverPreset2Active", () => _d.driverPreset2Active);
            this.AttachDelegate("DriverPresetActiveIndex", () => _d.driverPresetActiveIndex);
            // engine status line under the RPM box, as printed by pages.lua
            this.AttachDelegate("StarterStatus", () => StarterStatus());

            // ---- plank / legality wear ----
            // The car's own telemetry defs use x1000 on the three legality channels for mm.
            this.AttachDelegate("PlankWear", () => Guard(_d.plankWear));
            this.AttachDelegate("FrontLegalityWearMm", () => Guard(_d.frontLegalityWear) * 1000f);
            this.AttachDelegate("MidLegalityWearMm", () => Guard(_d.midLegalityWear) * 1000f);
            this.AttachDelegate("RearLegalityWearMm", () => Guard(_d.rearLegalityWear) * 1000f);
            this.AttachDelegate("WorstLegalityWearMm", () => WorstLegalityWearMm());
            this.AttachDelegate("IsPlankIllegal", () => WorstLegalityWearMm() > 1f ? 1 : 0);

            // ---- aero / tyre configuration ----
            this.AttachDelegate("AeroRearWingGurney", () => _d.aeroRearWingGurney);
            this.AttachDelegate("AeroLouvers", () => _d.aeroLouvers);
            this.AttachDelegate("AeroFrontWingDamage", () => _d.aeroFrontWingDamage);
            this.AttachDelegate("TyreCompoundRange", () => _d.tyreCompoundRange);
            this.AttachDelegate("CompoundIndex", () => _d.compoundIndex);

            // ---- tyre / brake temps (delta to optimum, exactly as the cockpit prints it) ----
            this.AttachDelegate("TyreTempDeltaFL", () => _d.tyreTempDeltaFL);
            this.AttachDelegate("TyreTempDeltaFR", () => _d.tyreTempDeltaFR);
            this.AttachDelegate("TyreTempDeltaRL", () => _d.tyreTempDeltaRL);
            this.AttachDelegate("TyreTempDeltaRR", () => _d.tyreTempDeltaRR);
            this.AttachDelegate("TyreTempDeltaTextFL", () => TempDeltaText(_d.tyreTempDeltaFL));
            this.AttachDelegate("TyreTempDeltaTextFR", () => TempDeltaText(_d.tyreTempDeltaFR));
            this.AttachDelegate("TyreTempDeltaTextRL", () => TempDeltaText(_d.tyreTempDeltaRL));
            this.AttachDelegate("TyreTempDeltaTextRR", () => TempDeltaText(_d.tyreTempDeltaRR));
            this.AttachDelegate("TyreColorFL", () => TyreColor(_d.tyreTempDeltaFL));
            this.AttachDelegate("TyreColorFR", () => TyreColor(_d.tyreTempDeltaFR));
            this.AttachDelegate("TyreColorRL", () => TyreColor(_d.tyreTempDeltaRL));
            this.AttachDelegate("TyreColorRR", () => TyreColor(_d.tyreTempDeltaRR));
            this.AttachDelegate("BrakeDiscTempFL", () => _d.brakeDiscTempFL);
            this.AttachDelegate("BrakeDiscTempFR", () => _d.brakeDiscTempFR);
            this.AttachDelegate("BrakeDiscTempRL", () => _d.brakeDiscTempRL);
            this.AttachDelegate("BrakeDiscTempRR", () => _d.brakeDiscTempRR);
            this.AttachDelegate("BrakeColorFL", () => BrakeColor(_d.brakeDiscTempFL));
            this.AttachDelegate("BrakeColorFR", () => BrakeColor(_d.brakeDiscTempFR));
            this.AttachDelegate("BrakeColorRL", () => BrakeColor(_d.brakeDiscTempRL));
            this.AttachDelegate("BrakeColorRR", () => BrakeColor(_d.brakeDiscTempRR));

            // ---- display configuration ----
            this.AttachDelegate("DisplayOption1", () => _d.displayOption1);            // 1 = brake temps page
            this.AttachDelegate("DisplayOption2", () => _d.displayOption2);            // 1 = nearby drivers
            this.AttachDelegate("DisplayOption3", () => _d.displayOption3);            // 1 = fuel row
            this.AttachDelegate("DisplayBacklightBrightness", () => _d.displayBacklightBrightness);
            this.AttachDelegate("LedShiftPattern", () => _d.ledShiftPattern);
            this.AttachDelegate("LedBrightness", () => _d.ledBrightness);
            this.AttachDelegate("LightState", () => _d.lightState);                    // 0 off, 1 RISC, 2 rain, 3 safety

            // ---- timing block ----
            this.AttachDelegate("LapNumber", () => _d.lapCount);
            this.AttachDelegate("SessionLaps", () => _d.sessionLaps);
            this.AttachDelegate("LapsRemaining", () => _d.lapsRemaining);
            this.AttachDelegate("LapsRemainingText", () => "( " + _d.lapsRemaining.ToString(Inv) + " )");
            this.AttachDelegate("PreviousLapTime", () => LapTime(_d.previousLapTimeMs));
            this.AttachDelegate("PreviousLapTimeMs", () => _d.previousLapTimeMs);
            this.AttachDelegate("PreviousLapTimeColor", () => PreviousLapColor());
            this.AttachDelegate("BestLapTime", () => LapTime(_d.bestLapTimeMs));
            this.AttachDelegate("BestLapTimeMs", () => _d.bestLapTimeMs);
            this.AttachDelegate("EstimatedLapTime", () => LapTime(_d.estimatedLapTimeMs));
            this.AttachDelegate("EstimatedLapTimeMs", () => _d.estimatedLapTimeMs);
            this.AttachDelegate("ReferenceLapTime", () => LapTime(_d.referenceLapTimeMs));
            this.AttachDelegate("ReferenceLapTimeMs", () => _d.referenceLapTimeMs);
            this.AttachDelegate("PerfDelta", () => Guard(_d.perfDelta));
            this.AttachDelegate("PerfDeltaText", () => FormatPerfDelta(_d.perfDelta));
            this.AttachDelegate("PerfDeltaColor", () => PerfDeltaColor(_d.perfDelta));
            this.AttachDelegate("PerfDeltaLastLapText", () => FormatPerfDelta(_d.perfDeltaLastLapSaved));
            this.AttachDelegate("GapToCarAhead", () => Guard(_d.gapToCarAhead));
            this.AttachDelegate("HeadwindKmh", () => Guard(_d.headwindKmh));
            this.AttachDelegate("IsCaution", () => _d.isCaution);

            // ---- nearby drivers panel ----
            this.AttachDelegate("Nearby1Name", () => Tag(_d.nearby1Name));
            this.AttachDelegate("Nearby1Delta", () => Guard(_d.nearby1Delta));
            this.AttachDelegate("Nearby1DeltaText", () => AbsText(_d.nearby1Delta, _d.nearby1Name));
            this.AttachDelegate("Nearby2Name", () => Tag(_d.nearby2Name));
            this.AttachDelegate("Nearby2Delta", () => Guard(_d.nearby2Delta));
            this.AttachDelegate("Nearby2DeltaText", () => AbsText(_d.nearby2Delta, _d.nearby2Name));
            this.AttachDelegate("Nearby3Name", () => Tag(_d.nearby3Name));
            this.AttachDelegate("Nearby3Delta", () => Guard(_d.nearby3Delta));
            this.AttachDelegate("Nearby3DeltaText", () => AbsText(_d.nearby3Delta, _d.nearby3Name));

            // ---- popups (setting splashes + lap popup + banners) ----
            this.AttachDelegate("SplashKind", () => SplashKind());                     // see SplashKind()
            this.AttachDelegate("SplashId", () => _splash != null ? _splash.Id : "");
            this.AttachDelegate("SplashLabel", () => _splash != null ? _splash.Label() : "");
            this.AttachDelegate("SplashValue", () => _splash != null ? _splash.Value() : "");
            this.AttachDelegate("SplashColor", () => _splash != null ? _splash.Color : ColTransparent);
            this.AttachDelegate("SplashTextColor", () => _splash != null ? _splash.TextColor : ColWhite);
            this.AttachDelegate("SplashValueFontSize", () => _splash != null ? _splash.ValueFontSize : 0.0);
            this.AttachDelegate("BannerKind", () => _banner != null ? _banner.Id : "");
            this.AttachDelegate("BannerLabel", () => _banner != null ? _banner.Label() : "");
            this.AttachDelegate("BannerValue", () => _banner != null ? _banner.Value() : "");
            this.AttachDelegate("BannerColor", () => _banner != null ? _banner.Color : ColTransparent);
            this.AttachDelegate("BannerTextColor", () => _banner != null ? _banner.TextColor : ColWhite);
            this.AttachDelegate("BannerValueFontSize", () => _banner != null ? _banner.ValueFontSize : 0.0);

            // lap popup contents (display/styles/style_0/popups.lua -> lap)
            this.AttachDelegate("LapPopupActive", () => _splash != null && _splash.Id == "lap" ? 1 : 0);
            this.AttachDelegate("LapPopupPrevLapTime", () => LapTime(_d.previousLapTimeMs));
            this.AttachDelegate("LapPopupBestLapTime", () => LapTime(_d.bestLapTimeMs));
            this.AttachDelegate("LapPopupDelta", () => FormatPerfDelta(_d.perfDeltaLastLapSaved));
            this.AttachDelegate("LapPopupDeltaColor",
                () => _d.previousLapTimeMs > 0 && _d.previousLapTimeMs <= _d.bestLapTimeMs
                    ? ColPurple : ColChargingGreen);
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (_acc == null)
            {
                // retry opening roughly once a second until the in-game exporter creates the MMF
                if (++_retry >= 60) { _retry = 0; TryOpen(); }
                return;
            }

            try
            {
                _acc.Read(0, out _d);

                if (_d.connected != 1)
                {
                    _splashController.Reset();
                    _bannerController.Reset();
                    _splash = null;
                    _banner = null;
                    return;
                }

                double now = _clock.Elapsed.TotalSeconds;
                double settingSeconds = _d.displayPopupTime * 0.1;   // Popup:setActive, time == 1
                double lapSeconds = _d.displayLapPopupTime * 0.1;    // Popup:setActive, time == 2

                // A driver-preset change loads many settings at once; the cockpit suppresses the
                // resulting popup storm on both controllers (controller.lua).
                bool presetChanged = _presetInitialized
                    && (_d.driverPreset1Active != _lastPreset1 || _d.driverPreset2Active != _lastPreset2);
                _lastPreset1 = _d.driverPreset1Active;
                _lastPreset2 = _d.driverPreset2Active;
                _presetInitialized = true;

                _banner = _bannerController.Update(now, settingSeconds, lapSeconds, presetChanged, _d.puMode);
                _splash = _splashController.Update(now, settingSeconds, lapSeconds, presetChanged, _d.puMode);
            }
            catch
            {
                Close();
            }
        }

        public void End(PluginManager pluginManager) { Close(); }

        private void TryOpen()
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(MmfName, MemoryMappedFileRights.Read);
                _acc = _mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(VrcData)), MemoryMappedFileAccess.Read);
            }
            catch
            {
                Close();
            }
        }

        private void Close()
        {
            if (_acc != null) _acc.Dispose();
            if (_mmf != null) _mmf.Dispose();
            _acc = null;
            _mmf = null;
        }

        // ---------------- formatting helpers ----------------

        private static float Guard(float v) { return (float.IsNaN(v) || float.IsInfinity(v)) ? 0f : v; }

        private static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private static string PuModeName(int mode)
        {
            if (mode < 1) return "";
            return mode <= PuModeNames.Length ? PuModeNames[mode - 1] : mode.ToString(Inv);
        }

        private static string SlmLatchName(int latch)
        {
            return latch >= 0 && latch < SlmLatchNames.Length ? SlmLatchNames[latch] : "OFF";
        }

        // Matches lights/stw_controller.lua LATCH_COLORS: 1 dim white, 2 blue, 3 yellow.
        private static string SlmLatchColor(int latch)
        {
            switch (latch)
            {
                case 1: return "#FFB3B3B3";
                case 2: return ColRacingBlue;
                case 3: return ColWarningYellow;
                default: return ColTransparent;
            }
        }

        private string BrakeBiasText() { return (_d.frontBias * 100f).ToString("0.0", Inv); }

        // The cockpit only refreshes the BBal popup when the formatted value changes.
        private int BrakeBiasKey() { return (int)Math.Round(_d.frontBias * 1000f); }

        private float FuelGrams(float litres)
        {
            float g = Guard(litres) * Guard(_d.fuelKgPerLiter) * 1000f;
            return g > 999f ? 999f : g;
        }

        private float WorstLegalityWearMm()
        {
            float f = Guard(_d.frontLegalityWear), m = Guard(_d.midLegalityWear), r = Guard(_d.rearLegalityWear);
            float worst = f > m ? f : m;
            if (r > worst) worst = r;
            return worst * 1000f;
        }

        private static string Signed(float v, int decimals)
        {
            string fmt = decimals == 0 ? "0" : "0." + new string('0', decimals);
            return (v >= 0 ? "+" : "-") + Math.Abs(v).ToString(fmt, Inv);
        }

        private static string TempDeltaText(int delta)
        {
            // "%+03d": sign plus at least two digits.
            return (delta >= 0 ? "+" : "-") + Math.Abs(delta).ToString("00", Inv);
        }

        // pages.lua: cool below -10, optimum inside +/-10, no background above +10.
        private static string TyreColor(int delta)
        {
            if (delta < -10) return ColCoolBlue;
            if (delta > 10) return ColTransparent;
            return ColRbrGreen;
        }

        private static string BrakeColor(int discTemp)
        {
            return discTemp < 200 ? ColCoolBlue : ColRbrGreen;
        }

        private static string LapTime(int ms)
        {
            if (ms <= 0) return "0:00:00";
            if (ms > 999999) ms = 999999;
            TimeSpan ts = TimeSpan.FromMilliseconds(ms);
            return ((int)ts.TotalMinutes).ToString(Inv) + ":" + ts.Seconds.ToString("00", Inv)
                   + "." + ts.Milliseconds.ToString("000", Inv);
        }

        // data.lua: "0.00" when there is no delta, otherwise signed, 1 decimal past 10 s.
        private static string FormatPerfDelta(float v)
        {
            if (v == 0f) return "0.00";
            double a = Math.Abs(v);
            return (v > 0f ? "+" : "-") + a.ToString(a >= 10.0 ? "0.0" : "0.00", Inv);
        }

        private static string PerfDeltaColor(float v)
        {
            if (v == 0f) return ColWhite;
            return v < 0f ? ColChargingGreen : ColRed;
        }

        private string PreviousLapColor()
        {
            if (_d.previousLapTimeMs == 0) return ColWhite;
            if (_d.bestLapTimeMs == _d.previousLapTimeMs) return ColPurple;
            return _d.previousLapTimeMs < _d.bestLapTimeMs ? ColChargingGreen : ColWarningYellow;
        }

        private static string LapDeltaModeName(int mode)
        {
            switch (mode)
            {
                case 1: return "BEST";
                case 2: return "TARGET";
                default: return "LAST";
            }
        }

        private string StarterStatus()
        {
            if (_d.isEngineRunning != 0) return "";
            if (_d.isStarterCranking != 0) return "K Start Engaged";
            return _d.isIgnitionStage2 != 0 ? "K Start Ready" : "P1";
        }

        // 0 none, 1 setting splash, 2 lap popup, 3 display off, 4 boot splash
        private int SplashKind()
        {
            if (_splash == null) return 0;
            switch (_splash.Id)
            {
                case "off": return 3;
                case "splash": return 4;
                case "lap": return 2;
                default: return 1;
            }
        }

        private static string Tag(int packed)
        {
            if (packed == 0) return "";
            char[] c = new char[3];
            for (int i = 0; i < 3; i++) c[i] = (char)((packed >> (8 * i)) & 0xFF);
            return new string(c).Trim();
        }

        private static string AbsText(float delta, int packedName)
        {
            if (packedName == 0) return "";
            return Math.Abs(Guard(delta)).ToString("0.0", Inv);
        }

        // ---------------- popup machinery ----------------
        // Mirrors display/classes/Popup.lua + styles/style_0/controller.lua.

        private class Popup
        {
            public readonly string Id;
            public readonly int TimeKind;            // 0 = condition-driven, 1 = setting, 2 = lap
            public readonly Func<int> Watch;         // value whose change triggers the popup
            public readonly Func<bool> Forced;       // condition that keeps it on screen
            public readonly Func<string> Label;
            public readonly Func<string> Value;
            public readonly string Color;
            public readonly string TextColor;
            public readonly double ValueFontSize;

            public int Last;
            public bool Initialized;
            public bool ForcedNow;
            public double Until;

            public Popup(string id, int timeKind, Func<int> watch, Func<bool> forced,
                         Func<string> label, Func<string> value,
                         string color, string textColor, double valueFontSize)
            {
                Id = id;
                TimeKind = timeKind;
                Watch = watch;
                Forced = forced;
                Label = label;
                Value = value;
                Color = color;
                TextColor = textColor;
                ValueFontSize = valueFontSize;
            }

            public bool Check()
            {
                int now = Watch();
                bool forced = Forced();
                ForcedNow = forced;

                if (!Initialized)
                {
                    Initialized = true;
                    Last = now;
                    return false;
                }

                bool triggered = Last != now || forced;
                Last = now;
                return triggered;
            }

            public void SetActive(double now, double settingSeconds, double lapSeconds)
            {
                double add = TimeKind == 1 ? settingSeconds : (TimeKind == 2 ? lapSeconds : 0.0);
                Until = now + add;
            }

            public bool Active(double now) { return Until > now; }

            public void Reset() { Initialized = false; Until = 0; ForcedNow = false; }
        }

        private class PopupController
        {
            private readonly Popup[] _list;
            private Popup _current;
            private int _lastPuMode;
            private bool _puModeInitialized;

            public PopupController(Popup[] list) { _list = list; }

            public void Reset()
            {
                _current = null;
                _puModeInitialized = false;
                foreach (Popup p in _list) p.Reset();
            }

            public Popup Update(double now, double settingSeconds, double lapSeconds, bool presetChanged, int puMode)
            {
                bool puModeChanged = _puModeInitialized && puMode != _lastPuMode;
                _lastPuMode = puMode;
                _puModeInitialized = true;

                Popup newPopup = null;
                Popup puModePopup = null;
                bool currentTriggered = false;

                foreach (Popup p in _list)
                {
                    if (!p.Check()) continue;
                    if (p == _current) currentTriggered = true;

                    if (p.Id == "puMode" && puModeChanged) puModePopup = p;
                    else if (newPopup == null) newPopup = p;
                }

                if (presetChanged)
                {
                    if (_current != null && _current.Active(now)) return _current;
                    _current = null;
                    return null;
                }

                if (puModePopup != null) newPopup = puModePopup;

                if (currentTriggered && _current != null) _current.SetActive(now, settingSeconds, lapSeconds);

                if (newPopup != null)
                {
                    if (_current == null)
                    {
                        _current = newPopup;
                        _current.SetActive(now, settingSeconds, lapSeconds);
                        return _current;
                    }

                    if (_current != newPopup)
                    {
                        if (!_current.ForcedNow) _current.Until = 0;
                        _current = newPopup;
                        _current.SetActive(now, settingSeconds, lapSeconds);
                    }

                    return _current;
                }

                if (_current != null && !_current.Active(now)) _current = null;
                return _current;
            }
        }
    }

    // Field order/types MUST match the LAYOUT string in vrc_fa26_telemetry.lua.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VrcData
    {
        public uint counter;
        public int connected;

        public int strat;
        public int puMode;
        public int deploymentStrat;
        public int deploymentSplit;
        public float kersInput;
        public float kersCharge;
        public float kersRegen;
        public float kersDeployMJ;
        public float kersRegenMJ;
        public float kersRegenLimitMJ;
        public float kersChargeDelta;
        public float kersChargeLast;
        public float kersChargeESOC;
        public float rearMotorPowerKW;
        public float rearMotorTorque;
        public float mgukMaxPower;
        public float mgukMaxPowerLimit;
        public float mgukMaxPowerReduction;
        public int mgukMaxPowerReset;
        public float mgukMaxPowerResetSpeedThreshold;
        public int isOvertakeActive;
        public int isOvertakeActivePending;
        public int isHybridBoostActive;
        public int isHybridAntiActive;
        public int isPowerLimited;
        public int isPowerLimitedPending;
        public float powerLimitedExitTorque;
        public float puTemperature;
        public float puTorque;

        public int drsLatch;
        public int drsMode;
        public int drsAvailable;
        public int drsEnabledLap;

        public float frontBias;
        public float brakeBiasLive;
        public float brakeMigration;
        public float brakeBiasTargetDelta;
        public int brakeShapeMap;
        public int brakeBalanceMap;
        public float brakePressure;
        public float ebb;

        public int diffEntry;
        public int diffMid;
        public int diffExit;
        public int diffPower;
        public int diffCoast;

        public int fuelMap;
        public int pedalMap;
        public int torqueMap;
        public int engineBrake;
        public float fuelSavedLastLap;
        public float fuelDelta;
        public float fuelLevel;
        public float fuelKgPerLiter;
        public float engineLifeLeft;

        public float torqueDemand;
        public float torqueDriverDemand;
        public float torqueOut;
        public float throttleRaw;
        public float throttlePedal;

        public int targetLapTimeMs;
        public int targetStintLaps;
        public int lapDeltaMode;
        public int lapTimeDelta;

        public int isEngineRunning;
        public int isAntistallActive;
        public int isIgnitionStage1;
        public int isIgnitionStage2;
        public int isSteeringWheelAttached;
        public int isSteeringWheelBooted;
        public int isStarterCranking;
        public int isPitLimiterActive;
        public int isConstantSpeedLimiterActive;
        public int isBrakeMagicActive;
        public int isFirstGearShift;
        public float pitSpeedLimit;
        public float launchLoadPerc;
        public int driverPreset1Active;
        public int driverPreset2Active;
        public int driverPresetActiveIndex;

        public float plankWear;
        public float frontLegalityWear;
        public float midLegalityWear;
        public float rearLegalityWear;

        public int aeroRearWingGurney;
        public int aeroLouvers;
        public int aeroFrontWingDamage;
        public int tyreCompoundRange;
        public int compoundIndex;

        public int tyreTempDeltaFL;
        public int tyreTempDeltaFR;
        public int tyreTempDeltaRL;
        public int tyreTempDeltaRR;
        public int brakeDiscTempFL;
        public int brakeDiscTempFR;
        public int brakeDiscTempRL;
        public int brakeDiscTempRR;

        public int displayOption1;
        public int displayOption2;
        public int displayOption3;
        public int displayPopupTime;
        public int displayLapPopupTime;
        public int displayBacklightBrightness;
        public int ledShiftPattern;
        public int ledBrightness;
        public int lightState;

        public int lapCount;
        public int sessionLaps;
        public int lapsRemaining;
        public int previousLapTimeMs;
        public int bestLapTimeMs;
        public int estimatedLapTimeMs;
        public int referenceLapTimeMs;
        public float perfDelta;
        public float perfDeltaLastLapSaved;
        public float gapToCarAhead;
        public float headwindKmh;
        public int isCaution;

        public int nearby1Name;
        public float nearby1Delta;
        public int nearby2Name;
        public float nearby2Delta;
        public int nearby3Name;
        public float nearby3Delta;
    }
}
