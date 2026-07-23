using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace AutoPublicTransit
{
    public partial class Manager
    {
        private bool _metroScanRunning;
        private bool _trainScanRunning;
        private static readonly MethodInfo VanillaRailStopPositionMethod = FindVanillaRailStopPositionMethod();

        private sealed class PassengerRailMode
        {
            public string Name;
            public string PluralName;
            public TransportInfo.TransportType TransportType;
            public ItemClass.SubService SubService;
            public VehicleInfo.VehicleType VehicleType;
        }

        private sealed class MetroStationCandidate
        {
            public ushort BuildingId;
            public ushort AnchorNode;
            public Vector3 StopPosition;
            public bool FixedPlatform;
            public int ComponentId;
        }

        private sealed class MetroTrackComponent
        {
            public int Id;
            public readonly List<ushort> Nodes = new List<ushort>();
            public readonly List<MetroStationCandidate> Stations = new List<MetroStationCandidate>();
            public readonly List<HashSet<ushort>> ExistingLineStationSets = new List<HashSet<ushort>>();
            public int ExternalBranchNodes;
        }

        private sealed class MetroLineProbe
        {
            public ushort LineId;
            public int ExpectedStops;
            public int StationCount;
            public int ComponentId;
        }

        public void RunMetroScan()
        {
            RunPassengerRailScan(new PassengerRailMode
            {
                Name = "Metro",
                PluralName = "Metros",
                TransportType = TransportInfo.TransportType.Metro,
                SubService = ItemClass.SubService.PublicTransportMetro,
                VehicleType = VehicleInfo.VehicleType.Metro
            });
        }

        public void RunTrainScan()
        {
            RunPassengerRailScan(new PassengerRailMode
            {
                Name = "Train",
                PluralName = "Trains",
                TransportType = TransportInfo.TransportType.Train,
                SubService = ItemClass.SubService.PublicTransportTrain,
                VehicleType = VehicleInfo.VehicleType.Train
            });
        }

        private void RunPassengerRailScan(PassengerRailMode mode)
        {
            if (_scanRunning || _metroScanRunning || _trainScanRunning)
            {
                TransitLogging.Warn("A transit scan is already running; " + mode.Name + " scan was not started.");
                UpdatePassengerRailScanStatus(mode, "Last " + mode.Name + " scan: another scan is running");
                return;
            }

            SetPassengerRailScanRunning(mode, true);
            UpdatePassengerRailScanStatus(mode, "Last " + mode.Name + " scan: scanning player infrastructure");
            TransitLogging.Log(
                "Starting passenger " + mode.Name +
                " infrastructure scan; demand scanning is intentionally disabled; inter-station distance limit=none; station stops use the vanilla TransportTool platform resolver.");

            var summary = new MetroScanSummary();
            List<MetroLineProbe> probes = null;
            SetPassengerRailScanSummary(mode, summary);
            DateTime startedAt = DateTime.UtcNow;

            try
            {
                TransportManager transportManager = TransportManager.instance;
                NetManager netManager = NetManager.instance;
                BuildingManager buildingManager = BuildingManager.instance;
                if (transportManager == null || netManager == null || buildingManager == null)
                    throw new InvalidOperationException("Metro, track, or building data is not ready yet. Wait for the city to finish loading, then scan again.");

                TransportInfo railInfo = transportManager.GetTransportInfo(mode.TransportType);
                if (railInfo == null)
                    throw new InvalidOperationException("The vanilla passenger " + mode.Name + " transport definition is unavailable in this city.");

                int[] componentByNode;
                Dictionary<int, MetroTrackComponent> components = BuildMetroTrackComponents(netManager, mode, out componentByNode);
                summary.TrackComponents = components.Count;

                List<MetroStationCandidate> stations = CollectMetroStations(buildingManager, netManager, componentByNode, railInfo, mode, summary);
                summary.StationsFound = stations.Count;
                for (int i = 0; i < stations.Count; i++)
                {
                    MetroStationCandidate station = stations[i];
                    MetroTrackComponent component;
                    if (station.ComponentId <= 0 || !components.TryGetValue(station.ComponentId, out component))
                    {
                        summary.StationsWithoutTrack++;
                        continue;
                    }

                    component.Stations.Add(station);
                }

                MarkMetroComponentsWithExistingLines(transportManager, components, stations, componentByNode, summary, mode);

                probes = new List<MetroLineProbe>();
                Randomizer randomizer = SimulationManager.instance.m_randomizer;
                foreach (MetroTrackComponent component in components.Values)
                {
                    if (component.Stations.Count < 2)
                    {
                        if (component.Stations.Count == 1)
                            summary.SingleStationGroupsSkipped++;
                        continue;
                    }

                    summary.ConnectedStationGroups++;

                    if (component.ExternalBranchNodes > 0)
                        summary.JunctionGroupsPlanned++;

                    List<List<MetroStationCandidate>> corridorPlans = BuildMetroCorridorPlans(component, netManager, mode);
                    if (corridorPlans.Count == 0)
                    {
                        summary.FailedLines++;
                        TransitLogging.Warn(
                            mode.Name + " component " + component.Id +
                            " could not be decomposed into connected station corridors.");
                        continue;
                    }

                    summary.PlannedCorridors += corridorPlans.Count;
                    TransitLogging.Log(
                        mode.Name + " component " + component.Id + " planned " + corridorPlans.Count +
                        " corridor(s) from " + component.Stations.Count +
                        " stations using station-track topology; rawTrackJunctionNodes=" + component.ExternalBranchNodes + ".");

                    int existingCorridorsSkipped = 0;
                    for (int corridorIndex = 0; corridorIndex < corridorPlans.Count; corridorIndex++)
                    {
                        List<MetroStationCandidate> corridor = corridorPlans[corridorIndex];
                        if (corridor.Count == 2 && component.Stations.Count > 2)
                        {
                            summary.FailedLines++;
                            TransitLogging.Warn(
                                "Suppressed short " + mode.Name + " shuttle on component " + component.Id +
                                " because its two stations belong to a connected " + component.Stations.Count +
                                "-station network; a trunk or longer through-service is required.");
                            continue;
                        }

                        if (IsPassengerRailCorridorAlreadyServed(component, corridor))
                        {
                            existingCorridorsSkipped++;
                            TransitLogging.Log(
                                "Skipped " + mode.Name + " component " + component.Id + " corridor " + (corridorIndex + 1) +
                                " because one complete existing line already serves every planned station.");
                            continue;
                        }

                        summary.CandidateLines++;
                        MetroLineProbe probe;
                        string failureReason;
                        if (TryCreateMetroLineProbe(component.Id, corridor, railInfo, mode, ref randomizer, out probe, out failureReason))
                        {
                            probes.Add(probe);
                        }
                        else
                        {
                            summary.FailedLines++;
                            TransitLogging.Warn(
                                "Skipped " + mode.Name + " component " + component.Id + " corridor " + (corridorIndex + 1) +
                                " because its validation line could not be prepared: " + failureReason + ".");
                        }
                    }

                    if (existingCorridorsSkipped == corridorPlans.Count)
                        summary.ExistingLineGroupsSkipped++;
                }

                TransitLogging.Log(
                    mode.Name + " infrastructure scan prepared " + probes.Count + " validation line(s): stations=" + summary.StationsFound +
                    ", stationsWithoutTrack=" + summary.StationsWithoutTrack +
                    ", trackComponents=" + summary.TrackComponents +
                    ", connectedStationGroups=" + summary.ConnectedStationGroups +
                    ", existingLines=" + summary.ExistingLines +
                    ", existingGroupsSkipped=" + summary.ExistingLineGroupsSkipped +
                    ", junctionGroupsPlanned=" + summary.JunctionGroupsPlanned +
                    ", plannedCorridors=" + summary.PlannedCorridors + ".");

                StartCoroutine(SettleMetroLineProbes(probes, summary, startedAt, mode));
                probes = null;
            }
            catch (Exception e)
            {
                if (probes != null)
                {
                    for (int i = 0; i < probes.Count; i++)
                        SafeReleaseLine(probes[i].LineId);
                    probes.Clear();
                }

                summary.FailureMessage = e.Message;
                SetPassengerRailScanSummary(mode, summary);
                SetPassengerRailScanRunning(mode, false);
                TransitLogging.Error(mode.Name + " scan failed: " + e);
                UpdatePassengerRailScanSummary(mode, summary);
                ShowPassengerRailScanResult(mode, summary);
            }
        }

        private Dictionary<int, MetroTrackComponent> BuildMetroTrackComponents(NetManager netManager, PassengerRailMode mode, out int[] componentByNode)
        {
            NetNode[] nodes = netManager.m_nodes.m_buffer;
            componentByNode = new int[nodes.Length];
            var components = new Dictionary<int, MetroTrackComponent>();
            int nextComponent = 0;

            for (ushort nodeId = 1; nodeId < nodes.Length; nodeId++)
            {
                if (componentByNode[nodeId] != 0 || !IsCreatedMetroTrackNode(ref nodes[nodeId], mode))
                    continue;

                var component = new MetroTrackComponent { Id = ++nextComponent };
                components.Add(component.Id, component);
                var queue = new Queue<ushort>();
                queue.Enqueue(nodeId);
                componentByNode[nodeId] = component.Id;

                while (queue.Count > 0)
                {
                    ushort current = queue.Dequeue();
                    component.Nodes.Add(current);
                    ref NetNode node = ref nodes[current];
                    int metroDegree = 0;

                    for (int segmentIndex = 0; segmentIndex < 8; segmentIndex++)
                    {
                        ushort segmentId = node.GetSegment(segmentIndex);
                        if (segmentId == 0)
                            continue;

                        ref NetSegment segment = ref netManager.m_segments.m_buffer[segmentId];
                        if (!IsCreatedMetroTrackSegment(ref segment, mode))
                            continue;

                        metroDegree++;
                        ushort other = segment.m_startNode == current ? segment.m_endNode : segment.m_startNode;
                        if (other == 0 || other >= nodes.Length || componentByNode[other] != 0)
                            continue;

                        if (!IsCreatedMetroTrackNode(ref nodes[other], mode))
                            continue;

                        componentByNode[other] = component.Id;
                        queue.Enqueue(other);
                    }

                    if (metroDegree > 2 && node.m_building == 0)
                        component.ExternalBranchNodes++;
                }
            }

            return components;
        }

        private List<MetroStationCandidate> CollectMetroStations(
            BuildingManager buildingManager,
            NetManager netManager,
            int[] componentByNode,
            TransportInfo railInfo,
            PassengerRailMode mode,
            MetroScanSummary summary)
        {
            var stations = new List<MetroStationCandidate>();
            Building[] buildings = buildingManager.m_buildings.m_buffer;
            int transportStationAiCount = 0;
            int depotBackedStationCount = 0;

            for (ushort buildingId = 1; buildingId < buildingManager.m_buildings.m_size; buildingId++)
            {
                ref Building building = ref buildings[buildingId];
                if ((building.m_flags & Building.Flags.Created) == 0)
                    continue;

                BuildingInfo info = building.Info;
                if (info == null || info.m_class == null || info.m_buildingAI == null)
                    continue;
                if (info.m_class.m_service != ItemClass.Service.PublicTransport ||
                    info.m_class.m_subService != mode.SubService)
                    continue;

                if (mode.TransportType == TransportInfo.TransportType.Train && IsCargoTrainStation(info))
                {
                    summary.CargoStationsSkipped++;
                    continue;
                }

                bool supportsMetroLines = false;
                TransportStationAI stationAi = info.m_buildingAI as TransportStationAI;
                if (stationAi != null)
                {
                    TransportInfo lineInfo = stationAi.GetTransportLineInfo();
                    supportsMetroLines = lineInfo != null && lineInfo.m_transportType == mode.TransportType;
                    if (supportsMetroLines)
                        transportStationAiCount++;
                }

                DepotAI depotAi = info.m_buildingAI as DepotAI;
                if (!supportsMetroLines && depotAi != null)
                {
                    supportsMetroLines = IsMetroTransportInfo(depotAi.m_transportInfo, mode) ||
                                         IsMetroTransportInfo(depotAi.m_secondaryTransportInfo, mode);
                    if (supportsMetroLines)
                        depotBackedStationCount++;
                }

                if (!supportsMetroLines)
                    continue;

                Vector3 stopPosition;
                ushort anchorNode;
                bool fixedPlatform;
                if (!TryResolvePassengerRailStation(
                    buildingId,
                    ref building,
                    netManager,
                    componentByNode,
                    railInfo,
                    mode,
                    out anchorNode,
                    out stopPosition,
                    out fixedPlatform))
                {
                    stations.Add(new MetroStationCandidate
                    {
                        BuildingId = buildingId,
                        AnchorNode = 0,
                        StopPosition = building.m_position,
                        FixedPlatform = false,
                        ComponentId = 0
                    });
                    continue;
                }

                int componentId = anchorNode != 0 && anchorNode < componentByNode.Length
                    ? componentByNode[anchorNode]
                    : 0;
                stations.Add(new MetroStationCandidate
                {
                    BuildingId = buildingId,
                    AnchorNode = anchorNode,
                    StopPosition = stopPosition,
                    FixedPlatform = fixedPlatform,
                    ComponentId = componentId
                });
            }

            TransitLogging.Log(
                mode.Name + " passenger-station discovery: stations=" + stations.Count +
                ", transportStationAI=" + transportStationAiCount +
                ", depotBacked=" + depotBackedStationCount +
                ", cargoSkipped=" + summary.CargoStationsSkipped + ".");

            return stations;
        }

        private bool IsMetroTransportInfo(TransportInfo info, PassengerRailMode mode)
        {
            return info != null && info.m_transportType == mode.TransportType;
        }

        private bool IsCargoTrainStation(BuildingInfo info)
        {
            if (info == null)
                return false;

            if (info.m_buildingAI is CargoStationAI)
                return true;

            string aiName = info.m_buildingAI != null ? info.m_buildingAI.GetType().Name : string.Empty;
            string prefabName = info.name ?? string.Empty;
            return ContainsIgnoreCase(aiName, "Cargo") || ContainsIgnoreCase(prefabName, "Cargo");
        }

        private bool TryResolvePassengerRailStation(
            ushort buildingId,
            ref Building building,
            NetManager netManager,
            int[] componentByNode,
            TransportInfo railInfo,
            PassengerRailMode mode,
            out ushort anchorNode,
            out Vector3 stopPosition,
            out bool fixedPlatform)
        {
            anchorNode = 0;
            stopPosition = building.m_position;
            fixedPlatform = false;
            ToolController toolController = ToolsModifierControl.toolController;
            TransportTool transportTool = toolController == null
                ? null
                : toolController.GetComponent<TransportTool>();
            if (transportTool == null || VanillaRailStopPositionMethod == null)
            {
                TransitLogging.Warn(
                    mode.Name + " station " + buildingId +
                    " could not be resolved because the vanilla TransportTool platform resolver is unavailable.");
                return false;
            }

            object[] arguments =
            {
                railInfo,
                (ushort)0,
                buildingId,
                (ushort)0,
                stopPosition,
                false
            };

            bool resolved;
            try
            {
                resolved = (bool)VanillaRailStopPositionMethod.Invoke(transportTool, arguments);
            }
            catch (Exception e)
            {
                TransitLogging.Warn(
                    mode.Name + " station " + buildingId +
                    " vanilla platform resolution failed: " + e.GetBaseException().Message + ".");
                return false;
            }

            if (!resolved || !(arguments[4] is Vector3))
            {
                TransitLogging.Warn(
                    mode.Name + " station " + buildingId +
                    " was rejected by the vanilla TransportTool platform resolver.");
                return false;
            }

            stopPosition = (Vector3)arguments[4];
            fixedPlatform = arguments[5] is bool && (bool)arguments[5];
            NetNode[] nodes = netManager.m_nodes.m_buffer;
            float bestDistance = float.MaxValue;
            for (ushort nodeId = 1; nodeId < nodes.Length; nodeId++)
            {
                if (componentByNode[nodeId] == 0 || !IsCreatedMetroTrackNode(ref nodes[nodeId], mode))
                    continue;

                float distance = Geometry.DistanceXZ(stopPosition, nodes[nodeId].m_position);
                if (distance < bestDistance)
                {
                    anchorNode = nodeId;
                    bestDistance = distance;
                }
            }

            if (anchorNode == 0)
            {
                TransitLogging.Warn(
                    mode.Name + " station " + buildingId +
                    " has a vanilla platform position but no created compatible track component.");
                return false;
            }

            TransitLogging.Log(
                mode.Name + " station " + buildingId +
                " resolved by vanilla platform logic: component=" + componentByNode[anchorNode] +
                ", topologyMappingDistance=" + bestDistance.ToString("0.0", CultureInfo.InvariantCulture) +
                ", fixedPlatform=" + fixedPlatform +
                ", interStationDistanceLimit=none.");
            return true;
        }

        private static MethodInfo FindVanillaRailStopPositionMethod()
        {
            MethodInfo[] methods = typeof(TransportTool).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != "GetStopPosition")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 6 &&
                    parameters[0].ParameterType == typeof(TransportInfo) &&
                    parameters[1].ParameterType == typeof(ushort) &&
                    parameters[2].ParameterType == typeof(ushort) &&
                    parameters[3].ParameterType == typeof(ushort) &&
                    parameters[4].ParameterType == typeof(Vector3).MakeByRefType() &&
                    parameters[5].ParameterType == typeof(bool).MakeByRefType())
                    return method;
            }

            return null;
        }

        private void MarkMetroComponentsWithExistingLines(
            TransportManager transportManager,
            Dictionary<int, MetroTrackComponent> components,
            List<MetroStationCandidate> stations,
            int[] componentByNode,
            MetroScanSummary summary,
            PassengerRailMode mode)
        {
            for (ushort lineId = 1; lineId < transportManager.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref transportManager.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0 ||
                    (line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0 ||
                    !line.Complete)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != mode.TransportType)
                    continue;

                summary.ExistingLines++;
                var stationsByComponent = new Dictionary<int, HashSet<ushort>>();
                ushort firstStop = line.m_stops;
                ushort currentStop = firstStop;
                int stopSafety = 0;
                while (currentStop != 0 && stopSafety++ < 512)
                {
                    int componentId = currentStop < componentByNode.Length ? componentByNode[currentStop] : 0;
                    Vector3 stopPosition = NetManager.instance.m_nodes.m_buffer[currentStop].m_position;
                    MetroStationCandidate nearest = FindNearestMetroStation(stopPosition, stations, 128f, componentId);
                    if (nearest != null)
                    {
                        HashSet<ushort> stationSet;
                        if (!stationsByComponent.TryGetValue(nearest.ComponentId, out stationSet))
                        {
                            stationSet = new HashSet<ushort>();
                            stationsByComponent.Add(nearest.ComponentId, stationSet);
                        }
                        stationSet.Add(nearest.BuildingId);
                    }

                    currentStop = TransportLine.GetNextStop(currentStop);
                    if (currentStop == firstStop)
                        break;
                }

                foreach (KeyValuePair<int, HashSet<ushort>> pair in stationsByComponent)
                {
                    MetroTrackComponent component;
                    if (!components.TryGetValue(pair.Key, out component) || pair.Value.Count == 0)
                        continue;

                    component.ExistingLineStationSets.Add(pair.Value);
                }
            }
        }

        private MetroStationCandidate FindNearestMetroStation(
            Vector3 position,
            List<MetroStationCandidate> stations,
            float maxDistance,
            int requiredComponentId)
        {
            MetroStationCandidate nearest = null;
            float bestDistance = maxDistance;
            for (int i = 0; i < stations.Count; i++)
            {
                MetroStationCandidate station = stations[i];
                if (station.ComponentId <= 0 ||
                    (requiredComponentId > 0 && station.ComponentId != requiredComponentId))
                    continue;

                float distance = Geometry.DistanceXZ(position, station.StopPosition);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    nearest = station;
                }
            }

            return nearest;
        }

        private List<List<MetroStationCandidate>> BuildMetroCorridorPlans(MetroTrackComponent component, NetManager netManager, PassengerRailMode mode)
        {
            var corridors = new List<List<MetroStationCandidate>>();
            int stationCount = component.Stations.Count;
            if (stationCount < 2)
                return corridors;

            var distances = new float[stationCount, stationCount];
            for (int sourceIndex = 0; sourceIndex < stationCount; sourceIndex++)
            {
                float[] nodeDistances = ComputeMetroTrackDistances(component.Stations[sourceIndex].AnchorNode, netManager, mode);
                for (int targetIndex = 0; targetIndex < stationCount; targetIndex++)
                {
                    ushort targetNode = component.Stations[targetIndex].AnchorNode;
                    distances[sourceIndex, targetIndex] = targetNode < nodeDistances.Length
                        ? nodeDistances[targetNode]
                        : float.PositiveInfinity;
                }
            }

            var inTree = new bool[stationCount];
            var bestDistance = new float[stationCount];
            var parent = new int[stationCount];
            var adjacency = new List<int>[stationCount];
            for (int i = 0; i < stationCount; i++)
            {
                bestDistance[i] = float.PositiveInfinity;
                parent[i] = -1;
                adjacency[i] = new List<int>();
            }

            bestDistance[0] = 0f;
            int treeEdges = 0;
            for (int iteration = 0; iteration < stationCount; iteration++)
            {
                int selected = -1;
                float selectedDistance = float.PositiveInfinity;
                for (int i = 0; i < stationCount; i++)
                {
                    if (!inTree[i] && bestDistance[i] < selectedDistance)
                    {
                        selected = i;
                        selectedDistance = bestDistance[i];
                    }
                }

                if (selected < 0 || float.IsInfinity(selectedDistance))
                    return corridors;

                inTree[selected] = true;
                if (parent[selected] >= 0)
                {
                    adjacency[selected].Add(parent[selected]);
                    adjacency[parent[selected]].Add(selected);
                    treeEdges++;
                }

                for (int candidate = 0; candidate < stationCount; candidate++)
                {
                    float distance = distances[selected, candidate];
                    if (!inTree[candidate] && distance < bestDistance[candidate])
                    {
                        bestDistance[candidate] = distance;
                        parent[candidate] = selected;
                    }
                }
            }

            if (treeEdges != stationCount - 1)
                return corridors;

            var leaves = new List<int>();
            for (int i = 0; i < stationCount; i++)
            {
                if (adjacency[i].Count == 1)
                    leaves.Add(i);
            }

            if (leaves.Count < 2)
                return corridors;

            var unpairedLeaves = new List<int>(leaves);
            var coveredStations = new bool[stationCount];
            while (unpairedLeaves.Count >= 2)
            {
                int bestLeftIndex = -1;
                int bestRightIndex = -1;
                float bestLength = -1f;
                List<int> bestPath = null;

                for (int leftIndex = 0; leftIndex < unpairedLeaves.Count - 1; leftIndex++)
                {
                    for (int rightIndex = leftIndex + 1; rightIndex < unpairedLeaves.Count; rightIndex++)
                    {
                        List<int> path = GetMetroStationTreePath(
                            unpairedLeaves[leftIndex],
                            unpairedLeaves[rightIndex],
                            adjacency);
                        float length = GetMetroStationTreePathLength(path, distances);
                        if (path.Count >= 2 && length > bestLength)
                        {
                            bestLength = length;
                            bestLeftIndex = leftIndex;
                            bestRightIndex = rightIndex;
                            bestPath = path;
                        }
                    }
                }

                if (bestPath == null)
                    return new List<List<MetroStationCandidate>>();

                AddMetroCorridorPlan(corridors, bestPath, component.Stations, coveredStations);
                unpairedLeaves.RemoveAt(bestRightIndex);
                unpairedLeaves.RemoveAt(bestLeftIndex);
            }

            if (unpairedLeaves.Count == 1)
            {
                int remainingLeaf = unpairedLeaves[0];
                List<int> bestPath = null;
                float bestLength = -1f;
                for (int i = 0; i < leaves.Count; i++)
                {
                    if (leaves[i] == remainingLeaf)
                        continue;

                    List<int> path = GetMetroStationTreePath(remainingLeaf, leaves[i], adjacency);
                    float length = GetMetroStationTreePathLength(path, distances);
                    if (path.Count >= 2 && length > bestLength)
                    {
                        bestLength = length;
                        bestPath = path;
                    }
                }

                AddMetroCorridorPlan(corridors, bestPath, component.Stations, coveredStations);
            }

            for (int stationIndex = 0; stationIndex < stationCount; stationIndex++)
            {
                if (coveredStations[stationIndex])
                    continue;

                List<int> bestPath = null;
                float bestLength = -1f;
                for (int leafIndex = 0; leafIndex < leaves.Count; leafIndex++)
                {
                    List<int> path = GetMetroStationTreePath(stationIndex, leaves[leafIndex], adjacency);
                    float length = GetMetroStationTreePathLength(path, distances);
                    if (path.Count >= 2 && length > bestLength)
                    {
                        bestLength = length;
                        bestPath = path;
                    }
                }

                AddMetroCorridorPlan(corridors, bestPath, component.Stations, coveredStations);
            }

            return corridors;
        }

        private List<int> GetMetroStationTreePath(int start, int end, List<int>[] adjacency)
        {
            var path = new List<int>();
            if (start < 0 || end < 0 || start >= adjacency.Length || end >= adjacency.Length)
                return path;

            var parent = new int[adjacency.Length];
            for (int i = 0; i < parent.Length; i++)
                parent[i] = -1;

            var queue = new Queue<int>();
            parent[start] = start;
            queue.Enqueue(start);
            while (queue.Count > 0 && parent[end] < 0)
            {
                int current = queue.Dequeue();
                for (int i = 0; i < adjacency[current].Count; i++)
                {
                    int next = adjacency[current][i];
                    if (parent[next] >= 0)
                        continue;

                    parent[next] = current;
                    queue.Enqueue(next);
                }
            }

            if (parent[end] < 0)
                return path;

            int cursor = end;
            while (cursor != start)
            {
                path.Add(cursor);
                cursor = parent[cursor];
            }
            path.Add(start);
            path.Reverse();
            return path;
        }

        private float GetMetroStationTreePathLength(List<int> path, float[,] distances)
        {
            if (path == null || path.Count < 2)
                return -1f;

            float length = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                float leg = distances[path[i - 1], path[i]];
                if (float.IsInfinity(leg))
                    return -1f;
                length += leg;
            }

            return length;
        }

        private void AddMetroCorridorPlan(
            List<List<MetroStationCandidate>> corridors,
            List<int> path,
            List<MetroStationCandidate> stations,
            bool[] coveredStations)
        {
            if (path == null || path.Count < 2)
                return;

            var corridor = new List<MetroStationCandidate>();
            for (int i = 0; i < path.Count; i++)
            {
                int stationIndex = path[i];
                corridor.Add(stations[stationIndex]);
                coveredStations[stationIndex] = true;
            }

            corridors.Add(corridor);
        }

        private bool IsPassengerRailCorridorAlreadyServed(
            MetroTrackComponent component,
            List<MetroStationCandidate> corridor)
        {
            if (component == null || corridor == null || corridor.Count < 2)
                return false;

            for (int lineIndex = 0; lineIndex < component.ExistingLineStationSets.Count; lineIndex++)
            {
                HashSet<ushort> stationSet = component.ExistingLineStationSets[lineIndex];
                bool allStationsServed = true;
                for (int stationIndex = 0; stationIndex < corridor.Count; stationIndex++)
                {
                    if (!stationSet.Contains(corridor[stationIndex].BuildingId))
                    {
                        allStationsServed = false;
                        break;
                    }
                }

                if (allStationsServed)
                    return true;
            }

            return false;
        }

        private float[] ComputeMetroTrackDistances(ushort startNode, NetManager netManager, PassengerRailMode mode)
        {
            NetNode[] nodes = netManager.m_nodes.m_buffer;
            float[] distances = new float[nodes.Length];
            bool[] queued = new bool[nodes.Length];
            for (int i = 0; i < distances.Length; i++)
                distances[i] = float.PositiveInfinity;

            if (startNode == 0 || startNode >= nodes.Length)
                return distances;

            var queue = new Queue<ushort>();
            distances[startNode] = 0f;
            queued[startNode] = true;
            queue.Enqueue(startNode);

            while (queue.Count > 0)
            {
                ushort current = queue.Dequeue();
                queued[current] = false;
                ref NetNode node = ref nodes[current];

                for (int segmentIndex = 0; segmentIndex < 8; segmentIndex++)
                {
                    ushort segmentId = node.GetSegment(segmentIndex);
                    if (segmentId == 0)
                        continue;

                    ref NetSegment segment = ref netManager.m_segments.m_buffer[segmentId];
                    if (!IsCreatedMetroTrackSegment(ref segment, mode))
                        continue;

                    ushort other = segment.m_startNode == current ? segment.m_endNode : segment.m_startNode;
                    if (other == 0 || other >= nodes.Length || !IsCreatedMetroTrackNode(ref nodes[other], mode))
                        continue;

                    float segmentLength = Mathf.Max(1f, segment.m_averageLength);
                    float proposed = distances[current] + segmentLength;
                    if (proposed >= distances[other])
                        continue;

                    distances[other] = proposed;
                    if (!queued[other])
                    {
                        queued[other] = true;
                        queue.Enqueue(other);
                    }
                }
            }

            return distances;
        }

        private bool TryCreateMetroLineProbe(
            int componentId,
            List<MetroStationCandidate> orderedStations,
            TransportInfo railInfo,
            PassengerRailMode mode,
            ref Randomizer randomizer,
            out MetroLineProbe probe,
            out string failureReason)
        {
            probe = null;
            failureReason = null;
            TransportManager transportManager = TransportManager.instance;
            ushort lineId;
            if (!transportManager.CreateLine(out lineId, ref randomizer, railInfo, false))
            {
                failureReason = "TransportManager.CreateLine returned false";
                return false;
            }

            bool keepProbe = false;
            try
            {
                ref TransportLine line = ref transportManager.m_lines.m_buffer[lineId];
                line.m_building = orderedStations[0].BuildingId;
                line.m_flags |= TransportLine.Flags.Temporary | TransportLine.Flags.Hidden;

                var directionalStops = new List<MetroStationCandidate>();
                for (int i = 0; i < orderedStations.Count; i++)
                    directionalStops.Add(orderedStations[i]);
                for (int i = orderedStations.Count - 2; i >= 1; i--)
                    directionalStops.Add(orderedStations[i]);

                for (int i = 0; i < directionalStops.Count; i++)
                {
                    MetroStationCandidate station = directionalStops[i];
                    if (!TryAddStopToLine(lineId, i, station.StopPosition, station.FixedPlatform, out failureReason))
                    {
                        failureReason = "station stop " + (i + 1) + " was rejected: " + failureReason;
                        return false;
                    }
                }

                if (!TryCloseLine(lineId, directionalStops[0].StopPosition, out failureReason))
                {
                    failureReason = "line could not close: " + failureReason;
                    return false;
                }

                ref TransportLine builtLine = ref transportManager.m_lines.m_buffer[lineId];
                if (!builtLine.Complete)
                {
                    failureReason = "hidden Metro validation line did not become complete";
                    return false;
                }

                try
                {
                    builtLine.UpdatePaths(lineId);
                }
                catch (Exception e)
                {
                    failureReason = "UpdatePaths failed: " + e.Message;
                    return false;
                }

                probe = new MetroLineProbe
                {
                    LineId = lineId,
                    ExpectedStops = directionalStops.Count,
                    StationCount = orderedStations.Count,
                    ComponentId = componentId
                };
                keepProbe = true;
                TransitLogging.Log(
                    "Prepared hidden " + mode.Name + " validation line " + lineId + " for component " + componentId +
                    ": stations=" + orderedStations.Count + ", directionalStops=" + directionalStops.Count + ".");
                return true;
            }
            finally
            {
                if (!keepProbe)
                    SafeReleaseLine(lineId);
            }
        }

        private IEnumerator SettleMetroLineProbes(
            List<MetroLineProbe> probes,
            MetroScanSummary summary,
            DateTime startedAt,
            PassengerRailMode mode)
        {
            if (probes == null)
                probes = new List<MetroLineProbe>();

            try
            {
                for (int pass = 1; pass <= 8 && probes.Count > 0; pass++)
                {
                    if (!IsPassengerRailScanRunning(mode))
                        break;

                    for (int frame = 0; frame < 10; frame++)
                        yield return null;

                    bool finalPass = pass == 8;
                    for (int index = probes.Count - 1; index >= 0; index--)
                    {
                        MetroLineProbe probe = probes[index];
                        try
                        {
                            TransportManager transportManager = TransportManager.instance;
                            if (transportManager == null || probe.LineId == 0 || probe.LineId >= transportManager.m_lines.m_size)
                            {
                                summary.FailedLines++;
                                probes.RemoveAt(index);
                                continue;
                            }

                            string failureReason;
                            GeneratedProbeStatus status = GetMetroProbePathStatus(
                                probe.LineId,
                                probe.ExpectedStops,
                                finalPass,
                                out failureReason);

                            if (status == GeneratedProbeStatus.Pending)
                                continue;

                            if (status == GeneratedProbeStatus.Valid && TryPublishMetroLineProbe(probe, mode, out failureReason))
                            {
                                summary.CreatedLines++;
                                summary.CreatedLineIds.Add(probe.LineId);
                            }
                            else
                            {
                                summary.FailedLines++;
                                SafeReleaseLine(probe.LineId);
                                TransitLogging.Warn(
                                    mode.Name + " validation line " + probe.LineId + " for component " + probe.ComponentId +
                                    " was released without publication: " + failureReason + ".");
                            }

                            probes.RemoveAt(index);
                        }
                        catch (Exception e)
                        {
                            summary.FailedLines++;
                            SafeReleaseLine(probe.LineId);
                            probes.RemoveAt(index);
                            TransitLogging.Error(
                                mode.Name + " validation line " + probe.LineId +
                                " failed during settlement/publication and was released: " + e);
                        }
                    }
                }

                bool scanStillRunning = IsPassengerRailScanRunning(mode);
                for (int i = 0; i < probes.Count; i++)
                {
                    if (scanStillRunning)
                        summary.FailedLines++;
                    SafeReleaseLine(probes[i].LineId);
                    TransitLogging.Warn(
                        mode.Name + " validation line " + probes[i].LineId +
                        (scanStillRunning
                            ? " did not settle before the bounded timeout and was released."
                            : " was released because the " + mode.Name + " scan was cancelled."));
                }
                probes.Clear();

                if (!scanStillRunning)
                    yield break;

                summary.DurationSeconds = (float)(DateTime.UtcNow - startedAt).TotalSeconds;
                summary.Completed = true;
                SetPassengerRailScanSummary(mode, summary);
                SetPassengerRailScanRunning(mode, false);

                RefreshPublicTransportOverviewPanels("post-" + mode.Name + "-publication", true);
                StartCoroutine(RefreshPublicTransportOverviewPanelsDeferred());
                UpdatePassengerRailScanSummary(mode, summary);
                ShowPassengerRailScanResult(mode, summary);
                TransitLogging.Log(
                    mode.Name + " scan completed in " + summary.DurationSeconds.ToString("0.00", CultureInfo.InvariantCulture) +
                    "s: createdLines=" + summary.CreatedLines + ", failedLines=" + summary.FailedLines + ".");
            }
            finally
            {
                for (int i = 0; i < probes.Count; i++)
                    SafeReleaseLine(probes[i].LineId);
                SetPassengerRailScanRunning(mode, false);
            }
        }

        private GeneratedProbeStatus GetMetroProbePathStatus(
            ushort lineId,
            int expectedStops,
            bool finalPass,
            out string failureReason)
        {
            ref TransportLine line = ref TransportManager.instance.m_lines.m_buffer[lineId];
            return GetStrictLinePathStatus(ref line, lineId, expectedStops, finalPass, out failureReason);
        }

        private bool TryPublishMetroLineProbe(MetroLineProbe probe, PassengerRailMode mode, out string failureReason)
        {
            failureReason = null;
            TransportManager transportManager = TransportManager.instance;
            ref TransportLine line = ref transportManager.m_lines.m_buffer[probe.LineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0 || !line.Complete)
            {
                failureReason = "settled line was not complete";
                return false;
            }
            if (line.Info == null || line.Info.m_transportType != mode.TransportType)
            {
                failureReason = "settled line was not a passenger " + mode.Name + " line";
                return false;
            }

            List<Vector3> actualStops = GetExistingLineStops(ref line);
            if (actualStops.Count < probe.ExpectedStops)
            {
                failureReason = "settled line retained only " + actualStops.Count + " of " + probe.ExpectedStops + " directional stops";
                return false;
            }

            ushort publicLineNumber;
            if (!TryAssignGeneratedPublicLineNumberForType(
                transportManager,
                ref line,
                mode.TransportType,
                out publicLineNumber,
                out failureReason))
                return false;

            line.m_flags &= ~(TransportLine.Flags.Temporary |
                              TransportLine.Flags.Hidden |
                              TransportLine.Flags.Invalid |
                              TransportLine.Flags.Selected |
                              TransportLine.Flags.Highlighted);
            line.m_flags |= TransportLine.Flags.Created |
                            TransportLine.Flags.Complete |
                            TransportLine.Flags.CompleteSet;

            PublishMetroStopNodes(probe.LineId, ref line);
            try
            {
                line.CheckCompletionMilestone();
                line.UpdatePaths(probe.LineId);
                line.UpdateMeshData(probe.LineId);
                transportManager.UpdateLine(probe.LineId);
                transportManager.UpdateLinesNow();
            }
            catch (Exception e)
            {
                failureReason = "Metro publication refresh failed: " + e.Message;
                return false;
            }

            TransitLogging.Log(
                "Published generated passenger " + mode.Name + " line id=" + probe.LineId +
                ", publicNumber=" + publicLineNumber +
                ", originStation=" + line.m_building +
                ", stations=" + probe.StationCount +
                ", directionalStops=" + actualStops.Count +
                ", compatibility=vanilla-line-api/no-vehicle-or-setting-overrides.");
            return true;
        }

        private void PublishMetroStopNodes(ushort lineId, ref TransportLine line)
        {
            NetManager netManager = NetManager.instance;
            SimulationManager simulationManager = SimulationManager.instance;
            ushort firstStop = line.m_stops;
            ushort current = firstStop;
            int safety = 0;

            while (current != 0 && current < netManager.m_nodes.m_buffer.Length && safety++ < 512)
            {
                ref NetNode node = ref netManager.m_nodes.m_buffer[current];
                bool changed = false;
                if ((node.m_flags & NetNode.Flags.Temporary) != 0)
                {
                    node.m_flags &= ~NetNode.Flags.Temporary;
                    changed = true;
                }
                if (node.m_transportLine != lineId)
                {
                    node.m_transportLine = lineId;
                    changed = true;
                }

                if (changed)
                {
                    node.m_buildIndex = simulationManager.m_currentBuildIndex++;
                    netManager.UpdateNode(current);
                }

                current = TransportLine.GetNextStop(current);
                if (current == firstStop)
                    break;
            }
        }

        private bool TryAssignGeneratedPublicLineNumberForType(
            TransportManager transportManager,
            ref TransportLine line,
            TransportInfo.TransportType transportType,
            out ushort publicLineNumber,
            out string failureReason)
        {
            publicLineNumber = line.m_lineNumber;
            if (publicLineNumber != 0)
            {
                failureReason = null;
                return true;
            }

            if (TransportManagerLineNumberField == null)
            {
                failureReason = "could not access TransportManager line-number counter";
                return false;
            }

            ushort[] lineNumbers = TransportManagerLineNumberField.GetValue(transportManager) as ushort[];
            int transportIndex = (int)transportType;
            if (lineNumbers == null || transportIndex < 0 || transportIndex >= lineNumbers.Length)
            {
                failureReason = "TransportManager line-number counter was unavailable for " + transportType;
                return false;
            }

            uint next = (uint)lineNumbers[transportIndex] + 1u;
            if (next > ushort.MaxValue)
            {
                failureReason = transportType + " line-number counter overflowed";
                return false;
            }

            publicLineNumber = (ushort)next;
            lineNumbers[transportIndex] = publicLineNumber;
            line.m_lineNumber = publicLineNumber;
            failureReason = null;
            return true;
        }

        private bool IsPassengerRailScanRunning(PassengerRailMode mode)
        {
            return mode.TransportType == TransportInfo.TransportType.Metro
                ? _metroScanRunning
                : _trainScanRunning;
        }

        private void SetPassengerRailScanRunning(PassengerRailMode mode, bool value)
        {
            if (mode.TransportType == TransportInfo.TransportType.Metro)
                _metroScanRunning = value;
            else
                _trainScanRunning = value;
        }

        private void SetPassengerRailScanSummary(PassengerRailMode mode, MetroScanSummary summary)
        {
            if (mode.TransportType == TransportInfo.TransportType.Metro)
                State.LastMetroScanSummary = summary;
            else
                State.LastTrainScanSummary = summary;
        }

        private void UpdatePassengerRailScanStatus(PassengerRailMode mode, string text)
        {
            if (mode.TransportType == TransportInfo.TransportType.Metro)
                AutoPublicTransitUI.UpdateMetroScanStatus(text);
            else
                AutoPublicTransitUI.UpdateTrainScanStatus(text);
        }

        private void UpdatePassengerRailScanSummary(PassengerRailMode mode, MetroScanSummary summary)
        {
            if (mode.TransportType == TransportInfo.TransportType.Metro)
                AutoPublicTransitUI.UpdateMetroScanSummary(summary);
            else
                AutoPublicTransitUI.UpdateTrainScanSummary(summary);
        }

        private void ShowPassengerRailScanResult(PassengerRailMode mode, MetroScanSummary summary)
        {
            if (mode.TransportType == TransportInfo.TransportType.Metro)
                AutoPublicTransitUI.ShowMetroScanResult(summary);
            else
                AutoPublicTransitUI.ShowTrainScanResult(summary);
        }

        private bool IsCreatedMetroTrackNode(ref NetNode node, PassengerRailMode mode)
        {
            return (node.m_flags & NetNode.Flags.Created) != 0 && IsMetroTrackInfo(node.Info, mode);
        }

        private bool IsCreatedMetroTrackSegment(ref NetSegment segment, PassengerRailMode mode)
        {
            if ((segment.m_flags & NetSegment.Flags.Created) == 0 ||
                (segment.m_flags & (NetSegment.Flags.Deleted | NetSegment.Flags.Collapsed)) != 0)
                return false;

            return IsMetroTrackInfo(segment.Info, mode);
        }

        private bool IsMetroTrackInfo(NetInfo info, PassengerRailMode mode)
        {
            if (info == null)
                return false;

            if (mode.TransportType == TransportInfo.TransportType.Metro && info.m_netAI is MetroTrackBaseAI)
                return true;
            if (mode.TransportType == TransportInfo.TransportType.Train && info.m_netAI is TrainTrackBaseAI)
                return true;

            if (info.m_class == null ||
                info.m_class.m_service != ItemClass.Service.PublicTransport ||
                info.m_class.m_subService != mode.SubService ||
                info.m_lanes == null)
                return false;

            for (int laneIndex = 0; laneIndex < info.m_lanes.Length; laneIndex++)
            {
                NetInfo.Lane lane = info.m_lanes[laneIndex];
                if (lane == null)
                    continue;

                if ((lane.m_vehicleType & mode.VehicleType) != 0 &&
                    (lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                    return true;
            }

            return false;
        }
    }
}
