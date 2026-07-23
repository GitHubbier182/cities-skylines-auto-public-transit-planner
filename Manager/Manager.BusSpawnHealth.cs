using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AutoPublicTransit
{
    public partial class Manager : MonoBehaviour
    {
        private const uint BusSpawnHealthInitialDelayFrames = 2048u;
        private const uint BusSpawnHealthRetryDelayFrames = 2048u;
        private const int BusSpawnHealthMaxPasses = 3;
        private const uint ActiveDepotDispatchMonitorIntervalFrames = 4096u;
        private const float BusSpawnHealthDepotPressureRadius = 72f;
        private const float BusSpawnHealthStandingVelocitySqr = 1f;
        private const int BusSpawnHealthDepotQueueWarningCountPerDepot = 5;
        private const int BusSpawnHealthDepotPressureRequiredJamScans = 3;
        private const int BusSpawnHealthDepotPressureClearScans = 3;
        private const uint DepotDispatchJamCacheRefreshFrames = 256u;
        private int _depotPressureConsecutiveJamScans;
        private int _depotPressureConsecutiveClearScans;
        private bool _depotPressureSustained;

        private class DepotAccessProbe
        {
            public ushort DepotId;
            public Vector3 AccessPosition;
        }

        private void UpdateActiveDepotDispatchMonitor()
        {
            if (IsAnyTransitScanRunning())
                return;

            SimulationManager sm = SimulationManager.instance;
            if (sm == null)
                return;

            uint currentFrame = sm.m_currentFrameIndex;
            if (_nextActiveDepotDispatchCheckFrame != 0 && unchecked(_nextActiveDepotDispatchCheckFrame - currentFrame) < 0x80000000u)
                return;

            _nextActiveDepotDispatchCheckFrame = currentFrame + ActiveDepotDispatchMonitorIntervalFrames;
            BusSpawnHealthSummary health = BuildActiveBusNetworkHealthSummary(true);
            if (health == null || health.CheckedLineCount == 0)
            {
                State.LastBusSpawnHealthSummary = health;
                AutoPublicTransitUI.UpdateScanSummary(State.LastScanSummary);
                return;
            }

            State.LastBusSpawnHealthSummary = health;
            LogBusSpawnHealthSummary(health, 0);
            AutoPublicTransitUI.UpdateScanSummary(State.LastScanSummary);
        }

        private System.Collections.IEnumerator CheckGeneratedBusSpawnHealthDeferred(TransitScanSummary scanSummary)
        {
            if (scanSummary == null || scanSummary.CreatedLineIds == null || scanSummary.CreatedLineIds.Count == 0)
                yield break;

            var lineIds = new List<ushort>(scanSummary.CreatedLineIds);

            yield return WaitForSimulationFrames(BusSpawnHealthInitialDelayFrames);

            BusSpawnHealthSummary health = null;
            for (int pass = 1; pass <= BusSpawnHealthMaxPasses; pass++)
            {
                while (IsAnyTransitScanRunning())
                    yield return null;

                health = BuildBusSpawnHealthSummary(lineIds);
                State.LastBusSpawnHealthSummary = health;
                LogBusSpawnHealthSummary(health, pass);
                AutoPublicTransitUI.UpdateScanSummary(State.LastScanSummary);

                if (health == null || !health.NeedsPlayerAttention)
                    yield break;

                if (pass < BusSpawnHealthMaxPasses)
                    yield return WaitForSimulationFrames(BusSpawnHealthRetryDelayFrames);
            }

        }

        private System.Collections.IEnumerator WaitForSimulationFrames(uint frames)
        {
            SimulationManager sm = SimulationManager.instance;
            if (sm == null)
                yield break;

            uint startFrame = sm.m_currentFrameIndex;
            while (unchecked(sm.m_currentFrameIndex - startFrame) < frames)
                yield return null;
        }

        private BusSpawnHealthSummary BuildBusSpawnHealthSummary(List<ushort> lineIds)
        {
            return BuildBusSpawnHealthSummary(lineIds, false);
        }

        private BusSpawnHealthSummary BuildActiveBusNetworkHealthSummary(bool advanceDepotPressureState)
        {
            return BuildBusSpawnHealthSummary(CollectCompleteBusLineIds(), true, advanceDepotPressureState);
        }

        public void RefreshActiveBusSpawnHealthForUi()
        {
            if (IsAnyTransitScanRunning())
                return;

            bool wasSustained = _depotPressureSustained;
            BusSpawnHealthSummary health = BuildActiveBusNetworkHealthSummary(true);
            State.LastBusSpawnHealthSummary = health;

            if (health != null && health.DepotDispatchPressureLikely != wasSustained)
            {
                TransitLogging.Log(
                    "Overview depot dispatch pressure changed: depotEntranceStandingBuses=" + health.DepotDispatchPressureVehicleCount +
                    ", depotPressureThreshold=" + health.DepotDispatchPressureThreshold +
                    ", worstDepotEntranceStandingBuses=" + health.WorstDepotNearbyBusCount +
                    ", depotPressureJamScans=" + health.DepotDispatchPressureConsecutiveJamScans +
                    ", depotPressureClearScans=" + health.DepotDispatchPressureConsecutiveClearScans +
                    ", depotDispatchPressure=" + health.DepotDispatchPressureLikely + ".");
            }
        }

        private List<ushort> CollectCompleteBusLineIds()
        {
            var lineIds = new List<ushort>();
            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return lineIds;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0)
                    continue;

                if (!line.Complete)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                    continue;

                if (IsProtectedFromAptManagement(lineId, ref line))
                    continue;

                lineIds.Add(lineId);
            }

            return lineIds;
        }

        private BusSpawnHealthSummary BuildBusSpawnHealthSummary(List<ushort> lineIds, bool activeNetworkMonitor)
        {
            return BuildBusSpawnHealthSummary(lineIds, activeNetworkMonitor, false);
        }

        private BusSpawnHealthSummary BuildBusSpawnHealthSummary(List<ushort> lineIds, bool activeNetworkMonitor, bool advanceDepotPressureState)
        {
            var health = new BusSpawnHealthSummary();
            health.CreatedLineCount = lineIds != null ? lineIds.Count : 0;
            health.ActiveNetworkMonitor = activeNetworkMonitor;
            health.DepotCount = CountBusDepotsForSpawnHealth(out health.DepotProblemCount);

            TransitVehicleSpawnDelayStatus spawnDelayStatus;
            if (TransitVehicleSpawnDelayCompatibility.TryGetActiveStatus(out spawnDelayStatus) && spawnDelayStatus != null && spawnDelayStatus.IsActive)
            {
                health.TransitVehicleSpawnDelayActive = true;
                health.TransitVehicleSpawnDelaySettingKnown = spawnDelayStatus.HasBusDelay;
                health.TransitVehicleSpawnDelayBusDelay = spawnDelayStatus.BusDelay;
            }

            if (lineIds == null || lineIds.Count == 0)
                return health;

            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return health;

            for (int i = 0; i < lineIds.Count; i++)
            {
                ushort lineId = lineIds[i];
                if (lineId == 0 || lineId >= tm.m_lines.m_size)
                    continue;

                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                    continue;

                if (IsProtectedFromAptManagement(lineId, ref line))
                    continue;

                health.CheckedLineCount++;
                if (line.Complete)
                    health.CompleteLineCount++;

                int targetVehicles = SafeCalculateTargetVehicleCount(ref line);
                int assignedVehicles;
                int waitingPathVehicles;
                int returningToDepotVehicles;
                int activeVehicles = CountLineVehiclesByState(
                    ref line,
                    lineId,
                    out assignedVehicles,
                    out waitingPathVehicles,
                    out returningToDepotVehicles);
                bool repairVehicleSelection = ShouldRepairBusVehicleSelectionDuringSpawnHealth(lineId, activeNetworkMonitor);
                string vehicleAuditPhase = activeNetworkMonitor && repairVehicleSelection
                    ? "active-monitor-generated-line"
                    : null;
                BusLineVehicleAudit vehicleAudit = AuditBusLineVehicleSelection(lineId, ref line, repairVehicleSelection, vehicleAuditPhase);
                LogBusLineVehicleTargetDiagnostics(
                    lineId,
                    ref line,
                    targetVehicles,
                    assignedVehicles,
                    activeVehicles,
                    waitingPathVehicles,
                    returningToDepotVehicles,
                    activeNetworkMonitor);

                health.TargetVehicleCount += targetVehicles;
                health.AssignedVehicleCount += assignedVehicles;
                health.ActiveVehicleCount += activeVehicles;
                health.WaitingPathVehicleCount += waitingPathVehicles;
                health.ReturningToDepotVehicleCount += returningToDepotVehicles;
                AddVehicleAuditToSpawnHealth(health, lineId, vehicleAudit);

                if (targetVehicles > 0)
                {
                    health.LinesWithTargetVehicles++;

                    if (assignedVehicles == 0)
                        health.LinesWithoutVehicles++;

                    if (assignedVehicles < targetVehicles)
                        health.LinesBelowTarget++;

                    if (assignedVehicles > 0 && activeVehicles == 0)
                        health.LinesOnlyWaitingPathVehicles++;
                }
            }

            health.VehicleShortfallCount = Mathf.Max(0, health.TargetVehicleCount - health.AssignedVehicleCount);
            health.AssignedVehicleRatio = health.TargetVehicleCount > 0
                ? (float)health.AssignedVehicleCount / health.TargetVehicleCount
                : 1f;
            if (activeNetworkMonitor)
                health.ReturningToDepotVehicleCount = Mathf.Max(
                    health.ReturningToDepotVehicleCount,
                    CountCitywideBusVehiclesReturningToDepots());
            AddDepotDispatchPressureMetrics(health, advanceDepotPressureState);
            health.NeedsPlayerAttention = health.CheckedLineCount > 0 &&
                (health.LinesWithoutVehicles > 0 ||
                 health.LinesOnlyWaitingPathVehicles > 0 ||
                 health.UnsafeVehicleModelLineCount > 0 ||
                 health.LinesWithoutSafeCityBusVehicle > 0 ||
                 health.DepotDispatchPressureLikely);
            health.Recommendation = BuildBusSpawnHealthRecommendation(health);
            return health;
        }

        private bool ShouldRepairBusVehicleSelectionDuringSpawnHealth(ushort lineId, bool activeNetworkMonitor)
        {
            if (!activeNetworkMonitor)
                return true;

            TransitScanSummary lastScan = State.LastScanSummary;
            return lastScan != null &&
                   lastScan.CreatedLineIds != null &&
                   lastScan.CreatedLineIds.Contains(lineId);
        }

        private void AddVehicleAuditToSpawnHealth(BusSpawnHealthSummary health, ushort lineId, BusLineVehicleAudit audit)
        {
            if (health == null || audit == null)
                return;

            if (audit.ReplacementApplied)
                health.VehicleModelRepairCount++;

            if (audit.NoSafeCityBusVehicle)
            {
                health.LinesWithoutSafeCityBusVehicle++;
                health.VehicleModelIssueNames = AppendVehicleModelIssueName(health.VehicleModelIssueNames, audit.SelectedVehicleName);
                TransitLogging.Warn(
                    "Bus line " + lineId +
                    " cannot be assigned an ordinary city bus model: " + audit.GetLogSummary() + ".");
                return;
            }

            if (audit.HasUnresolvedIssue)
            {
                health.UnsafeVehicleModelLineCount++;
                health.VehicleModelIssueNames = AppendVehicleModelIssueName(health.VehicleModelIssueNames, audit.SelectedVehicleName);
                TransitLogging.Warn(
                    "Bus line " + lineId +
                    " has an unsafe or missing city bus vehicle model: " + audit.GetLogSummary() + ".");
            }
        }

        private void AddDepotDispatchPressureMetrics(BusSpawnHealthSummary health, bool advanceDepotPressureState)
        {
            if (health == null)
                return;

            List<DepotAccessProbe> depotAccessProbes = CollectBusDepotAccessProbes();
            health.DepotDispatchPressureVehicleCount = CountStandingBusVehiclesAtDepotEntrances(
                depotAccessProbes,
                out health.WorstDepotNearbyBusCount,
                out health.WorstDepotPosition);
            health.DepotDispatchPressureThreshold = GetDepotDispatchPressureStandingBusThreshold(health.DepotCount);
            health.DepotDispatchPressureLikely = IsDepotDispatchPressureLikely(health, advanceDepotPressureState);
            health.DepotDispatchPressureConsecutiveJamScans = _depotPressureConsecutiveJamScans;
            health.DepotDispatchPressureConsecutiveClearScans = _depotPressureConsecutiveClearScans;
        }

        private int GetDepotDispatchPressureStandingBusThreshold(int depotCount)
        {
            return Mathf.Max(BusSpawnHealthDepotQueueWarningCountPerDepot, depotCount * BusSpawnHealthDepotQueueWarningCountPerDepot);
        }

        private bool IsDepotDispatchPressureLikely(BusSpawnHealthSummary health, bool advanceDepotPressureState)
        {
            if (health == null || health.DepotCount <= 0 || health.TargetVehicleCount <= 0)
            {
                if (advanceDepotPressureState)
                    ResetDepotPressureStability();

                return false;
            }

            int pressureThreshold = health.DepotDispatchPressureThreshold > 0
                ? health.DepotDispatchPressureThreshold
                : GetDepotDispatchPressureStandingBusThreshold(health.DepotCount);
            bool jammedThisScan = health.DepotDispatchPressureVehicleCount > pressureThreshold;
            bool clearThisScan = health.DepotDispatchPressureVehicleCount < pressureThreshold;
            if (!advanceDepotPressureState)
                return _depotPressureSustained;

            if (jammedThisScan)
            {
                _depotPressureConsecutiveJamScans = Mathf.Min(
                    BusSpawnHealthDepotPressureRequiredJamScans,
                    _depotPressureConsecutiveJamScans + 1);
                _depotPressureConsecutiveClearScans = 0;

                if (_depotPressureConsecutiveJamScans >= BusSpawnHealthDepotPressureRequiredJamScans)
                    _depotPressureSustained = true;
            }
            else if (clearThisScan)
            {
                _depotPressureConsecutiveClearScans = Mathf.Min(
                    BusSpawnHealthDepotPressureClearScans,
                    _depotPressureConsecutiveClearScans + 1);
                _depotPressureConsecutiveJamScans = 0;
                if (_depotPressureConsecutiveClearScans >= BusSpawnHealthDepotPressureClearScans)
                {
                    _depotPressureSustained = false;
                    _depotPressureConsecutiveJamScans = 0;
                }
            }
            else
            {
                _depotPressureConsecutiveJamScans = 0;
                _depotPressureConsecutiveClearScans = 0;
            }

            return _depotPressureSustained;
        }

        private void ResetDepotPressureStability()
        {
            _depotPressureConsecutiveJamScans = 0;
            _depotPressureConsecutiveClearScans = 0;
            _depotPressureSustained = false;
        }

        private int CountStandingBusVehiclesAtDepotEntrances(
            List<DepotAccessProbe> depotAccessProbes,
            out int worstDepotNearbyBusCount,
            out Vector3 worstDepotPosition,
            Dictionary<ushort, int> countsByDepotId = null)
        {
            worstDepotNearbyBusCount = 0;
            worstDepotPosition = Vector3.zero;
            if (countsByDepotId != null)
                countsByDepotId.Clear();

            if (depotAccessProbes == null || depotAccessProbes.Count == 0)
                return 0;

            VehicleManager vm = VehicleManager.instance;
            if (vm == null)
                return 0;

            var countsByDepot = new int[depotAccessProbes.Count];
            float radiusSqr = BusSpawnHealthDepotPressureRadius * BusSpawnHealthDepotPressureRadius;

            for (ushort vehicleId = 1; vehicleId < vm.m_vehicles.m_size; vehicleId++)
            {
                ref Vehicle vehicle = ref vm.m_vehicles.m_buffer[vehicleId];
                if ((vehicle.m_flags & Vehicle.Flags.Created) == 0)
                    continue;

                if (!IsBusVehicle(ref vehicle))
                    continue;

                if (!IsStandingOutsideDepot(ref vehicle))
                    continue;

                Vector3 position = vehicle.GetLastFramePosition();
                int nearestDepot = -1;
                float nearestSqr = radiusSqr;
                for (int i = 0; i < depotAccessProbes.Count; i++)
                {
                    float sqr = SqrDistanceXZ(position, depotAccessProbes[i].AccessPosition);
                    if (sqr >= nearestSqr)
                        continue;

                    nearestSqr = sqr;
                    nearestDepot = i;
                }

                if (nearestDepot >= 0)
                    countsByDepot[nearestDepot]++;
            }

            int total = 0;
            for (int i = 0; i < countsByDepot.Length; i++)
            {
                total += countsByDepot[i];
                if (countsByDepotId != null)
                    countsByDepotId[depotAccessProbes[i].DepotId] = countsByDepot[i];

                if (countsByDepot[i] <= worstDepotNearbyBusCount)
                    continue;

                worstDepotNearbyBusCount = countsByDepot[i];
                worstDepotPosition = depotAccessProbes[i].AccessPosition;
            }

            return total;
        }

        private bool IsStandingOutsideDepot(ref Vehicle vehicle)
        {
            if ((vehicle.m_flags & Vehicle.Flags.Spawned) == 0)
                return false;

            if ((vehicle.m_flags & Vehicle.Flags.InsideBuilding) != 0)
                return false;

            if (vehicle.GetLastFrameVelocity().sqrMagnitude > BusSpawnHealthStandingVelocitySqr)
                return false;

            Vehicle.Flags stationaryFlags =
                Vehicle.Flags.Stopped |
                Vehicle.Flags.WaitingPath |
                Vehicle.Flags.WaitingSpace |
                Vehicle.Flags.WaitingTarget |
                Vehicle.Flags.WaitingLoading |
                Vehicle.Flags.Congestion;

            return (vehicle.m_flags & stationaryFlags) != 0 ||
                   vehicle.m_waitCounter >= 3 ||
                   vehicle.m_blockCounter >= 2;
        }

        private bool IsBusVehicle(ref Vehicle vehicle)
        {
            if (vehicle.m_transportLine != 0)
            {
                TransportManager tm = TransportManager.instance;
                if (tm != null && vehicle.m_transportLine < tm.m_lines.m_size)
                {
                    ref TransportLine line = ref tm.m_lines.m_buffer[vehicle.m_transportLine];
                    if (IsProtectedFromAptManagement(vehicle.m_transportLine, ref line))
                        return false;

                    TransportInfo lineInfo = line.Info;
                    if (lineInfo != null && lineInfo.m_transportType == TransportInfo.TransportType.Bus)
                        return true;
                }
            }

            VehicleInfo info = vehicle.Info;
            return info != null &&
                   info.m_class != null &&
                   info.m_class.m_service == ItemClass.Service.PublicTransport &&
                   info.m_class.m_subService == ItemClass.SubService.PublicTransportBus;
        }

        private float SqrDistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private int SafeCalculateTargetVehicleCount(ref TransportLine line)
        {
            try
            {
                return Mathf.Max(0, line.CalculateTargetVehicleCount());
            }
            catch
            {
                return 0;
            }
        }

        private int CountLineVehiclesByState(
            ref TransportLine line,
            ushort lineId,
            out int assignedVehicles,
            out int waitingPathVehicles,
            out int returningToDepotVehicles)
        {
            assignedVehicles = 0;
            waitingPathVehicles = 0;
            returningToDepotVehicles = 0;
            VehicleManager vm = VehicleManager.instance;
            if (vm == null)
                return 0;

            int activeVehicles = 0;
            ushort vehicleId = line.m_vehicles;
            int vehicleLimit = Math.Min((int)vm.m_vehicles.m_size, vm.m_vehicles.m_buffer.Length);
            var visited = new HashSet<ushort>();
            while (vehicleId != 0)
            {
                if (vehicleId >= vehicleLimit)
                {
                    TransitLogging.Warn(
                        "Stopped bus spawn health vehicle scan for line " + lineId +
                        " because vehicle id " + vehicleId + " was outside the active vehicle buffer.");
                    break;
                }

                if (!visited.Add(vehicleId))
                {
                    TransitLogging.Warn(
                        "Stopped bus spawn health vehicle scan for line " + lineId +
                        " because its vehicle chain contains a cycle at vehicle " + vehicleId + ".");
                    break;
                }

                ref Vehicle vehicle = ref vm.m_vehicles.m_buffer[vehicleId];
                ushort nextVehicleId = vehicle.m_nextLineVehicle;
                if ((vehicle.m_flags & Vehicle.Flags.Created) != 0)
                {
                    assignedVehicles++;
                    if (IsVehicleReturningToDepot(ref vehicle))
                    {
                        returningToDepotVehicles++;
                    }
                    else if ((vehicle.m_flags & Vehicle.Flags.WaitingPath) != 0)
                    {
                        waitingPathVehicles++;
                    }
                    else
                    {
                        activeVehicles++;
                    }
                }

                vehicleId = nextVehicleId;
                if (visited.Count >= vehicleLimit)
                {
                    TransitLogging.Warn(
                        "Stopped bus spawn health vehicle scan for line " + lineId +
                        " because its vehicle chain reached the vehicle-buffer limit.");
                    break;
                }
            }

            return activeVehicles;
        }

        private bool IsAnyTransitScanRunning()
        {
            return _scanRunning || _metroScanRunning || _trainScanRunning;
        }

        private void LogBusLineVehicleTargetDiagnostics(
            ushort lineId,
            ref TransportLine line,
            int targetVehicles,
            int assignedVehicles,
            int activeVehicles,
            int waitingPathVehicles,
            int returningToDepotVehicles,
            bool activeNetworkMonitor)
        {
            if (activeNetworkMonitor)
                return;

            TransportInfo info = line.Info;
            int globalBudget = 0;
            try
            {
                if (info != null && info.m_class != null)
                    globalBudget = EconomyManager.instance.GetBudget(info.m_class);
            }
            catch
            {
            }

            int effectiveBudget = (globalBudget * line.m_budget + 50) / 100;
            float defaultVehicleDistance = info != null ? info.m_defaultVehicleDistance : 0f;
            List<Vector3> stops = GetExistingLineStops(ref line);
            float pathLength = 0f;
            float maxLegRatio;
            float maxLegExtra;
            string pathFailure;
            bool pathOk = TryComputeTransportLinePathMetrics(
                ref line,
                lineId,
                stops.Count,
                out pathLength,
                out maxLegRatio,
                out maxLegExtra,
                out pathFailure);

            int expectedFromPath = defaultVehicleDistance > 0f && pathOk
                ? Mathf.CeilToInt(effectiveBudget * pathLength / (defaultVehicleDistance * 100f))
                : 0;
            int expectedFromStoredLength = defaultVehicleDistance > 0f && line.m_totalLength > 0f
                ? Mathf.CeilToInt(effectiveBudget * line.m_totalLength / (defaultVehicleDistance * 100f))
                : 0;
            string vehicleName = null;
            try
            {
                vehicleName = GetVehicleInfoDiagnosticName(line.GetLineVehicle(lineId));
            }
            catch
            {
            }

            string message =
                "Bus target vehicle diagnostics: line=" + lineId +
                ", publicNumber=" + line.m_lineNumber +
                ", stops=" + stops.Count +
                ", complete=" + line.Complete +
                ", targetVehicles=" + targetVehicles +
                ", assignedVehicles=" + assignedVehicles +
                ", activeVehicles=" + activeVehicles +
                ", waitingPathVehicles=" + waitingPathVehicles +
                ", returningToDepotVehicles=" + returningToDepotVehicles +
                ", globalBudget=" + globalBudget +
                ", lineBudget=" + line.m_budget +
                ", effectiveBudget=" + effectiveBudget +
                ", storedTotalLength=" + FormatKilometers(line.m_totalLength) +
                ", pathLength=" + (pathOk ? FormatKilometers(pathLength) : "unavailable(" + pathFailure + ")") +
                ", defaultVehicleDistance=" + FormatKilometers(defaultVehicleDistance) +
                ", expectedFromStoredLength=" + expectedFromStoredLength +
                ", expectedFromPath=" + expectedFromPath +
                ", averageInterval=" + line.m_averageInterval +
                ", selectedVehicle=" + (string.IsNullOrEmpty(vehicleName) ? "none" : vehicleName) +
                ".";

            if (targetVehicles <= 1 && (pathLength >= 3000f || stops.Count >= 6))
                TransitLogging.Warn(message);
            else
                TransitLogging.Log(message);
        }

        private bool IsVehicleReturningToDepot(ref Vehicle vehicle)
        {
            return (vehicle.m_flags & Vehicle.Flags.GoingBack) != 0;
        }

        private int CountCitywideBusVehiclesReturningToDepots()
        {
            VehicleManager vm = VehicleManager.instance;
            if (vm == null)
                return 0;

            int returning = 0;
            for (ushort vehicleId = 1; vehicleId < vm.m_vehicles.m_size; vehicleId++)
            {
                ref Vehicle vehicle = ref vm.m_vehicles.m_buffer[vehicleId];
                if ((vehicle.m_flags & Vehicle.Flags.Created) == 0)
                    continue;

                if (!IsBusVehicle(ref vehicle))
                    continue;

                if (IsVehicleReturningToDepot(ref vehicle))
                    returning++;
            }

            return returning;
        }

        private int CountBusDepotsForSpawnHealth(out int depotProblemCount)
        {
            depotProblemCount = 0;
            BuildingManager bm = BuildingManager.instance;
            if (bm == null)
                return 0;

            int depotCount = 0;
            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if (!IsCreatedBusDepot(ref building))
                    continue;

                depotCount++;
                if (building.m_problems.IsNotNone)
                    depotProblemCount++;
            }

            return depotCount;
        }

        private List<DepotAccessProbe> CollectBusDepotAccessProbes()
        {
            var probes = new List<DepotAccessProbe>();
            BuildingManager bm = BuildingManager.instance;
            if (bm == null)
                return probes;

            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if (!IsCreatedBusDepot(ref building))
                    continue;

                probes.Add(new DepotAccessProbe
                {
                    DepotId = id,
                    AccessPosition = GetDepotAccessPosition(ref building)
                });
            }

            return probes;
        }

        private bool IsCreatedBusDepot(ref Building building)
        {
            return (building.m_flags & Building.Flags.Created) != 0 &&
                   building.Info != null &&
                   building.Info.m_class != null &&
                   building.Info.m_class.m_service == ItemClass.Service.PublicTransport &&
                   building.Info.m_class.m_subService == ItemClass.SubService.PublicTransportBus &&
                   building.Info.m_buildingAI is DepotAI;
        }

        private Vector3 GetDepotAccessPosition(ref Building building)
        {
            NetManager nm = NetManager.instance;
            ushort segmentId = building.m_accessSegment;
            if (nm == null || segmentId == 0 || segmentId >= nm.m_segments.m_size)
                return building.m_position;

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return building.m_position;

            Vector3 accessPosition = segment.GetClosestPosition(building.m_position);
            if (float.IsNaN(accessPosition.x) || float.IsNaN(accessPosition.z))
                return building.m_position;

            return accessPosition;
        }

        private string BuildBusSpawnHealthRecommendation(BusSpawnHealthSummary health)
        {
            if (health == null)
                return null;

            if (health.DepotCount <= 0)
                return "No working bus depot was found. Add or enable a bus depot before expecting new APT lines to dispatch buses.";

            if (health.LinesWithoutSafeCityBusVehicle > 0)
                return "APT found generated bus lines that have no compatible ordinary city bus model. Disable coach/intercity-only bus assets for normal bus lines, or enable at least one ordinary city bus model.";

            if (health.UnsafeVehicleModelLineCount > 0)
                return "APT found generated bus lines still selected to use an unsafe coach/intercity-style vehicle model. Change those lines to an ordinary city bus model in the line vehicle selector.";

            if (health.LinesWithoutVehicles > 0 && health.TransitVehicleSpawnDelayActive)
            {
                if (health.TransitVehicleSpawnDelaySettingKnown)
                    return "Transit Vehicle Spawn Delay is active with BusDelay=" + health.TransitVehicleSpawnDelayBusDelay.ToString(CultureInfo.InvariantCulture) + ". Set its Bus spawning delay to 1 or lower until the new lines receive their first buses.";

                return "Transit Vehicle Spawn Delay is active, but APT could not read its bus delay setting. Temporarily lower or disable that mod's bus spawning delay until the new lines receive their first buses.";
            }

            if (health.DepotDispatchPressureLikely)
            {
                if (health.ActiveNetworkMonitor)
                    return "APT's active depot monitor sees buses standing outside a depot entrance. A new depot can help future buses spawn from a clearer location, but buses already queued at this depot still need the exit or traffic jam to clear. Add or move depot capacity near the busiest line clusters, and keep depot exits off congested roads.";

                return "APT created complete lines, but dispatch is far below target and buses are standing outside a depot entrance. A new depot can help future buses spawn from a clearer location, but buses already queued at this depot still need the exit or traffic jam to clear. Add or move depot capacity near the busiest line clusters, and keep depot exits off congested roads.";
            }

            if (health.LinesWithoutVehicles > 0)
                return "APT published the lines, but vanilla dispatch has not assigned buses yet. Check powered, connected bus depots, vehicle limits, and transport or vehicle-spawn mods.";

            if (health.LinesOnlyWaitingPathVehicles > 0)
                return "Buses are assigned but still waiting for paths. Check depot road access, traffic near depots, and mods that alter bus spawning or pathfinding.";

            if (health.LinesBelowTarget > 0)
                return "Some new lines are still below their vanilla target vehicle count; let the city run or add depot capacity if this persists.";

            return "New APT bus lines have active dispatched vehicles.";
        }

        private void LogBusSpawnHealthSummary(BusSpawnHealthSummary health, int pass)
        {
            if (health == null)
            {
                TransitLogging.Warn("Bus spawn health pass " + pass + " did not produce a summary.");
                return;
            }

            string spawnDelay = health.TransitVehicleSpawnDelayActive
                ? (health.TransitVehicleSpawnDelaySettingKnown
                    ? "active(busDelay=" + health.TransitVehicleSpawnDelayBusDelay.ToString(CultureInfo.InvariantCulture) + ")"
                    : "active(busDelay=unknown)")
                : "inactive";

            TransitLogging.Log(
                (pass > 0 ? "Bus spawn health pass " + pass : "Active depot dispatch monitor") +
                ": createdLines=" + health.CreatedLineCount +
                ", checkedLines=" + health.CheckedLineCount +
                ", completeLines=" + health.CompleteLineCount +
                ", depots=" + health.DepotCount +
                ", problemDepots=" + health.DepotProblemCount +
                ", targetVehicles=" + health.TargetVehicleCount +
                ", assignedVehicles=" + health.AssignedVehicleCount +
                ", activeVehicles=" + health.ActiveVehicleCount +
                ", waitingPathVehicles=" + health.WaitingPathVehicleCount +
                ", returningToDepotVehicles=" + health.ReturningToDepotVehicleCount +
                ", vehicleShortfall=" + health.VehicleShortfallCount +
                ", assignedVehicleRatio=" + health.AssignedVehicleRatio.ToString("0.00", CultureInfo.InvariantCulture) +
                ", linesWithTargets=" + health.LinesWithTargetVehicles +
                ", linesWithoutVehicles=" + health.LinesWithoutVehicles +
                ", linesBelowTarget=" + health.LinesBelowTarget +
                ", linesOnlyWaitingPath=" + health.LinesOnlyWaitingPathVehicles +
                ", unsafeVehicleModelLines=" + health.UnsafeVehicleModelLineCount +
                ", vehicleModelRepairs=" + health.VehicleModelRepairCount +
                ", linesWithoutSafeCityBusVehicle=" + health.LinesWithoutSafeCityBusVehicle +
                ", vehicleModelIssueNames=" + (string.IsNullOrEmpty(health.VehicleModelIssueNames) ? "none" : health.VehicleModelIssueNames) +
                ", depotEntranceStandingBuses=" + health.DepotDispatchPressureVehicleCount +
                ", depotPressureThreshold=" + health.DepotDispatchPressureThreshold +
                ", worstDepotEntranceStandingBuses=" + health.WorstDepotNearbyBusCount +
                ", depotPressureJamScans=" + health.DepotDispatchPressureConsecutiveJamScans +
                ", depotPressureClearScans=" + health.DepotDispatchPressureConsecutiveClearScans +
                ", depotDispatchPressure=" + health.DepotDispatchPressureLikely +
                ", transitVehicleSpawnDelay=" + spawnDelay +
                ".");

            if (health.NeedsPlayerAttention && !string.IsNullOrEmpty(health.Recommendation))
                TransitLogging.Warn("Bus spawn health recommendation: " + health.Recommendation);
        }
    }
}
