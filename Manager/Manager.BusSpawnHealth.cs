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

        private System.Collections.IEnumerator CheckGeneratedBusSpawnHealthDeferred(TransitScanSummary scanSummary)
        {
            if (scanSummary == null || scanSummary.CreatedLineIds == null || scanSummary.CreatedLineIds.Count == 0)
                yield break;

            var lineIds = new List<ushort>(scanSummary.CreatedLineIds);

            yield return WaitForSimulationFrames(BusSpawnHealthInitialDelayFrames);

            BusSpawnHealthSummary health = null;
            for (int pass = 1; pass <= BusSpawnHealthMaxPasses; pass++)
            {
                health = BuildBusSpawnHealthSummary(lineIds);
                State.LastBusSpawnHealthSummary = health;
                LogBusSpawnHealthSummary(health, pass);

                if (health == null || !health.NeedsPlayerAttention)
                    yield break;

                if (pass < BusSpawnHealthMaxPasses)
                    yield return WaitForSimulationFrames(BusSpawnHealthRetryDelayFrames);
            }

            if (health != null && health.NeedsPlayerAttention)
                AutoPublicTransitUI.ShowBusSpawnHealthDialogIfNeeded(health);
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
            var health = new BusSpawnHealthSummary();
            health.CreatedLineCount = lineIds != null ? lineIds.Count : 0;
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

                health.CheckedLineCount++;
                if (line.Complete)
                    health.CompleteLineCount++;

                int targetVehicles = SafeCalculateTargetVehicleCount(ref line);
                int assignedVehicles = SafeCountLineVehicles(ref line, lineId);
                int waitingPathVehicles;
                int activeVehicles = CountLineVehiclesByState(ref line, out waitingPathVehicles);

                health.TargetVehicleCount += targetVehicles;
                health.AssignedVehicleCount += assignedVehicles;
                health.ActiveVehicleCount += activeVehicles;
                health.WaitingPathVehicleCount += waitingPathVehicles;

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

            health.NeedsPlayerAttention = health.CheckedLineCount > 0 &&
                (health.LinesWithoutVehicles > 0 || health.LinesOnlyWaitingPathVehicles > 0);
            health.Recommendation = BuildBusSpawnHealthRecommendation(health);
            return health;
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

        private int SafeCountLineVehicles(ref TransportLine line, ushort lineId)
        {
            try
            {
                return Mathf.Max(0, line.CountVehicles(lineId));
            }
            catch
            {
                return 0;
            }
        }

        private int CountLineVehiclesByState(ref TransportLine line, out int waitingPathVehicles)
        {
            waitingPathVehicles = 0;
            VehicleManager vm = VehicleManager.instance;
            if (vm == null)
                return 0;

            int activeVehicles = 0;
            ushort vehicleId = line.m_vehicles;
            int guard = 0;
            while (vehicleId != 0)
            {
                ref Vehicle vehicle = ref vm.m_vehicles.m_buffer[vehicleId];
                ushort nextVehicleId = vehicle.m_nextLineVehicle;
                if ((vehicle.m_flags & Vehicle.Flags.Created) != 0)
                {
                    if ((vehicle.m_flags & Vehicle.Flags.WaitingPath) != 0)
                        waitingPathVehicles++;
                    else
                        activeVehicles++;
                }

                vehicleId = nextVehicleId;
                guard++;
                if (guard >= 16384)
                {
                    TransitLogging.Warn("Stopped bus spawn health vehicle scan because line vehicle list looked invalid.");
                    break;
                }
            }

            return activeVehicles;
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
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null || building.Info.m_class == null)
                    continue;

                if (building.Info.m_class.m_service != ItemClass.Service.PublicTransport)
                    continue;

                if (building.Info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                    continue;

                if (!(building.Info.m_buildingAI is DepotAI))
                    continue;

                depotCount++;
                if (building.m_problems.IsNotNone)
                    depotProblemCount++;
            }

            return depotCount;
        }

        private string BuildBusSpawnHealthRecommendation(BusSpawnHealthSummary health)
        {
            if (health == null)
                return null;

            if (health.DepotCount <= 0)
                return "No working bus depot was found. Add or enable a bus depot before expecting new APT lines to dispatch buses.";

            if (health.LinesWithoutVehicles > 0 && health.TransitVehicleSpawnDelayActive)
            {
                if (health.TransitVehicleSpawnDelaySettingKnown)
                    return "Transit Vehicle Spawn Delay is active with BusDelay=" + health.TransitVehicleSpawnDelayBusDelay.ToString(CultureInfo.InvariantCulture) + ". Set its Bus spawning delay to 1 or lower until the new lines receive their first buses.";

                return "Transit Vehicle Spawn Delay is active, but APT could not read its bus delay setting. Temporarily lower or disable that mod's bus spawning delay until the new lines receive their first buses.";
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
                "Bus spawn health pass " + pass +
                ": createdLines=" + health.CreatedLineCount +
                ", checkedLines=" + health.CheckedLineCount +
                ", completeLines=" + health.CompleteLineCount +
                ", depots=" + health.DepotCount +
                ", problemDepots=" + health.DepotProblemCount +
                ", targetVehicles=" + health.TargetVehicleCount +
                ", assignedVehicles=" + health.AssignedVehicleCount +
                ", activeVehicles=" + health.ActiveVehicleCount +
                ", waitingPathVehicles=" + health.WaitingPathVehicleCount +
                ", linesWithTargets=" + health.LinesWithTargetVehicles +
                ", linesWithoutVehicles=" + health.LinesWithoutVehicles +
                ", linesBelowTarget=" + health.LinesBelowTarget +
                ", linesOnlyWaitingPath=" + health.LinesOnlyWaitingPathVehicles +
                ", transitVehicleSpawnDelay=" + spawnDelay +
                ".");

            if (health.NeedsPlayerAttention && !string.IsNullOrEmpty(health.Recommendation))
                TransitLogging.Warn("Bus spawn health recommendation: " + health.Recommendation);
        }
    }
}
