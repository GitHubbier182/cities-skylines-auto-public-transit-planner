using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoPublicTransit
{
    public partial class Manager : MonoBehaviour
    {
        private const uint DepotReturnFastDespawnCheckIntervalFrames = 16u;
        private const uint DepotReturnFastDespawnTimeoutFrames = 32768u;
        private const float DepotReturnFastDespawnRadius = 8f;
        private const float DepotReturnFastDespawnGateInset = 0.65f;
        private const float DepotReturnFastDespawnRadiusSqr = DepotReturnFastDespawnRadius * DepotReturnFastDespawnRadius;

        private uint _nextDepotDispatchJamCacheRefreshFrame;
        private uint _nextDepotReturnFastDespawnCheckFrame;
        private readonly Dictionary<ushort, int> _depotDispatchStandingBusesByDepot = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, DepotReturnFastDespawnState> _depotReturnFastDespawnByVehicle = new Dictionary<ushort, DepotReturnFastDespawnState>();
        private readonly List<ushort> _depotReturnFastDespawnRemovals = new List<ushort>();

        private class DepotReturnFastDespawnState
        {
            public ushort LineId;
            public ushort DepotId;
            public uint StartedFrame;
        }

        internal bool TrySteerBusDepotDispatch(
            ushort lineId,
            ref TransferManager.TransferOffer depotOffer,
            string source)
        {
            if (lineId == 0)
                return false;

            TransportManager tm = TransportManager.instance;
            BuildingManager bm = BuildingManager.instance;
            if (tm == null || bm == null || lineId >= tm.m_lines.m_size)
                return false;

            ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0 || !line.Complete)
                return false;

            TransportInfo lineInfo = line.Info;
            if (lineInfo == null || lineInfo.m_transportType != TransportInfo.TransportType.Bus)
                return false;

            if (IsProtectedFromAptManagement(lineId, ref line))
                return false;

            ushort currentDepotId = depotOffer.Building;
            if (!IsEligibleBusDispatchDepot(currentDepotId, bm, lineInfo))
                return false;

            RefreshDepotDispatchJamCacheIfNeeded();
            bool currentDepotJammed = IsDepotTemporarilyJammedForDispatch(currentDepotId);
            int currentDepotStandingBuses = GetDepotDispatchStandingBusCount(currentDepotId);

            Vector3 anchor;
            if (!TryGetLineFirstStopPosition(ref line, out anchor))
                anchor = depotOffer.Position;

            if (!IsFiniteVector(anchor))
                return false;

            ushort preferredDepotId;
            Vector3 preferredPosition;
            float preferredDistance;
            int skippedJammedDepots;
            if (!TryFindNearestEligibleBusDepot(
                anchor,
                bm,
                lineInfo,
                true,
                out preferredDepotId,
                out preferredPosition,
                out preferredDistance,
                out skippedJammedDepots))
                return false;

            if (preferredDepotId == 0 || preferredDepotId == currentDepotId)
                return false;

            float currentDistance = DistanceXZ(anchor, depotOffer.Position);
            if (!IsFiniteDistance(currentDistance) ||
                !IsFiniteDistance(preferredDistance) ||
                (!currentDepotJammed && preferredDistance + 1f >= currentDistance))
                return false;

            depotOffer.Building = preferredDepotId;
            depotOffer.Position = preferredPosition;

            TransitLogging.Verbose(
                "DEPOT_DISPATCH_REDIRECT: line=" + lineId +
                ", source=" + source +
                ", fromDepot=" + currentDepotId +
                ", toDepot=" + preferredDepotId +
                ", currentDistance=" + currentDistance.ToString("0") +
                ", preferredDistance=" + preferredDistance.ToString("0") +
                ", currentDepotJammed=" + currentDepotJammed +
                ", currentDepotStandingBuses=" + currentDepotStandingBuses +
                ", skippedJammedDepots=" + skippedJammedDepots + ".");

            return true;
        }

        internal bool TrySteerReturningBusToNearestDepot(ushort vehicleId, ref Vehicle vehicle, string source)
        {
            if (vehicleId == 0 || (vehicle.m_flags & Vehicle.Flags.Created) == 0)
                return false;

            ushort lineId = vehicle.m_transportLine;
            if (lineId == 0)
                return false;

            TransportManager tm = TransportManager.instance;
            BuildingManager bm = BuildingManager.instance;
            if (tm == null || bm == null || lineId >= tm.m_lines.m_size)
                return false;

            ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0 || !line.Complete)
                return false;

            TransportInfo lineInfo = line.Info;
            if (lineInfo == null || lineInfo.m_transportType != TransportInfo.TransportType.Bus)
                return false;

            if (IsProtectedFromAptManagement(lineId, ref line))
                return false;

            VehicleInfo vehicleInfo = vehicle.Info;
            if (vehicleInfo == null || !(vehicleInfo.m_vehicleAI is BusAI))
                return false;

            ushort currentDepotId = vehicle.m_sourceBuilding;
            if (!IsEligibleBusDispatchDepot(currentDepotId, bm, lineInfo))
                return false;

            RefreshDepotDispatchJamCacheIfNeeded();
            bool currentDepotJammed = IsDepotTemporarilyJammedForDispatch(currentDepotId);
            int currentDepotStandingBuses = GetDepotDispatchStandingBusCount(currentDepotId);

            Vector3 anchor = vehicle.GetLastFramePosition();
            if (!IsFiniteVector(anchor))
                return false;

            ushort preferredDepotId;
            Vector3 preferredPosition;
            float preferredDistance;
            int skippedJammedDepots;
            bool foundPreferredDepot = TryFindNearestEligibleBusDepot(
                anchor,
                bm,
                lineInfo,
                true,
                out preferredDepotId,
                out preferredPosition,
                out preferredDistance,
                out skippedJammedDepots);

            float currentDistance = DistanceXZ(anchor, bm.m_buildings.m_buffer[currentDepotId].m_position);
            bool preferredDepotIsCloser =
                IsFiniteDistance(currentDistance) &&
                IsFiniteDistance(preferredDistance) &&
                preferredDistance + 1f < currentDistance;
            bool shouldRedirect =
                foundPreferredDepot &&
                IsFiniteDistance(currentDistance) &&
                IsFiniteDistance(preferredDistance) &&
                preferredDepotId != 0 &&
                preferredDepotId != currentDepotId &&
                preferredDepotIsCloser;

            ushort returnDepotId = currentDepotId;
            if (shouldRedirect)
            {
                vehicleInfo.m_vehicleAI.SetSource(vehicleId, ref vehicle, preferredDepotId);
                returnDepotId = preferredDepotId;

                TransitLogging.Verbose(
                    "DEPOT_RETURN_REDIRECT: vehicle=" + vehicleId +
                    ", line=" + lineId +
                    ", source=" + source +
                    ", fromDepot=" + currentDepotId +
                    ", toDepot=" + preferredDepotId +
                    ", currentDistance=" + currentDistance.ToString("0") +
                    ", preferredDistance=" + preferredDistance.ToString("0") +
                    ", currentDepotJammed=" + currentDepotJammed +
                    ", currentDepotStandingBuses=" + currentDepotStandingBuses +
                    ", skippedJammedDepots=" + skippedJammedDepots + ".");
            }

            TrackDepotReturnFastDespawn(vehicleId, lineId, returnDepotId);
            return shouldRedirect;
        }

        private bool TryFindNearestEligibleBusDepot(
            Vector3 anchor,
            BuildingManager bm,
            TransportInfo lineInfo,
            bool avoidJammedDepots,
            out ushort depotId,
            out Vector3 depotPosition,
            out float distance,
            out int skippedJammedDepots)
        {
            depotId = 0;
            depotPosition = Vector3.zero;
            distance = float.MaxValue;
            skippedJammedDepots = 0;

            if (bm == null || lineInfo == null)
                return false;

            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                if (!IsEligibleBusDispatchDepot(id, bm, lineInfo))
                    continue;

                if (avoidJammedDepots && IsDepotTemporarilyJammedForDispatch(id))
                {
                    skippedJammedDepots++;
                    continue;
                }

                ref Building building = ref bm.m_buildings.m_buffer[id];
                if (!IsFiniteVector(building.m_position))
                    continue;

                float candidateDistance = DistanceXZ(anchor, building.m_position);
                if (!IsFiniteDistance(candidateDistance) || candidateDistance >= distance)
                    continue;

                depotId = id;
                depotPosition = building.m_position;
                distance = candidateDistance;
            }

            return depotId != 0;
        }

        private void RefreshDepotDispatchJamCacheIfNeeded()
        {
            SimulationManager sm = SimulationManager.instance;
            if (sm == null)
                return;

            uint currentFrame = sm.m_currentFrameIndex;
            if (_nextDepotDispatchJamCacheRefreshFrame != 0 &&
                unchecked(_nextDepotDispatchJamCacheRefreshFrame - currentFrame) < 0x80000000u)
                return;

            _nextDepotDispatchJamCacheRefreshFrame = currentFrame + DepotDispatchJamCacheRefreshFrames;

            List<DepotAccessProbe> probes = CollectBusDepotAccessProbes();
            int worstCount;
            Vector3 worstPosition;
            CountStandingBusVehiclesAtDepotEntrances(
                probes,
                out worstCount,
                out worstPosition,
                _depotDispatchStandingBusesByDepot);
        }

        private bool IsDepotTemporarilyJammedForDispatch(ushort depotId)
        {
            int standingBuses;
            return _depotDispatchStandingBusesByDepot.TryGetValue(depotId, out standingBuses) &&
                   standingBuses >= BusSpawnHealthDepotQueueWarningCountPerDepot;
        }

        private int GetDepotDispatchStandingBusCount(ushort depotId)
        {
            int standingBuses;
            return _depotDispatchStandingBusesByDepot.TryGetValue(depotId, out standingBuses)
                ? standingBuses
                : 0;
        }

        private void TrackDepotReturnFastDespawn(ushort vehicleId, ushort lineId, ushort depotId)
        {
            if (vehicleId == 0 || depotId == 0)
                return;

            SimulationManager sm = SimulationManager.instance;
            if (sm == null)
                return;

            _depotReturnFastDespawnByVehicle[vehicleId] = new DepotReturnFastDespawnState
            {
                LineId = lineId,
                DepotId = depotId,
                StartedFrame = sm.m_currentFrameIndex
            };
        }

        private void UpdateDepotReturnFastDespawn()
        {
            if (_depotReturnFastDespawnByVehicle.Count == 0)
                return;

            SimulationManager sm = SimulationManager.instance;
            if (sm == null)
                return;

            uint currentFrame = sm.m_currentFrameIndex;
            if (_nextDepotReturnFastDespawnCheckFrame != 0 &&
                unchecked(_nextDepotReturnFastDespawnCheckFrame - currentFrame) < 0x80000000u)
                return;

            _nextDepotReturnFastDespawnCheckFrame = currentFrame + DepotReturnFastDespawnCheckIntervalFrames;

            VehicleManager vm = VehicleManager.instance;
            BuildingManager bm = BuildingManager.instance;
            if (vm == null || bm == null)
                return;

            _depotReturnFastDespawnRemovals.Clear();
            foreach (KeyValuePair<ushort, DepotReturnFastDespawnState> pair in _depotReturnFastDespawnByVehicle)
            {
                ushort vehicleId = pair.Key;
                DepotReturnFastDespawnState state = pair.Value;
                if (vehicleId == 0 || vehicleId >= vm.m_vehicles.m_size || state == null)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                ref Vehicle vehicle = ref vm.m_vehicles.m_buffer[vehicleId];
                if ((vehicle.m_flags & Vehicle.Flags.Created) == 0)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                if (unchecked(currentFrame - state.StartedFrame) > DepotReturnFastDespawnTimeoutFrames)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                if (vehicle.m_transportLine != 0)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                if ((vehicle.m_flags & Vehicle.Flags.GoingBack) == 0)
                    continue;

                ushort depotId = vehicle.m_sourceBuilding != 0 ? vehicle.m_sourceBuilding : state.DepotId;
                if (depotId == 0 || depotId >= bm.m_buildings.m_size)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                ref Building depot = ref bm.m_buildings.m_buffer[depotId];
                if ((depot.m_flags & Building.Flags.Created) == 0)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                Vector3 vehiclePosition = vehicle.GetLastFramePosition();
                if (!IsFiniteVector(vehiclePosition))
                    continue;

                Vector3 accessPosition = GetDepotAccessPosition(ref depot);
                if (!IsFiniteVector(accessPosition))
                    accessPosition = depot.m_position;

                Vector3 releasePosition = GetDepotGateReleasePosition(ref depot, accessPosition);
                float distanceSqr = SqrDistanceXZ(vehiclePosition, releasePosition);
                if (!IsFiniteDistance(distanceSqr) || distanceSqr > DepotReturnFastDespawnRadiusSqr)
                    continue;

                VehicleInfo vehicleInfo = vehicle.Info;
                if (vehicleInfo == null || vehicleInfo.m_vehicleAI == null)
                {
                    _depotReturnFastDespawnRemovals.Add(vehicleId);
                    continue;
                }

                vehicleInfo.m_vehicleAI.SetSource(vehicleId, ref vehicle, 0);
                vm.ReleaseVehicle(vehicleId);
                _depotReturnFastDespawnRemovals.Add(vehicleId);

                TransitLogging.Verbose(
                    "DEPOT_RETURN_FAST_DESPAWN: vehicle=" + vehicleId +
                    ", line=" + state.LineId +
                    ", depot=" + depotId +
                    ", gateDistance=" + Mathf.Sqrt(distanceSqr).ToString("0") +
                    ", radius=" + DepotReturnFastDespawnRadius.ToString("0") +
                    ", accessDistance=" + DistanceXZ(vehiclePosition, accessPosition).ToString("0") +
                    ", depotDistance=" + DistanceXZ(vehiclePosition, depot.m_position).ToString("0") + ".");
            }

            for (int i = 0; i < _depotReturnFastDespawnRemovals.Count; i++)
                _depotReturnFastDespawnByVehicle.Remove(_depotReturnFastDespawnRemovals[i]);

            _depotReturnFastDespawnRemovals.Clear();
        }

        private Vector3 GetDepotGateReleasePosition(ref Building depot, Vector3 accessPosition)
        {
            if (!IsFiniteVector(accessPosition) || !IsFiniteVector(depot.m_position))
                return depot.m_position;

            return Vector3.Lerp(accessPosition, depot.m_position, DepotReturnFastDespawnGateInset);
        }

        private bool IsEligibleBusDispatchDepot(ushort depotId, BuildingManager bm, TransportInfo lineInfo)
        {
            if (depotId == 0 || bm == null || lineInfo == null || depotId >= bm.m_buildings.m_size)
                return false;

            ref Building building = ref bm.m_buildings.m_buffer[depotId];
            if ((building.m_flags & Building.Flags.Created) == 0)
                return false;

            BuildingInfo info = building.Info;
            if (info == null || info.m_class == null || info.m_buildingAI == null)
                return false;

            if (info.m_class.m_service != ItemClass.Service.PublicTransport ||
                info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                return false;

            DepotAI depotAI = info.m_buildingAI as DepotAI;
            if (depotAI == null)
                return false;

            if (!DepotTransportInfoMatches(depotAI.m_transportInfo, lineInfo) &&
                !DepotTransportInfoMatches(depotAI.m_secondaryTransportInfo, lineInfo))
                return false;

            return true;
        }

        private bool DepotTransportInfoMatches(TransportInfo depotInfo, TransportInfo lineInfo)
        {
            if (depotInfo == null || lineInfo == null)
                return false;

            if (depotInfo.m_transportType != TransportInfo.TransportType.Bus)
                return false;

            if (depotInfo.m_vehicleReason != lineInfo.m_vehicleReason)
                return false;

            if (depotInfo.m_class == null || lineInfo.m_class == null)
                return false;

            return depotInfo.m_class.m_service == lineInfo.m_class.m_service &&
                   depotInfo.m_class.m_subService == lineInfo.m_class.m_subService;
        }

        private bool TryGetLineFirstStopPosition(ref TransportLine line, out Vector3 position)
        {
            position = Vector3.zero;
            ushort stopId = line.m_stops;
            if (stopId == 0)
                return false;

            NetManager nm = NetManager.instance;
            if (nm == null || stopId >= nm.m_nodes.m_size)
                return false;

            ref NetNode stop = ref nm.m_nodes.m_buffer[stopId];
            if ((stop.m_flags & NetNode.Flags.Created) == 0)
                return false;

            position = stop.m_position;
            return true;
        }

        private bool IsFiniteVector(Vector3 position)
        {
            return IsFiniteDistance(position.x) &&
                   IsFiniteDistance(position.y) &&
                   IsFiniteDistance(position.z);
        }

        private bool IsFiniteDistance(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
