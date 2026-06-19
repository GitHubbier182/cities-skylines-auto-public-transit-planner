using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace AutoPublicTransit
{
    public class TransitRouteBuilder
    {
        private const int MaxStrategicReuse = 4;
        private const int MaxTouristCoverageRoutes = 10;
        private const int MaxCoverageBackfillRoutes = 64;
        private readonly Dictionary<long, float> _planningLinkDistanceCache = new Dictionary<long, float>();

        public int LocationFallbackLinkCount { get; private set; }
        public int RoadGraphRejectedLinkCount { get; private set; }
        public int TouristCoverageRouteCount { get; private set; }
        public int MandatoryTransitHubInsertionCount { get; private set; }
        public int MandatoryTransitHubMissCount { get; private set; }
        public int RouteConnectorInsertionCount { get; private set; }
        public int RouteConnectorConnectedRouteCount { get; private set; }
        public int RouteConnectorRejectedRouteCount { get; private set; }
        public int CoverageBackfillRouteCount { get; private set; }
        public int CoverageBackfillUncoveredNodeCount { get; private set; }
        public int CoverageBackfillConnectorCount { get; private set; }
        public int CoverageBackfillConnectedLineCount { get; private set; }
        public int CoverageBackfillUnconnectedLineCount { get; private set; }
        public int CoverageBackfillStrategicFallbackRouteCount { get; private set; }

        public List<List<Vector3>> BuildRoutes(List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            LocationFallbackLinkCount = 0;
            RoadGraphRejectedLinkCount = 0;
            TouristCoverageRouteCount = 0;
            MandatoryTransitHubInsertionCount = 0;
            MandatoryTransitHubMissCount = 0;
            RouteConnectorInsertionCount = 0;
            RouteConnectorConnectedRouteCount = 0;
            RouteConnectorRejectedRouteCount = 0;
            _planningLinkDistanceCache.Clear();

            var routes = new List<List<Vector3>>();
            if (nodes == null || nodes.Count == 0)
                return routes;

            nodes.Sort((a, b) => GetSeedPriority(b).CompareTo(GetSeedPriority(a)));

            int n = nodes.Count;
            bool[] used = new bool[n];
            int[] routeUseCount = new int[n];

            for (int i = 0; i < n; i++)
            {
                if (!CanUseNode(i, nodes, used, routeUseCount))
                    continue;

                if (!IsStrategicNode(nodes[i]) && TryInsertNodeIntoExistingRoutes(i, nodes, routes, cfg, stopLocator))
                {
                    RecordNodeUse(i, nodes, used, routeUseCount);
                    continue;
                }

                DemandNode seed = nodes[i];
                var route = new List<Vector3>();
                var routeIndexes = new List<int>();
                int targetStops = GetPrimaryCoverageTargetStops(seed, cfg);
                float maxRouteLength = GetPrimaryCoverageMaxRouteLength(seed, cfg);
                AddNodeToRoute(i, nodes, route, routeIndexes, used, routeUseCount);

                while (route.Count < targetStops)
                {
                    int bestIndex = FindBestNextNode(i, route, nodes, used, routeUseCount, cfg, stopLocator);
                    if (bestIndex == -1)
                        break;

                    AddNodeToRoute(bestIndex, nodes, route, routeIndexes, used, routeUseCount);
                }

                TryFillRouteGaps(route, routeIndexes, nodes, used, routeUseCount, cfg, stopLocator, targetStops, cfg.MaxRoadDistance * 0.8f);
                route = OptimizeRoadOrder(route, nodes, cfg, stopLocator);
                route = PruneToFeasiblePlanningLoop(route, nodes, routeIndexes, used, routeUseCount, cfg, stopLocator, maxRouteLength);
                routeIndexes = GetRouteNodeIndexes(route, nodes);
                float routeLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);

                if (route.Count >= cfg.MinStopsPerRoute &&
                    routeLength != float.MaxValue &&
                    routeLength <= maxRouteLength &&
                    HasUsefulRoutePurpose(route, nodes, routeIndexes, cfg))
                {
                    routes.Add(route);
                }
                else
                {
                    ReleaseRouteIndexes(routeIndexes, nodes, used, routeUseCount);
                }
            }

            TryInsertRemainingUsefulStops(nodes, routes, used, routeUseCount, cfg, stopLocator);
            EnsureTouristAnchorCoverageRoutes(nodes, routes, used, routeUseCount, cfg, stopLocator);
            ConnectRouteCandidatesSecondPass(routes, nodes, routeUseCount, cfg, stopLocator);
            bool requireNetworkConnection = ShouldRequireNetworkConnection(routes, nodes);

            for (int i = routes.Count - 1; i >= 0; i--)
            {
                routes[i] = OptimizeRoadOrder(routes[i], nodes, cfg, stopLocator);
                routes[i] = PruneToFeasiblePlanningLoop(routes[i], nodes, null, used, routeUseCount, cfg, stopLocator);
                List<int> routeIndexes = GetRouteNodeIndexes(routes[i], nodes);
                float routeLength = ComputePlanningClosedLength(routes[i], nodes, cfg, stopLocator);
                if (routes[i].Count < cfg.MinStopsPerRoute ||
                    routeLength == float.MaxValue ||
                    routeLength > cfg.MaxLineLengthKm * 1000f ||
                    !HasUsefulRoutePurpose(routes[i], nodes, routeIndexes, cfg) ||
                    (requireNetworkConnection && !IsRouteConnectedToTransitOrAnotherRoute(i, routes[i], routes, nodes, cfg)))
                {
                    if (requireNetworkConnection && !IsRouteConnectedToTransitOrAnotherRoute(i, routes[i], routes, nodes, cfg))
                        RouteConnectorRejectedRouteCount++;

                    routes.RemoveAt(i);
                    continue;
                }
            }

            if (requireNetworkConnection)
                RemoveUnconnectedRoutesAfterValidation(routes, nodes, cfg);

            for (int i = 0; i < routes.Count; i++)
            {
                if (routes[i].Count > 1)
                    routes[i].Add(routes[i][0]);
            }

            return routes;
        }

        public List<List<Vector3>> BuildCoverageBackfillRoutes(
            List<DemandNode> nodes,
            List<ExistingLineSnapshot> publishedLines,
            List<Vector3> transitHubs,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            CoverageBackfillRouteCount = 0;
            CoverageBackfillUncoveredNodeCount = 0;
            CoverageBackfillConnectorCount = 0;
            CoverageBackfillConnectedLineCount = 0;
            CoverageBackfillUnconnectedLineCount = 0;
            CoverageBackfillStrategicFallbackRouteCount = 0;
            _planningLinkDistanceCache.Clear();

            var routes = new List<List<Vector3>>();
            if (nodes == null || nodes.Count == 0)
                return routes;

            bool[] connectedPublishedLines = BuildPublishedLineConnectionMask(publishedLines, transitHubs, cfg);
            CountPublishedLineConnections(connectedPublishedLines, publishedLines);
            HashSet<int> connectorIndexes;
            List<DemandNode> workingNodes = BuildCoverageBackfillWorkingNodes(nodes, publishedLines, connectedPublishedLines, transitHubs, cfg, stopLocator, out connectorIndexes);
            CoverageBackfillConnectorCount = connectorIndexes.Count;

            bool[] covered = BuildPublishedCoverageMask(nodes, publishedLines, connectedPublishedLines, cfg);
            var seedIndexes = new List<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!covered[i])
                    seedIndexes.Add(i);
            }

            CoverageBackfillUncoveredNodeCount = seedIndexes.Count;
            if (seedIndexes.Count == 0)
                return routes;

            seedIndexes.Sort((a, b) => GetCoverageBackfillPriority(nodes[b]).CompareTo(GetCoverageBackfillPriority(nodes[a])));

            bool[] used = new bool[workingNodes.Count];
            int[] routeUseCount = new int[workingNodes.Count];

            for (int i = 0; i < seedIndexes.Count && routes.Count < MaxCoverageBackfillRoutes; i++)
            {
                int seedIndex = seedIndexes[i];
                if (covered[seedIndex])
                    continue;

                if (!CanUseNode(seedIndex, workingNodes, used, routeUseCount))
                    continue;

                List<Vector3> route;
                if (!TryBuildCoverageBackfillRoute(seedIndex, covered, workingNodes, connectorIndexes, used, routeUseCount, cfg, stopLocator, out route))
                    continue;

                routes.Add(route);
                CoverageBackfillRouteCount++;
                MarkNodesCoveredByRoute(route, nodes, covered, cfg);
            }

            return routes;
        }

        private float GetSeedPriority(DemandNode node)
        {
            return GetNodeBenefit(node) + node.Demand;
        }

        private bool CanUseNode(int index, List<DemandNode> nodes, bool[] used, int[] routeUseCount)
        {
            if (IsStrategicNode(nodes[index]))
                return routeUseCount[index] < MaxStrategicReuse;

            return !used[index];
        }

        private bool IsStrategicNode(DemandNode node)
        {
            return DemandNodePurpose.IsStrategic(node);
        }

        private bool IsResidentialNode(DemandNode node)
        {
            return DemandNodePurpose.HasPurpose(node, DemandNodePurpose.Residential);
        }

        private bool IsWorkOrShoppingNode(DemandNode node)
        {
            return DemandNodePurpose.HasAnyPurpose(node, DemandNodePurpose.WorkOrShoppingMask);
        }

        private void AddNodeToRoute(int index, List<DemandNode> nodes, List<Vector3> route, List<int> routeIndexes, bool[] used, int[] routeUseCount)
        {
            route.Add(nodes[index].StopPosition);
            routeIndexes.Add(index);
            routeUseCount[index]++;

            if (!IsStrategicNode(nodes[index]))
                used[index] = true;
        }

        private void RecordNodeUse(int index, List<DemandNode> nodes, bool[] used, int[] routeUseCount)
        {
            routeUseCount[index]++;
            if (!IsStrategicNode(nodes[index]))
                used[index] = true;
        }

        private void ReleaseRouteIndexes(List<int> routeIndexes, List<DemandNode> nodes, bool[] used, int[] routeUseCount)
        {
            if (routeIndexes == null || nodes == null || routeUseCount == null)
                return;

            for (int i = 0; i < routeIndexes.Count; i++)
            {
                int index = routeIndexes[i];
                if (index < 0 || index >= nodes.Count || index >= routeUseCount.Length)
                    continue;

                routeUseCount[index] = Mathf.Max(0, routeUseCount[index] - 1);
                if (used != null && index < used.Length && !IsStrategicNode(nodes[index]))
                    used[index] = false;
            }
        }

        private int FindBestNextNode(
            int seedIndex,
            List<Vector3> route,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            int bestIndex = -1;
            float bestScore = float.MaxValue;
            Vector3 current = route[route.Count - 1];
            int currentIndex = FindNodeIndexForStop(current, nodes);
            DemandNode seed = seedIndex >= 0 && seedIndex < nodes.Count ? nodes[seedIndex] : nodes[currentIndex];
            float localReach = GetPrimaryCoverageLocalReach(seed, cfg);
            bool routeHasStrategicNode = RouteHasStrategicNode(route, nodes);
            bool routeHasTransitHubNode = RouteHasTransitHubNode(route, nodes);

            for (int j = 0; j < nodes.Count; j++)
            {
                if (!CanUseNode(j, nodes, used, routeUseCount))
                    continue;

                if (IsTooCloseToExistingRouteStop(route, nodes[j].StopPosition, cfg))
                    continue;

                float seedDistance = Geometry.DistanceXZ(seed.StopPosition, nodes[j].StopPosition);
                if (seedDistance > localReach && !IsStrategicNode(nodes[j]))
                    continue;

                float dist;
                if (!TryGetPlanningLinkDistance(currentIndex, j, current, nodes[j].StopPosition, nodes, cfg, stopLocator, out dist))
                    continue;

                float projectedLength = EstimateClosedRouteLength(route, nodes[j].StopPosition, nodes, cfg, stopLocator);
                if (projectedLength > cfg.MaxLineLengthKm * 1000f)
                    continue;

                float score = dist + seedDistance * 0.35f - GetNodeBenefit(nodes[j]);
                score += ComputeBacktrackPenalty(route, nodes[j].StopPosition);

                if (!routeHasStrategicNode && IsStrategicNode(nodes[j]))
                    score -= 180f;

                if (!routeHasTransitHubNode && DemandNodePurpose.HasPurpose(nodes[j], DemandNodePurpose.TransitHub))
                    score -= 260f;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestIndex = j;
            }

            return bestIndex;
        }

        private int GetPrimaryCoverageTargetStops(DemandNode seed, AutoPublicTransitConfig cfg)
        {
            int minimumStops = Mathf.Max(4, cfg.MinStopsPerRoute);
            int target = IsStrategicNode(seed) ? minimumStops + 1 : minimumStops;
            return Mathf.Min(cfg.MaxStopsPerRoute, target);
        }

        private float GetPrimaryCoverageLocalReach(DemandNode seed, AutoPublicTransitConfig cfg)
        {
            if (IsStrategicNode(seed))
                return Mathf.Max(cfg.MaxRoadDistance * 1.35f, cfg.MaxWalkingDistance * 3.75f);

            return Mathf.Max(cfg.MaxRoadDistance * 1.2f, cfg.MaxWalkingDistance * 3.25f);
        }

        private float GetPrimaryCoverageMaxRouteLength(DemandNode seed, AutoPublicTransitConfig cfg)
        {
            float routeLength = IsStrategicNode(seed)
                ? Mathf.Max(4200f, cfg.MaxRoadDistance * 7.0f)
                : Mathf.Max(3600f, cfg.MaxRoadDistance * 6.0f);

            return Mathf.Min(cfg.MaxLineLengthKm * 1000f, routeLength);
        }

        private float GetAllowedLinkDistance(DemandNode node, AutoPublicTransitConfig cfg)
        {
            if (IsStrategicNode(node))
                return cfg.MaxRoadDistance * 1.45f;

            return cfg.MaxRoadDistance;
        }

        private float GetNodeBenefit(DemandNode node)
        {
            float benefit = Mathf.Min(node.Demand * 2.5f, 160f);
            if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TransitHub))
                benefit += 240f;

            if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TouristAnchor))
                benefit += 185f;

            if (IsWorkOrShoppingNode(node))
                benefit += 110f;

            if (IsResidentialNode(node))
                benefit += 70f;

            return benefit;
        }

        private bool RouteHasStrategicNode(List<Vector3> route, List<DemandNode> nodes)
        {
            for (int i = 0; i < route.Count; i++)
            {
                for (int j = 0; j < nodes.Count; j++)
                {
                    if (!IsStrategicNode(nodes[j]))
                        continue;

                    if (Geometry.DistanceXZ(route[i], nodes[j].StopPosition) < 8f)
                        return true;
                }
            }

            return false;
        }

        private bool RouteHasTransitHubNode(List<Vector3> route, List<DemandNode> nodes)
        {
            for (int i = 0; i < route.Count; i++)
            {
                for (int j = 0; j < nodes.Count; j++)
                {
                    if (!DemandNodePurpose.HasPurpose(nodes[j], DemandNodePurpose.TransitHub))
                        continue;

                    if (Geometry.DistanceXZ(route[i], nodes[j].StopPosition) < 8f)
                        return true;
                }
            }

            return false;
        }

        private bool HasUsefulRoutePurpose(List<Vector3> route, List<DemandNode> nodes, List<int> routeIndexes, AutoPublicTransitConfig cfg)
        {
            if (routeIndexes == null || routeIndexes.Count == 0)
                return false;

            int strategicStops = 0;
            int normalStops = 0;
            int totalDemand = 0;

            for (int i = 0; i < routeIndexes.Count; i++)
            {
                DemandNode node = nodes[routeIndexes[i]];
                totalDemand += node.Demand;
                if (IsStrategicNode(node))
                    strategicStops++;
                else
                    normalStops++;
            }

            if (strategicStops > 0 && normalStops >= 2)
                return true;

            if (strategicStops >= 2)
                return true;

            return totalDemand >= cfg.DemandThreshold * cfg.MinStopsPerRoute;
        }

        private void ConnectRouteCandidatesSecondPass(
            List<List<Vector3>> routes,
            List<DemandNode> nodes,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (routes == null || nodes == null || routes.Count == 0 || stopLocator == null)
                return;

            InsertMandatoryTransitHubStopsIntoRoutes(routes, nodes, routeUseCount, cfg, stopLocator);

            if (routes.Count < 2 && !HasAnyTransitHubNode(nodes))
                return;

            for (int routeIndex = 0; routeIndex < routes.Count; routeIndex++)
            {
                List<Vector3> route = NormalizeOpenRoute(routes[routeIndex]);
                if (route.Count < cfg.MinStopsPerRoute || route.Count >= cfg.MaxStopsPerRoute)
                    continue;

                if (IsRouteConnectedToTransitOrAnotherRoute(routeIndex, route, routes, nodes, cfg))
                    continue;

                Vector3 connectorStop;
                int connectorNodeIndex;
                int insertAt;
                if (!TryFindBestRouteConnectorStop(routeIndex, route, routes, nodes, cfg, stopLocator, out connectorStop, out connectorNodeIndex, out insertAt))
                    continue;

                if (insertAt >= route.Count)
                    route.Add(connectorStop);
                else
                    route.Insert(insertAt, connectorStop);

                if (connectorNodeIndex >= 0 && connectorNodeIndex < routeUseCount.Length)
                    routeUseCount[connectorNodeIndex]++;

                routes[routeIndex] = route;
                RouteConnectorInsertionCount++;
                RouteConnectorConnectedRouteCount++;
            }
        }

        private void InsertMandatoryTransitHubStopsIntoRoutes(
            List<List<Vector3>> routes,
            List<DemandNode> nodes,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (routes == null || nodes == null || routeUseCount == null || stopLocator == null)
                return;

            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (!DemandNodePurpose.HasPurpose(nodes[nodeIndex], DemandNodePurpose.TransitHub))
                    continue;

                if (IsDemandNodeCoveredByRoutes(nodes[nodeIndex], routes, GetMandatoryTransitHubStopDistance(cfg)))
                    continue;

                int routeIndex;
                int insertAt;
                if (!TryFindBestMandatoryTransitHubInsertion(nodeIndex, routes, nodes, cfg, stopLocator, out routeIndex, out insertAt))
                {
                    MandatoryTransitHubMissCount++;
                    continue;
                }

                List<Vector3> route = NormalizeOpenRoute(routes[routeIndex]);
                if (insertAt >= route.Count)
                    route.Add(nodes[nodeIndex].StopPosition);
                else
                    route.Insert(insertAt, nodes[nodeIndex].StopPosition);

                routes[routeIndex] = route;
                routeUseCount[nodeIndex]++;
                MandatoryTransitHubInsertionCount++;
            }
        }

        private bool TryFindBestMandatoryTransitHubInsertion(
            int hubNodeIndex,
            List<List<Vector3>> routes,
            List<DemandNode> nodes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out int bestRouteIndex,
            out int bestInsertAt)
        {
            bestRouteIndex = -1;
            bestInsertAt = -1;

            DemandNode hubNode = nodes[hubNodeIndex];
            float bestScore = float.MaxValue;
            for (int routeIndex = 0; routeIndex < routes.Count; routeIndex++)
            {
                List<Vector3> route = NormalizeOpenRoute(routes[routeIndex]);
                if (route.Count < 2 || route.Count >= cfg.MaxStopsPerRoute)
                    continue;

                if (IsDemandNodeCoveredByRoute(hubNode, route, GetMandatoryTransitHubStopDistance(cfg)))
                    continue;

                float currentLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
                if (currentLength == float.MaxValue)
                    continue;

                for (int i = 0; i < route.Count; i++)
                {
                    Vector3 from = route[i];
                    Vector3 to = route[(i + 1) % route.Count];
                    float fromToHub;
                    float hubToNext;
                    if (!TryGetPlanningLinkDistance(from, hubNode.StopPosition, nodes, cfg, stopLocator, out fromToHub))
                        continue;
                    if (!TryGetPlanningLinkDistance(hubNode.StopPosition, to, nodes, cfg, stopLocator, out hubToNext))
                        continue;

                    float direct;
                    if (!TryGetPlanningLinkDistance(from, to, nodes, cfg, stopLocator, out direct))
                        direct = Geometry.DistanceXZ(from, to);

                    float added = fromToHub + hubToNext - direct;
                    if (added < -1f)
                        added = 0f;

                    if (currentLength + added > cfg.MaxLineLengthKm * 1000f)
                        continue;

                    float score = added + ComputeInsertionTurnPenalty(route, i, hubNode.StopPosition) - GetNodeBenefit(hubNode);
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    bestRouteIndex = routeIndex;
                    bestInsertAt = i + 1;
                }
            }

            return bestRouteIndex >= 0 && bestInsertAt >= 0;
        }

        private bool IsDemandNodeCoveredByRoutes(DemandNode node, List<List<Vector3>> routes, float maxDistance)
        {
            if (routes == null)
                return false;

            for (int i = 0; i < routes.Count; i++)
            {
                if (IsDemandNodeCoveredByRoute(node, routes[i], maxDistance))
                    return true;
            }

            return false;
        }

        private bool IsDemandNodeCoveredByRoute(DemandNode node, List<Vector3> route, float maxDistance)
        {
            if (route == null || route.Count == 0)
                return false;

            for (int i = 0; i < route.Count; i++)
            {
                if (Geometry.DistanceXZ(route[i], node.StopPosition) <= maxDistance)
                    return true;
            }

            return false;
        }

        private float GetMandatoryTransitHubStopDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(75f, Mathf.Min(120f, cfg.MaxWalkingDistance * 0.45f));
        }

        private bool ShouldRequireNetworkConnection(List<List<Vector3>> routes, List<DemandNode> nodes)
        {
            return (routes != null && routes.Count > 1) || HasAnyTransitHubNode(nodes);
        }

        private bool HasAnyTransitHubNode(List<DemandNode> nodes)
        {
            if (nodes == null)
                return false;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TransitHub))
                    return true;
            }

            return false;
        }

        private void RemoveUnconnectedRoutesAfterValidation(List<List<Vector3>> routes, List<DemandNode> nodes, AutoPublicTransitConfig cfg)
        {
            if (routes == null || routes.Count == 0)
                return;

            bool removed = true;
            int safety = 0;
            while (removed && safety < 8)
            {
                removed = false;
                safety++;

                for (int i = routes.Count - 1; i >= 0; i--)
                {
                    if (IsRouteConnectedToTransitOrAnotherRoute(i, routes[i], routes, nodes, cfg))
                        continue;

                    routes.RemoveAt(i);
                    RouteConnectorRejectedRouteCount++;
                    removed = true;
                }
            }
        }

        private bool IsRouteConnectedToTransitOrAnotherRoute(
            int routeIndex,
            List<Vector3> route,
            List<List<Vector3>> routes,
            List<DemandNode> nodes,
            AutoPublicTransitConfig cfg)
        {
            if (RouteHasTransitHubNode(route, nodes))
                return true;

            float transferDistance = GetRouteConnectorTransferDistance(cfg);
            for (int otherRouteIndex = 0; otherRouteIndex < routes.Count; otherRouteIndex++)
            {
                if (otherRouteIndex == routeIndex)
                    continue;

                List<Vector3> otherRoute = routes[otherRouteIndex];
                if (otherRoute == null || otherRoute.Count == 0)
                    continue;

                for (int i = 0; i < route.Count; i++)
                {
                    for (int j = 0; j < otherRoute.Count; j++)
                    {
                        if (Geometry.DistanceXZ(route[i], otherRoute[j]) <= transferDistance)
                            return true;
                    }
                }
            }

            return false;
        }

        private bool TryFindBestRouteConnectorStop(
            int routeIndex,
            List<Vector3> route,
            List<List<Vector3>> routes,
            List<DemandNode> nodes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out Vector3 connectorStop,
            out int connectorNodeIndex,
            out int insertAt)
        {
            connectorStop = Vector3.zero;
            connectorNodeIndex = -1;
            insertAt = -1;

            float bestScore = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TransitHub))
                    continue;

                ConsiderRouteConnectorCandidate(route, nodes[i].StopPosition, i, 900f, nodes, cfg, stopLocator, ref bestScore, ref connectorStop, ref connectorNodeIndex, ref insertAt);
            }

            for (int otherRouteIndex = 0; otherRouteIndex < routes.Count; otherRouteIndex++)
            {
                if (otherRouteIndex == routeIndex)
                    continue;

                List<Vector3> otherRoute = NormalizeOpenRoute(routes[otherRouteIndex]);
                if (otherRoute.Count == 0)
                    continue;

                float bonus = RouteHasTransitHubNode(otherRoute, nodes) ? 520f : 260f;
                for (int stopIndex = 0; stopIndex < otherRoute.Count; stopIndex++)
                {
                    int nodeIndex = FindNodeIndexForStop(otherRoute[stopIndex], nodes);
                    ConsiderRouteConnectorCandidate(route, otherRoute[stopIndex], nodeIndex, bonus, nodes, cfg, stopLocator, ref bestScore, ref connectorStop, ref connectorNodeIndex, ref insertAt);
                }
            }

            return insertAt >= 0;
        }

        private void ConsiderRouteConnectorCandidate(
            List<Vector3> route,
            Vector3 candidateStop,
            int candidateNodeIndex,
            float connectorBonus,
            List<DemandNode> nodes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            ref float bestScore,
            ref Vector3 bestStop,
            ref int bestNodeIndex,
            ref int bestInsertAt)
        {
            if (route == null || route.Count < 2 || route.Count >= cfg.MaxStopsPerRoute)
                return;

            if (IsTooCloseToExistingConnectorStop(route, candidateStop, cfg))
                return;

            float currentLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
            if (currentLength == float.MaxValue)
                return;

            float bestCandidateScore = float.MaxValue;
            int bestCandidateInsertAt = -1;

            for (int i = 0; i < route.Count; i++)
            {
                Vector3 from = route[i];
                Vector3 to = route[(i + 1) % route.Count];
                float fromToCandidate;
                float candidateToNext;
                if (!TryGetPlanningLinkDistance(from, candidateStop, nodes, cfg, stopLocator, out fromToCandidate))
                    continue;
                if (!TryGetPlanningLinkDistance(candidateStop, to, nodes, cfg, stopLocator, out candidateToNext))
                    continue;

                float direct;
                if (!TryGetPlanningLinkDistance(from, to, nodes, cfg, stopLocator, out direct))
                    direct = Geometry.DistanceXZ(from, to);

                float added = fromToCandidate + candidateToNext - direct;
                if (added < -1f)
                    added = 0f;

                if (currentLength + added > cfg.MaxLineLengthKm * 1000f)
                    continue;

                float score = added;
                score += ComputeInsertionTurnPenalty(route, i, candidateStop);
                score -= connectorBonus;

                if (candidateNodeIndex >= 0)
                    score -= GetNodeBenefit(nodes[candidateNodeIndex]) * 0.25f;

                if (score >= bestCandidateScore)
                    continue;

                bestCandidateScore = score;
                bestCandidateInsertAt = i + 1;
            }

            if (bestCandidateInsertAt < 0 || bestCandidateScore >= bestScore)
                return;

            bestScore = bestCandidateScore;
            bestStop = candidateStop;
            bestNodeIndex = candidateNodeIndex;
            bestInsertAt = bestCandidateInsertAt;
        }

        private float GetRouteConnectorTransferDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(45f, Mathf.Min(cfg.MaxWalkingDistance * 0.25f, cfg.GridCellSize * 0.35f));
        }

        private bool IsTooCloseToExistingConnectorStop(List<Vector3> route, Vector3 candidateStop, AutoPublicTransitConfig cfg)
        {
            float minSpacing = Mathf.Max(35f, Mathf.Min(cfg.MaxWalkingDistance * 0.18f, cfg.GridCellSize * 0.3f));
            for (int i = 0; i < route.Count; i++)
            {
                if (Geometry.DistanceXZ(route[i], candidateStop) < minSpacing)
                    return true;
            }

            return false;
        }

        private bool[] BuildPublishedLineConnectionMask(
            List<ExistingLineSnapshot> publishedLines,
            List<Vector3> transitHubs,
            AutoPublicTransitConfig cfg)
        {
            if (publishedLines == null || publishedLines.Count == 0)
                return new bool[0];

            bool requireConnection = publishedLines.Count > 1 || (transitHubs != null && transitHubs.Count > 0);
            var connected = new bool[publishedLines.Count];
            if (!requireConnection)
            {
                for (int i = 0; i < connected.Length; i++)
                    connected[i] = true;

                return connected;
            }

            for (int i = 0; i < publishedLines.Count; i++)
            {
                ExistingLineSnapshot line = publishedLines[i];
                if (line == null || line.Stops == null || line.Stops.Count == 0)
                    continue;

                if (DoesStopListTransferToPositions(line.Stops, transitHubs, GetPublishedHubTransferDistance(cfg)))
                {
                    connected[i] = true;
                    continue;
                }

                for (int otherIndex = 0; otherIndex < publishedLines.Count; otherIndex++)
                {
                    if (otherIndex == i)
                        continue;

                    ExistingLineSnapshot other = publishedLines[otherIndex];
                    if (other == null || other.Stops == null || other.Stops.Count == 0)
                        continue;

                    if (DoStopListsTransfer(line.Stops, other.Stops, GetPublishedBusTransferDistance(cfg)))
                    {
                        connected[i] = true;
                        break;
                    }
                }
            }

            return connected;
        }

        private void CountPublishedLineConnections(bool[] connectedPublishedLines, List<ExistingLineSnapshot> publishedLines)
        {
            CoverageBackfillConnectedLineCount = 0;
            CoverageBackfillUnconnectedLineCount = 0;

            int lineCount = publishedLines != null ? publishedLines.Count : 0;
            for (int i = 0; i < lineCount; i++)
            {
                bool connected = connectedPublishedLines == null ||
                    i >= connectedPublishedLines.Length ||
                    connectedPublishedLines[i];

                if (connected)
                    CoverageBackfillConnectedLineCount++;
                else
                    CoverageBackfillUnconnectedLineCount++;
            }
        }

        private bool DoesStopListTransferToPositions(List<Vector3> stops, List<Vector3> targets, float maxDistance)
        {
            if (stops == null || targets == null || stops.Count == 0 || targets.Count == 0)
                return false;

            float maxSqr = maxDistance * maxDistance;
            for (int i = 0; i < stops.Count; i++)
            {
                for (int j = 0; j < targets.Count; j++)
                {
                    float dx = stops[i].x - targets[j].x;
                    float dz = stops[i].z - targets[j].z;
                    if (dx * dx + dz * dz <= maxSqr)
                        return true;
                }
            }

            return false;
        }

        private bool DoStopListsTransfer(List<Vector3> a, List<Vector3> b, float maxDistance)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0)
                return false;

            float maxSqr = maxDistance * maxDistance;
            for (int i = 0; i < a.Count; i++)
            {
                for (int j = 0; j < b.Count; j++)
                {
                    float dx = a[i].x - b[j].x;
                    float dz = a[i].z - b[j].z;
                    if (dx * dx + dz * dz <= maxSqr)
                        return true;
                }
            }

            return false;
        }

        private float GetPublishedHubTransferDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(130f, cfg.MaxWalkingDistance * 0.75f);
        }

        private float GetPublishedBusTransferDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(70f, Mathf.Min(cfg.MaxWalkingDistance * 0.45f, cfg.GridCellSize * 0.55f));
        }

        private List<DemandNode> BuildCoverageBackfillWorkingNodes(
            List<DemandNode> nodes,
            List<ExistingLineSnapshot> publishedLines,
            bool[] connectedPublishedLines,
            List<Vector3> transitHubs,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out HashSet<int> connectorIndexes)
        {
            var workingNodes = new List<DemandNode>();
            connectorIndexes = new HashSet<int>();

            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    workingNodes.Add(nodes[i]);
                    if (DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TransitHub))
                        connectorIndexes.Add(i);
                }
            }

            AddTransitHubConnectorNodes(workingNodes, connectorIndexes, transitHubs, cfg, stopLocator);
            AddPublishedLineConnectorNodes(workingNodes, connectorIndexes, publishedLines, connectedPublishedLines, cfg, stopLocator);
            return workingNodes;
        }

        private void AddTransitHubConnectorNodes(
            List<DemandNode> workingNodes,
            HashSet<int> connectorIndexes,
            List<Vector3> transitHubs,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (workingNodes == null || connectorIndexes == null || transitHubs == null || stopLocator == null)
                return;

            float searchDistance = Mathf.Max(cfg.MaxRoadDistance * 1.5f, Mathf.Max(cfg.MaxWalkingDistance * 4f, 900f));
            for (int i = 0; i < transitHubs.Count; i++)
            {
                CachedStopMatch match;
                if (!stopLocator.TryGetNearestBusStopMatch(transitHubs[i], searchDistance, out match))
                    continue;

                AddCoverageConnectorNode(workingNodes, connectorIndexes, transitHubs[i], match, Mathf.Max(45, cfg.DemandThreshold * 8), cfg, 55f);
            }
        }

        private void AddPublishedLineConnectorNodes(
            List<DemandNode> workingNodes,
            HashSet<int> connectorIndexes,
            List<ExistingLineSnapshot> publishedLines,
            bool[] connectedPublishedLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (workingNodes == null || connectorIndexes == null || publishedLines == null || stopLocator == null)
                return;

            int added = 0;
            int maxConnectors = 96;
            float snapDistance = Mathf.Max(96f, cfg.MaxWalkingDistance * 0.5f);
            for (int lineIndex = 0; lineIndex < publishedLines.Count && added < maxConnectors; lineIndex++)
            {
                if (connectedPublishedLines != null &&
                    lineIndex < connectedPublishedLines.Length &&
                    !connectedPublishedLines[lineIndex])
                {
                    continue;
                }

                ExistingLineSnapshot line = publishedLines[lineIndex];
                if (line.Stops == null || line.Stops.Count == 0)
                    continue;

                int stride = Mathf.Max(1, line.Stops.Count / 5);
                for (int stopIndex = 0; stopIndex < line.Stops.Count && added < maxConnectors; stopIndex += stride)
                {
                    CachedStopMatch match;
                    if (!stopLocator.TryGetNearestBusStopMatch(line.Stops[stopIndex], snapDistance, out match))
                        continue;

                    if (AddCoverageConnectorNode(workingNodes, connectorIndexes, line.Stops[stopIndex], match, Mathf.Max(35, cfg.DemandThreshold * 7), cfg, 140f))
                        added++;
                }
            }
        }

        private bool AddCoverageConnectorNode(
            List<DemandNode> workingNodes,
            HashSet<int> connectorIndexes,
            Vector3 centroid,
            CachedStopMatch match,
            int demand,
            AutoPublicTransitConfig cfg,
            float mergeDistance)
        {
            int existing = FindNodeIndexForStopWithin(match.StopPosition, workingNodes, mergeDistance);
            if (existing >= 0)
            {
                connectorIndexes.Add(existing);
                return false;
            }

            float cellSize = Mathf.Max(1f, cfg.GridCellSize);
            connectorIndexes.Add(workingNodes.Count);
            workingNodes.Add(new DemandNode
            {
                Centroid = centroid,
                StopPosition = match.StopPosition,
                StopSegmentId = match.SegmentId,
                Demand = demand,
                CellX = Mathf.FloorToInt(match.StopPosition.x / cellSize),
                CellZ = Mathf.FloorToInt(match.StopPosition.z / cellSize),
                Purpose = DemandNodePurpose.TransitHub,
                PurposeMask = DemandNodePurpose.TransitHubMask
            });

            return true;
        }

        private int FindNodeIndexForStopWithin(Vector3 stop, List<DemandNode> nodes, float maxDistance)
        {
            if (nodes == null)
                return -1;

            float maxSqr = maxDistance * maxDistance;
            int bestIndex = -1;
            float bestSqr = maxSqr;
            for (int i = 0; i < nodes.Count; i++)
            {
                float dx = nodes[i].StopPosition.x - stop.x;
                float dz = nodes[i].StopPosition.z - stop.z;
                float sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                bestIndex = i;
            }

            return bestIndex;
        }

        private bool[] BuildPublishedCoverageMask(
            List<DemandNode> nodes,
            List<ExistingLineSnapshot> publishedLines,
            bool[] connectedPublishedLines,
            AutoPublicTransitConfig cfg)
        {
            bool[] covered = new bool[nodes.Count];
            if (publishedLines == null || publishedLines.Count == 0)
                return covered;

            for (int i = 0; i < nodes.Count; i++)
            {
                for (int lineIndex = 0; lineIndex < publishedLines.Count; lineIndex++)
                {
                    if (connectedPublishedLines != null &&
                        lineIndex < connectedPublishedLines.Length &&
                        !connectedPublishedLines[lineIndex])
                    {
                        continue;
                    }

                    ExistingLineSnapshot line = publishedLines[lineIndex];
                    if (line.Stops == null || line.Stops.Count == 0)
                        continue;

                    if (DoesStopListCoverNode(line.Stops, nodes[i], GetPublishedCoverageDistance(nodes[i], cfg)))
                    {
                        covered[i] = true;
                        break;
                    }
                }
            }

            return covered;
        }

        private float GetPublishedCoverageDistance(DemandNode node, AutoPublicTransitConfig cfg)
        {
            if (IsTouristNode(node))
                return Mathf.Max(95f, cfg.MaxWalkingDistance * 0.65f);

            if (IsStrategicNode(node))
                return Mathf.Max(110f, cfg.MaxWalkingDistance * 0.75f);

            return Mathf.Max(130f, cfg.MaxWalkingDistance * 0.9f);
        }

        private float GetCoverageBackfillPriority(DemandNode node)
        {
            float priority = node.Demand;
            if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TransitHub))
                priority += 640f;

            if (IsTouristNode(node))
                priority += 500f;

            if (IsWorkOrShoppingNode(node))
                priority += 420f;

            if (IsResidentialNode(node))
                priority += 360f;

            if (IsStrategicNode(node))
                priority += 260f;

            return priority;
        }

        private bool TryBuildCoverageBackfillRoute(
            int seedIndex,
            bool[] covered,
            List<DemandNode> nodes,
            HashSet<int> connectorIndexes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out List<Vector3> route)
        {
            route = new List<Vector3>();
            var routeIndexes = new List<int>();
            int minimumStops = Mathf.Max(4, cfg.MinStopsPerRoute);
            bool seedIsConnector = IsNetworkConnectorIndex(seedIndex, nodes, connectorIndexes);
            int localTargetStops = seedIsConnector ? minimumStops : Mathf.Max(1, minimumStops - 1);
            int targetStops = Mathf.Min(cfg.MaxStopsPerRoute, localTargetStops);
            float maxBackfillLength = GetCoverageBackfillMaxRouteLength(cfg);

            AddNodeToRoute(seedIndex, nodes, route, routeIndexes, used, routeUseCount);

            int safety = 0;
            while (route.Count < targetStops && safety < cfg.MaxStopsPerRoute * 4)
            {
                safety++;
                int bestIndex = FindBestCoverageBackfillNode(seedIndex, route, covered, nodes, connectorIndexes, used, routeUseCount, cfg, stopLocator);
                if (bestIndex == -1)
                    break;

                AddNodeToRoute(bestIndex, nodes, route, routeIndexes, used, routeUseCount);
            }

            TryFillRouteGaps(route, routeIndexes, nodes, used, routeUseCount, cfg, stopLocator, targetStops, cfg.MaxRoadDistance * 0.75f, false);
            if (!EnsureCoverageBackfillRouteConnector(route, routeIndexes, nodes, connectorIndexes, routeUseCount, cfg, stopLocator, maxBackfillLength))
            {
                ReleaseRouteIndexes(routeIndexes, nodes, used, routeUseCount);
                if (TryBuildMandatoryStrategicFallbackRoute(seedIndex, nodes, connectorIndexes, routeUseCount, cfg, stopLocator, out route))
                    return true;

                return false;
            }

            route = PruneToFeasiblePlanningLoop(route, nodes, routeIndexes, used, routeUseCount, cfg, stopLocator, maxBackfillLength, false);
            routeIndexes = GetRouteNodeIndexes(route, nodes);

            float routeLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
            if (route.Count < minimumStops ||
                routeLength == float.MaxValue ||
                routeLength > maxBackfillLength ||
                !DoesStopListCoverNode(route, nodes[seedIndex], GetPublishedCoverageDistance(nodes[seedIndex], cfg)) ||
                !HasRequiredCoverageBackfillConnector(routeIndexes, seedIndex, nodes, connectorIndexes) ||
                !HasUsefulRoutePurpose(route, nodes, routeIndexes, cfg))
            {
                ReleaseRouteIndexes(routeIndexes, nodes, used, routeUseCount);
                if (TryBuildMandatoryStrategicFallbackRoute(seedIndex, nodes, connectorIndexes, routeUseCount, cfg, stopLocator, out route))
                    return true;

                return false;
            }

            return true;
        }

        private bool TryBuildMandatoryStrategicFallbackRoute(
            int seedIndex,
            List<DemandNode> nodes,
            HashSet<int> connectorIndexes,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out List<Vector3> route)
        {
            route = new List<Vector3>();
            if (nodes == null || seedIndex < 0 || seedIndex >= nodes.Count || connectorIndexes == null || stopLocator == null)
                return false;

            DemandNode seed = nodes[seedIndex];
            if (!IsStrategicNode(seed))
                return false;

            int connectorIndex;
            if (!TryFindMandatoryStrategicFallbackConnector(seedIndex, nodes, connectorIndexes, cfg, stopLocator, out connectorIndex))
                return false;

            var routeIndexes = new List<int>();
            if (!AddStrategicFallbackNodeStop(seedIndex, nodes, route, routeIndexes, routeUseCount))
                return false;

            if (!AddStrategicFallbackNodeStop(connectorIndex, nodes, route, routeIndexes, routeUseCount))
            {
                ReleaseRouteIndexes(routeIndexes, nodes, null, routeUseCount);
                route = new List<Vector3>();
                return false;
            }

            AddNearbyFallbackRoadStops(seed.StopPosition, route, cfg, stopLocator);
            AddNearbyFallbackRoadStops(nodes[connectorIndex].StopPosition, route, cfg, stopLocator);

            int minimumStops = Mathf.Max(4, cfg.MinStopsPerRoute);
            if (route.Count < minimumStops ||
                !DoesStopListCoverNode(route, seed, GetPublishedCoverageDistance(seed, cfg)) ||
                !HasDistinctNetworkConnector(routeIndexes, seedIndex, nodes, connectorIndexes) ||
                !HasUsefulRoutePurpose(route, nodes, routeIndexes, cfg))
            {
                ReleaseRouteIndexes(routeIndexes, nodes, null, routeUseCount);
                route = new List<Vector3>();
                return false;
            }

            CoverageBackfillStrategicFallbackRouteCount++;
            return true;
        }

        private bool TryFindMandatoryStrategicFallbackConnector(
            int seedIndex,
            List<DemandNode> nodes,
            HashSet<int> connectorIndexes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out int bestConnectorIndex)
        {
            bestConnectorIndex = -1;
            float bestScore = float.MaxValue;
            DemandNode seed = nodes[seedIndex];
            float connectorReach = Mathf.Max(
                GetCoverageBackfillConnectorReach(seed, cfg),
                Mathf.Max(cfg.MaxRoadDistance * 3.2f, cfg.MaxWalkingDistance * 7.5f));

            foreach (int connectorIndex in connectorIndexes)
            {
                if (connectorIndex < 0 || connectorIndex >= nodes.Count || connectorIndex == seedIndex)
                    continue;

                float direct = Geometry.DistanceXZ(seed.StopPosition, nodes[connectorIndex].StopPosition);
                if (direct > connectorReach)
                    continue;

                float linkDistance;
                if (!TryGetPlanningLinkDistance(seedIndex, connectorIndex, seed.StopPosition, nodes[connectorIndex].StopPosition, nodes, cfg, stopLocator, out linkDistance))
                    continue;

                float score = linkDistance + direct * 0.15f - GetNodeBenefit(nodes[connectorIndex]) * 0.25f;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestConnectorIndex = connectorIndex;
            }

            return bestConnectorIndex >= 0;
        }

        private bool AddStrategicFallbackNodeStop(
            int nodeIndex,
            List<DemandNode> nodes,
            List<Vector3> route,
            List<int> routeIndexes,
            int[] routeUseCount)
        {
            if (nodeIndex < 0 || nodeIndex >= nodes.Count)
                return false;

            if (!AddDistinctFallbackStop(route, nodes[nodeIndex].StopPosition, 24f))
                return false;

            routeIndexes.Add(nodeIndex);
            if (routeUseCount != null && nodeIndex < routeUseCount.Length)
                routeUseCount[nodeIndex]++;

            return true;
        }

        private void AddNearbyFallbackRoadStops(
            Vector3 anchor,
            List<Vector3> route,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            int minimumStops = Mathf.Max(4, cfg.MinStopsPerRoute);
            if (route == null || route.Count >= minimumStops)
                return;

            float searchDistance = Mathf.Max(320f, Mathf.Max(cfg.MaxWalkingDistance * 1.7f, cfg.MaxRoadDistance * 0.75f));
            List<CachedStopMatch> nearbyStops = stopLocator.GetNearbyBusStopMatches(anchor, searchDistance, minimumStops + 3, 70f);
            for (int i = 0; i < nearbyStops.Count && route.Count < minimumStops; i++)
            {
                AddDistinctFallbackStop(route, nearbyStops[i].StopPosition, 55f);
            }
        }

        private bool AddDistinctFallbackStop(List<Vector3> route, Vector3 stop, float minDistance)
        {
            if (route == null)
                return false;

            float minSqr = minDistance * minDistance;
            for (int i = 0; i < route.Count; i++)
            {
                float dx = route[i].x - stop.x;
                float dz = route[i].z - stop.z;
                if (dx * dx + dz * dz < minSqr)
                    return false;
            }

            route.Add(stop);
            return true;
        }

        private bool HasDistinctNetworkConnector(List<int> routeIndexes, int seedIndex, List<DemandNode> nodes, HashSet<int> connectorIndexes)
        {
            if (routeIndexes == null || nodes == null)
                return false;

            for (int i = 0; i < routeIndexes.Count; i++)
            {
                int routeIndex = routeIndexes[i];
                if (routeIndex == seedIndex)
                    continue;

                if (IsNetworkConnectorIndex(routeIndex, nodes, connectorIndexes))
                    return true;
            }

            return false;
        }

        private int FindBestCoverageBackfillNode(
            int seedIndex,
            List<Vector3> route,
            bool[] covered,
            List<DemandNode> nodes,
            HashSet<int> connectorIndexes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            int bestIndex = -1;
            float bestScore = float.MaxValue;
            DemandNode seed = nodes[seedIndex];
            Vector3 current = route[route.Count - 1];
            int currentIndex = FindNodeIndexForStop(current, nodes);
            int minimumStops = Mathf.Max(4, cfg.MinStopsPerRoute);
            float localReach = GetCoverageBackfillLocalReach(seed, cfg);
            float maxBackfillLength = GetCoverageBackfillMaxRouteLength(cfg);

            for (int i = 0; i < nodes.Count; i++)
            {
                if (!CanUseNode(i, nodes, used, routeUseCount))
                    continue;

                if (IsTooCloseToExistingRouteStop(route, nodes[i].StopPosition, cfg))
                    continue;

                float seedDistance = Geometry.DistanceXZ(seed.StopPosition, nodes[i].StopPosition);
                bool isConnector = IsNetworkConnectorIndex(i, nodes, connectorIndexes);
                if (isConnector)
                    continue;

                if (seedDistance > localReach)
                    continue;

                float linkDistance;
                if (!TryGetPlanningLinkDistance(currentIndex, i, current, nodes[i].StopPosition, nodes, cfg, stopLocator, out linkDistance))
                    continue;

                float projectedLength = EstimateClosedRouteLength(route, nodes[i].StopPosition, nodes, cfg, stopLocator);
                if (projectedLength > maxBackfillLength)
                    continue;

                bool isCovered = covered != null && i < covered.Length && covered[i];
                float score = linkDistance + seedDistance * 0.3f - GetNodeBenefit(nodes[i]) * 0.55f;

                if (!isCovered)
                    score -= 420f;
                else if (route.Count >= minimumStops)
                    score += 520f;

                if (IsTouristNode(nodes[i]))
                    score -= 180f;
                else if (IsStrategicNode(nodes[i]))
                    score -= 90f;

                score += ComputeBacktrackPenalty(route, nodes[i].StopPosition);

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestIndex = i;
            }

            return bestIndex;
        }

        private bool EnsureCoverageBackfillRouteConnector(
            List<Vector3> route,
            List<int> routeIndexes,
            List<DemandNode> nodes,
            HashSet<int> connectorIndexes,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            float maxRouteLength)
        {
            if (route == null || nodes == null || connectorIndexes == null || route.Count < 2 || route.Count >= cfg.MaxStopsPerRoute)
                return false;

            int seedNodeIndex = routeIndexes != null && routeIndexes.Count > 0 && routeIndexes[0] >= 0 && routeIndexes[0] < nodes.Count
                ? routeIndexes[0]
                : FindNodeIndexForStop(route[0], nodes);
            if (HasRequiredCoverageBackfillConnector(GetRouteNodeIndexes(route, nodes), seedNodeIndex, nodes, connectorIndexes))
                return true;

            float currentLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
            if (currentLength == float.MaxValue)
                return false;

            DemandNode seed = seedNodeIndex >= 0 && seedNodeIndex < nodes.Count
                ? nodes[seedNodeIndex]
                : new DemandNode { StopPosition = route[0], Demand = 0 };
            float connectorReach = GetCoverageBackfillConnectorReach(seed, cfg);
            float bestScore = float.MaxValue;
            int bestNodeIndex = -1;
            int bestInsertAt = -1;

            foreach (int connectorIndex in connectorIndexes)
            {
                if (connectorIndex < 0 || connectorIndex >= nodes.Count)
                    continue;

                if (connectorIndex == seedNodeIndex)
                    continue;

                Vector3 connectorStop = nodes[connectorIndex].StopPosition;
                if (GetNearestRouteStopDistance(route, connectorStop) > connectorReach)
                    continue;

                if (IsTooCloseToExistingConnectorStop(route, connectorStop, cfg))
                    continue;

                for (int i = 0; i < route.Count; i++)
                {
                    Vector3 from = route[i];
                    Vector3 to = route[(i + 1) % route.Count];
                    float fromToConnector;
                    float connectorToNext;
                    if (!TryGetPlanningLinkDistance(from, connectorStop, nodes, cfg, stopLocator, out fromToConnector))
                        continue;
                    if (!TryGetPlanningLinkDistance(connectorStop, to, nodes, cfg, stopLocator, out connectorToNext))
                        continue;

                    float direct;
                    if (!TryGetPlanningLinkDistance(from, to, nodes, cfg, stopLocator, out direct))
                        direct = Geometry.DistanceXZ(from, to);

                    float added = fromToConnector + connectorToNext - direct;
                    if (added < -1f)
                        added = 0f;

                    if (currentLength + added > maxRouteLength)
                        continue;

                    float score = added + ComputeInsertionTurnPenalty(route, i, connectorStop) - GetNodeBenefit(nodes[connectorIndex]) * 0.35f;
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    bestNodeIndex = connectorIndex;
                    bestInsertAt = i + 1;
                }
            }

            if (bestNodeIndex < 0 || bestInsertAt < 0)
                return false;

            if (bestInsertAt >= route.Count)
                route.Add(nodes[bestNodeIndex].StopPosition);
            else
                route.Insert(bestInsertAt, nodes[bestNodeIndex].StopPosition);

            if (routeIndexes != null && !routeIndexes.Contains(bestNodeIndex))
                routeIndexes.Add(bestNodeIndex);

            if (bestNodeIndex >= 0 && bestNodeIndex < routeUseCount.Length)
                routeUseCount[bestNodeIndex]++;

            return true;
        }

        private bool HasRequiredCoverageBackfillConnector(List<int> routeIndexes, int seedIndex, List<DemandNode> nodes, HashSet<int> connectorIndexes)
        {
            if (seedIndex >= 0 && IsNetworkConnectorIndex(seedIndex, nodes, connectorIndexes))
                return HasDistinctNetworkConnector(routeIndexes, seedIndex, nodes, connectorIndexes);

            return HasNetworkConnector(routeIndexes, nodes, connectorIndexes);
        }

        private float GetNearestRouteStopDistance(List<Vector3> route, Vector3 target)
        {
            if (route == null || route.Count == 0)
                return float.MaxValue;

            float best = float.MaxValue;
            for (int i = 0; i < route.Count; i++)
                best = Mathf.Min(best, Geometry.DistanceXZ(route[i], target));

            return best;
        }

        private float GetCoverageBackfillLocalReach(DemandNode seed, AutoPublicTransitConfig cfg)
        {
            if (IsStrategicNode(seed))
                return Mathf.Max(cfg.MaxRoadDistance * 1.15f, cfg.MaxWalkingDistance * 3.25f);

            return Mathf.Max(cfg.MaxRoadDistance * 1.25f, cfg.MaxWalkingDistance * 3.5f);
        }

        private float GetCoverageBackfillConnectorReach(DemandNode seed, AutoPublicTransitConfig cfg)
        {
            if (DemandNodePurpose.HasPurpose(seed, DemandNodePurpose.TransitHub))
                return Mathf.Max(cfg.MaxRoadDistance * 1.5f, cfg.MaxWalkingDistance * 4.0f);

            if (IsTouristNode(seed))
                return Mathf.Max(cfg.MaxRoadDistance * 1.9f, cfg.MaxWalkingDistance * 5.0f);

            return Mathf.Max(cfg.MaxRoadDistance * 2.25f, cfg.MaxWalkingDistance * 5.25f);
        }

        private float GetCoverageBackfillMaxRouteLength(AutoPublicTransitConfig cfg)
        {
            return Mathf.Min(cfg.MaxLineLengthKm * 1000f, Mathf.Max(3000f, cfg.MaxRoadDistance * 5.0f));
        }

        private bool HasNetworkConnector(List<int> routeIndexes, List<DemandNode> nodes, HashSet<int> connectorIndexes)
        {
            if (routeIndexes == null || nodes == null)
                return false;

            for (int i = 0; i < routeIndexes.Count; i++)
            {
                if (IsNetworkConnectorIndex(routeIndexes[i], nodes, connectorIndexes))
                    return true;
            }

            return false;
        }

        private bool IsNetworkConnectorIndex(int index, List<DemandNode> nodes, HashSet<int> connectorIndexes)
        {
            if (index < 0 || nodes == null || index >= nodes.Count)
                return false;

            if (connectorIndexes != null && connectorIndexes.Contains(index))
                return true;

            return DemandNodePurpose.HasPurpose(nodes[index], DemandNodePurpose.TransitHub);
        }

        private void MarkNodesCoveredByRoute(List<Vector3> route, List<DemandNode> nodes, bool[] covered, AutoPublicTransitConfig cfg)
        {
            if (route == null || nodes == null || covered == null)
                return;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (covered[i])
                    continue;

                if (DoesStopListCoverNode(route, nodes[i], GetPublishedCoverageDistance(nodes[i], cfg)))
                    covered[i] = true;
            }
        }

        private bool DoesStopListCoverNode(List<Vector3> stops, DemandNode node, float maxDistance)
        {
            if (stops == null || stops.Count == 0)
                return false;

            for (int i = 0; i < stops.Count; i++)
            {
                if (Geometry.DistanceXZ(stops[i], node.StopPosition) <= maxDistance)
                    return true;
            }

            return false;
        }

        private void TryFillRouteGaps(
            List<Vector3> route,
            List<int> routeIndexes,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            TryFillRouteGaps(route, routeIndexes, nodes, used, routeUseCount, cfg, stopLocator, cfg.MaxStopsPerRoute, cfg.MaxRoadDistance * 1.15f);
        }

        private void TryFillRouteGaps(
            List<Vector3> route,
            List<int> routeIndexes,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            int maxStops,
            float gapTriggerDistance)
        {
            TryFillRouteGaps(route, routeIndexes, nodes, used, routeUseCount, cfg, stopLocator, maxStops, gapTriggerDistance, true);
        }

        private void TryFillRouteGaps(
            List<Vector3> route,
            List<int> routeIndexes,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            int maxStops,
            float gapTriggerDistance,
            bool enforceMinimumStopLimit)
        {
            bool inserted = true;
            int safety = 0;
            int requestedLimit = Mathf.Max(2, Mathf.Min(cfg.MaxStopsPerRoute, maxStops));
            int stopLimit = enforceMinimumStopLimit
                ? Mathf.Max(Mathf.Max(2, cfg.MinStopsPerRoute), requestedLimit)
                : requestedLimit;

            while (inserted && route.Count < stopLimit && safety < 24)
            {
                inserted = false;
                safety++;

                float largestGap = Mathf.Max(120f, gapTriggerDistance);
                int insertAt = -1;
                int bestNodeIndex = -1;

                for (int i = 0; i < route.Count - 1; i++)
                {
                    float gap;
                    if (!TryGetPlanningLinkDistance(route[i], route[i + 1], nodes, cfg, stopLocator, out gap))
                        gap = float.MaxValue;

                    if (gap <= largestGap)
                        continue;

                    int candidate = FindBestNodeForGap(route[i], route[i + 1], nodes, used, routeUseCount, route, cfg, stopLocator);
                    if (candidate == -1)
                        continue;

                    largestGap = gap;
                    insertAt = i + 1;
                    bestNodeIndex = candidate;
                }

                if (bestNodeIndex != -1)
                {
                    route.Insert(insertAt, nodes[bestNodeIndex].StopPosition);
                    if (routeIndexes != null)
                        routeIndexes.Add(bestNodeIndex);
                    routeUseCount[bestNodeIndex]++;
                    if (!IsStrategicNode(nodes[bestNodeIndex]))
                        used[bestNodeIndex] = true;
                    inserted = true;
                }
            }
        }

        private int FindBestNodeForGap(
            Vector3 from,
            Vector3 to,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            List<Vector3> route,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            int bestIndex = -1;
            float bestScore = float.MaxValue;
            int fromIndex = FindNodeIndexForStop(from, nodes);
            int toIndex = FindNodeIndexForStop(to, nodes);
            float direct;
            if (!TryGetPlanningLinkDistance(fromIndex, toIndex, from, to, nodes, cfg, stopLocator, out direct))
                return -1;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (!CanUseNode(i, nodes, used, routeUseCount))
                    continue;

                Vector3 stop = nodes[i].StopPosition;
                if (IsTooCloseToExistingRouteStop(route, stop, cfg))
                    continue;

                float a;
                float b;
                if (!TryGetPlanningLinkDistance(fromIndex, i, from, stop, nodes, cfg, stopLocator, out a))
                    continue;
                if (!TryGetPlanningLinkDistance(i, toIndex, stop, to, nodes, cfg, stopLocator, out b))
                    continue;

                float detour = a + b - direct;
                float score = detour - GetNodeBenefit(nodes[i]) * 0.35f;
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestIndex = i;
            }

            return bestIndex;
        }

        private void TryInsertRemainingUsefulStops(List<DemandNode> nodes, List<List<Vector3>> routes, bool[] used, int[] routeUseCount, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!IsStrategicNode(nodes[i]))
                    continue;

                if (!CanUseNode(i, nodes, used, routeUseCount))
                    continue;

                if (TryInsertNodeIntoExistingRoutes(i, nodes, routes, cfg, stopLocator))
                    RecordNodeUse(i, nodes, used, routeUseCount);
            }
        }

        private void EnsureTouristAnchorCoverageRoutes(
            List<DemandNode> nodes,
            List<List<Vector3>> routes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (nodes == null || routes == null || nodes.Count == 0)
                return;

            var uncoveredTouristNodes = new List<int>();
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!IsTouristNode(nodes[i]))
                    continue;

                if (IsNodeCoveredByAnyRoute(i, nodes, routes, cfg))
                    continue;

                uncoveredTouristNodes.Add(i);
            }

            uncoveredTouristNodes.Sort((a, b) => nodes[b].Demand.CompareTo(nodes[a].Demand));

            for (int i = 0; i < uncoveredTouristNodes.Count && TouristCoverageRouteCount < MaxTouristCoverageRoutes; i++)
            {
                int seedIndex = uncoveredTouristNodes[i];
                if (IsNodeCoveredByAnyRoute(seedIndex, nodes, routes, cfg))
                    continue;

                if (!CanUseNode(seedIndex, nodes, used, routeUseCount))
                    continue;

                List<Vector3> route;
                if (!TryBuildTouristCoverageRoute(seedIndex, nodes, used, routeUseCount, cfg, stopLocator, out route))
                    continue;

                routes.Add(route);
                TouristCoverageRouteCount++;
            }
        }

        private bool TryBuildTouristCoverageRoute(
            int seedIndex,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out List<Vector3> route)
        {
            route = new List<Vector3>();
            var routeIndexes = new List<int>();
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            int targetStops = Mathf.Min(cfg.MaxStopsPerRoute, Mathf.Max(minimumStops, 6));

            AddNodeToRoute(seedIndex, nodes, route, routeIndexes, used, routeUseCount);

            int safety = 0;
            while (route.Count < targetStops && safety < cfg.MaxStopsPerRoute * 3)
            {
                safety++;
                int bestIndex = FindBestTouristCoverageNode(seedIndex, route, nodes, used, routeUseCount, cfg, stopLocator);
                if (bestIndex == -1)
                    break;

                AddNodeToRoute(bestIndex, nodes, route, routeIndexes, used, routeUseCount);
            }

            if (route.Count < minimumStops)
            {
                ReleaseRouteIndexes(routeIndexes, nodes, used, routeUseCount);
                return false;
            }

            TryFillRouteGaps(route, routeIndexes, nodes, used, routeUseCount, cfg, stopLocator);
            route = OptimizeRoadOrder(route, nodes, cfg, stopLocator);
            route = PruneToFeasiblePlanningLoop(route, nodes, routeIndexes, used, routeUseCount, cfg, stopLocator);
            routeIndexes = GetRouteNodeIndexes(route, nodes);

            float routeLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
            if (route.Count < minimumStops ||
                routeLength == float.MaxValue ||
                routeLength > cfg.MaxLineLengthKm * 1000f ||
                !DoesRouteCoverNode(route, nodes[seedIndex], cfg) ||
                !HasUsefulRoutePurpose(route, nodes, routeIndexes, cfg))
            {
                ReleaseRouteIndexes(routeIndexes, nodes, used, routeUseCount);
                return false;
            }

            return true;
        }

        private int FindBestTouristCoverageNode(
            int seedIndex,
            List<Vector3> route,
            List<DemandNode> nodes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            int bestIndex = -1;
            float bestScore = float.MaxValue;
            DemandNode seed = nodes[seedIndex];
            Vector3 current = route[route.Count - 1];
            int currentIndex = FindNodeIndexForStop(current, nodes);
            float localReach = GetTouristCoverageLocalReach(cfg);

            for (int i = 0; i < nodes.Count; i++)
            {
                if (!CanUseNode(i, nodes, used, routeUseCount))
                    continue;

                if (IsTooCloseToExistingRouteStop(route, nodes[i].StopPosition, cfg))
                    continue;

                float seedDistance = Geometry.DistanceXZ(seed.StopPosition, nodes[i].StopPosition);
                if (seedDistance > localReach && !IsStrategicNode(nodes[i]))
                    continue;

                float linkDistance;
                if (!TryGetPlanningLinkDistance(currentIndex, i, current, nodes[i].StopPosition, nodes, cfg, stopLocator, out linkDistance))
                    continue;

                float projectedLength = EstimateClosedRouteLength(route, nodes[i].StopPosition, nodes, cfg, stopLocator);
                if (projectedLength > cfg.MaxLineLengthKm * 1000f)
                    continue;

                float score = linkDistance + seedDistance * 0.35f - GetNodeBenefit(nodes[i]) * 0.9f;
                if (IsTouristNode(nodes[i]))
                    score -= 260f;
                else if (IsStrategicNode(nodes[i]))
                    score -= 120f;

                score += ComputeBacktrackPenalty(route, nodes[i].StopPosition);

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestIndex = i;
            }

            return bestIndex;
        }

        private bool IsTouristNode(DemandNode node)
        {
            return DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TouristAnchor);
        }

        private bool IsNodeCoveredByAnyRoute(int nodeIndex, List<DemandNode> nodes, List<List<Vector3>> routes, AutoPublicTransitConfig cfg)
        {
            if (routes == null)
                return false;

            for (int i = 0; i < routes.Count; i++)
            {
                if (DoesRouteCoverNode(routes[i], nodes[nodeIndex], cfg))
                    return true;
            }

            return false;
        }

        private bool DoesRouteCoverNode(List<Vector3> route, DemandNode node, AutoPublicTransitConfig cfg)
        {
            if (route == null || route.Count == 0)
                return false;

            float coverageDistance = GetTouristCoverageStopDistance(cfg);
            for (int i = 0; i < route.Count; i++)
            {
                if (Geometry.DistanceXZ(route[i], node.StopPosition) <= coverageDistance)
                    return true;
            }

            return false;
        }

        private float GetTouristCoverageStopDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(70f, cfg.GridCellSize * 0.55f);
        }

        private float GetTouristCoverageLocalReach(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(cfg.MaxRoadDistance * 2.25f, cfg.MaxWalkingDistance * 4f);
        }

        private bool TryInsertNodeIntoExistingRoutes(int nodeIndex, List<DemandNode> nodes, List<List<Vector3>> routes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            if (routes == null || routes.Count == 0)
                return false;

            DemandNode node = nodes[nodeIndex];
            int bestRoute = -1;
            int bestInsertAt = -1;
            float bestScore = float.MaxValue;

            for (int r = 0; r < routes.Count; r++)
            {
                List<Vector3> route = routes[r];
                if (route.Count >= cfg.MaxStopsPerRoute)
                    continue;

                if (IsTooCloseToExistingRouteStop(route, node.StopPosition, cfg))
                    continue;

                float currentLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
                if (float.IsInfinity(currentLength) || currentLength == float.MaxValue)
                    continue;

                for (int i = 0; i < route.Count; i++)
                {
                    Vector3 from = route[i];
                    Vector3 to = route[(i + 1) % route.Count];
                    int fromIndex = FindNodeIndexForStop(from, nodes);
                    int toIndex = FindNodeIndexForStop(to, nodes);
                    float a;
                    float b;
                    if (!TryGetPlanningLinkDistance(fromIndex, nodeIndex, from, node.StopPosition, nodes, cfg, stopLocator, out a))
                        continue;
                    if (!TryGetPlanningLinkDistance(nodeIndex, toIndex, node.StopPosition, to, nodes, cfg, stopLocator, out b))
                        continue;

                    float direct;
                    if (!TryGetPlanningLinkDistance(fromIndex, toIndex, from, to, nodes, cfg, stopLocator, out direct))
                        direct = Geometry.DistanceXZ(from, to);

                    float added = a + b - direct;
                    if (currentLength + added > cfg.MaxLineLengthKm * 1000f)
                        continue;

                    float score = added - GetNodeBenefit(node) * 0.7f;
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    bestRoute = r;
                    bestInsertAt = i + 1;
                }
            }

            if (bestRoute == -1)
                return false;

            float acceptanceThreshold = IsStrategicNode(node) ? 180f : 80f;
            if (bestScore > acceptanceThreshold)
                return false;

            List<Vector3> best = routes[bestRoute];
            if (bestInsertAt >= best.Count)
                best.Add(node.StopPosition);
            else
                best.Insert(bestInsertAt, node.StopPosition);

            return true;
        }

        private float EstimateClosedRouteLength(List<Vector3> route, Vector3 candidateStop, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            float total = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                float link;
                if (!TryGetPlanningLinkDistance(route[i - 1], route[i], nodes, cfg, stopLocator, out link))
                    return float.MaxValue;

                total += link;
            }

            float nextLink;
            if (!TryGetPlanningLinkDistance(route[route.Count - 1], candidateStop, nodes, cfg, stopLocator, out nextLink))
                return float.MaxValue;

            float closingLink;
            if (!TryGetPlanningLinkDistance(candidateStop, route[0], nodes, cfg, stopLocator, out closingLink))
                return float.MaxValue;

            total += nextLink;
            total += closingLink;
            return total;
        }

        private bool IsTooCloseToExistingRouteStop(List<Vector3> route, Vector3 candidateStop, AutoPublicTransitConfig cfg)
        {
            float minSpacing = Mathf.Max(55f, cfg.GridCellSize * 0.45f);
            for (int i = 0; i < route.Count; i++)
            {
                if (Geometry.DistanceXZ(route[i], candidateStop) < minSpacing)
                    return true;
            }

            return false;
        }

        private float ComputeBacktrackPenalty(List<Vector3> route, Vector3 candidateStop)
        {
            if (route.Count < 2)
                return 0f;

            Vector2 previous = Geometry.ToXZ(route[route.Count - 1] - route[route.Count - 2]);
            Vector2 next = Geometry.ToXZ(candidateStop - route[route.Count - 1]);
            if (previous.sqrMagnitude < 1f || next.sqrMagnitude < 1f)
                return 0f;

            float angle = Mathf.Abs(Geometry.SignedAngle(previous.normalized, next.normalized));
            if (angle > 165f)
                return 240f;
            if (angle > 135f)
                return 140f;
            if (angle > 100f)
                return 60f;

            return 0f;
        }

        private float ComputeOpenRouteLength(List<Vector3> route)
        {
            if (route == null || route.Count < 2)
                return 0f;

            float total = 0f;
            for (int i = 1; i < route.Count; i++)
            {
                total += Geometry.DistanceXZ(route[i - 1], route[i]);
            }

            total += Geometry.DistanceXZ(route[route.Count - 1], route[0]);
            return total;
        }

        private List<Vector3> PruneToFeasiblePlanningLoop(
            List<Vector3> route,
            List<DemandNode> nodes,
            List<int> trackedRouteIndexes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            return PruneToFeasiblePlanningLoop(
                route,
                nodes,
                trackedRouteIndexes,
                used,
                routeUseCount,
                cfg,
                stopLocator,
                cfg.MaxLineLengthKm * 1000f);
        }

        private List<Vector3> PruneToFeasiblePlanningLoop(
            List<Vector3> route,
            List<DemandNode> nodes,
            List<int> trackedRouteIndexes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            float maxRouteLength)
        {
            return PruneToFeasiblePlanningLoop(
                route,
                nodes,
                trackedRouteIndexes,
                used,
                routeUseCount,
                cfg,
                stopLocator,
                maxRouteLength,
                true);
        }

        private List<Vector3> PruneToFeasiblePlanningLoop(
            List<Vector3> route,
            List<DemandNode> nodes,
            List<int> trackedRouteIndexes,
            bool[] used,
            int[] routeUseCount,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            float maxRouteLength,
            bool optimizeAfterRemoval)
        {
            var current = NormalizeOpenRoute(route);
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            int safety = 0;
            float lengthLimit = Mathf.Max(120f, maxRouteLength);

            while (current.Count >= minimumStops && safety < 16)
            {
                float length = ComputePlanningClosedLength(current, nodes, cfg, stopLocator);
                if (length != float.MaxValue && length <= lengthLimit)
                {
                    ReleaseDroppedRouteIndexes(trackedRouteIndexes, current, nodes, used, routeUseCount);
                    return current;
                }

                if (current.Count <= minimumStops)
                    break;

                int removeIndex = FindBestPlanningLoopRemoval(current, nodes, cfg, stopLocator, optimizeAfterRemoval);
                if (removeIndex < 0)
                    break;

                current.RemoveAt(removeIndex);
                if (optimizeAfterRemoval)
                    current = OptimizeRoadOrder(current, nodes, cfg, stopLocator);
                safety++;
            }

            ReleaseDroppedRouteIndexes(trackedRouteIndexes, current, nodes, used, routeUseCount);
            return current;
        }

        private int FindBestPlanningLoopRemoval(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator, bool optimizeAfterRemoval)
        {
            int badLinkStart;
            if (TryFindBadPlanningLink(route, nodes, cfg, stopLocator, out badLinkStart))
            {
                int nextIndex = (badLinkStart + 1) % route.Count;
                return ChooseLowerBenefitStop(route, nodes, badLinkStart, nextIndex);
            }

            int bestRemove = -1;
            float bestScore = float.MaxValue;
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            for (int i = 0; i < route.Count; i++)
            {
                var candidate = new List<Vector3>(route);
                candidate.RemoveAt(i);
                if (candidate.Count < minimumStops)
                    continue;

                if (optimizeAfterRemoval)
                    candidate = OptimizeRoadOrder(candidate, nodes, cfg, stopLocator);
                float length = ComputePlanningClosedLength(candidate, nodes, cfg, stopLocator);
                if (length == float.MaxValue)
                    continue;

                int nodeIndex = FindNodeIndexForStop(route[i], nodes);
                float score = length;
                if (nodeIndex >= 0)
                    score -= GetNodeBenefit(nodes[nodeIndex]) * 0.25f;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestRemove = i;
            }

            if (bestRemove != -1)
                return bestRemove;

            return FindWeakestRouteStopIndex(route, nodes);
        }

        private bool TryFindBadPlanningLink(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator, out int linkStart)
        {
            linkStart = -1;
            if (route == null || route.Count < 2)
                return false;

            for (int i = 0; i < route.Count; i++)
            {
                float distance;
                if (!TryGetPlanningLinkDistance(route[i], route[(i + 1) % route.Count], nodes, cfg, stopLocator, out distance))
                {
                    linkStart = i;
                    return true;
                }
            }

            return false;
        }

        private int ChooseLowerBenefitStop(List<Vector3> route, List<DemandNode> nodes, int first, int second)
        {
            int firstNode = FindNodeIndexForStop(route[first], nodes);
            int secondNode = FindNodeIndexForStop(route[second], nodes);
            float firstBenefit = firstNode >= 0 ? GetNodeBenefit(nodes[firstNode]) : 0f;
            float secondBenefit = secondNode >= 0 ? GetNodeBenefit(nodes[secondNode]) : 0f;
            return firstBenefit <= secondBenefit ? first : second;
        }

        private int FindWeakestRouteStopIndex(List<Vector3> route, List<DemandNode> nodes)
        {
            int weakest = -1;
            float weakestBenefit = float.MaxValue;
            for (int i = 0; i < route.Count; i++)
            {
                int nodeIndex = FindNodeIndexForStop(route[i], nodes);
                float benefit = nodeIndex >= 0 ? GetNodeBenefit(nodes[nodeIndex]) : 0f;
                if (benefit >= weakestBenefit)
                    continue;

                weakestBenefit = benefit;
                weakest = i;
            }

            return weakest;
        }

        private List<int> GetRouteNodeIndexes(List<Vector3> route, List<DemandNode> nodes)
        {
            var indexes = new List<int>();
            if (route == null || nodes == null)
                return indexes;

            for (int i = 0; i < route.Count; i++)
            {
                int index = FindNodeIndexForStop(route[i], nodes);
                if (index >= 0 && !indexes.Contains(index))
                    indexes.Add(index);
            }

            return indexes;
        }

        private void ReleaseDroppedRouteIndexes(List<int> trackedRouteIndexes, List<Vector3> keptRoute, List<DemandNode> nodes, bool[] used, int[] routeUseCount)
        {
            if (trackedRouteIndexes == null || trackedRouteIndexes.Count == 0 || nodes == null)
                return;

            List<int> keptIndexes = GetRouteNodeIndexes(keptRoute, nodes);
            for (int i = 0; i < trackedRouteIndexes.Count; i++)
            {
                int index = trackedRouteIndexes[i];
                if (keptIndexes.Contains(index))
                    continue;

                routeUseCount[index] = Mathf.Max(0, routeUseCount[index] - 1);
                if (!IsStrategicNode(nodes[index]))
                    used[index] = false;
            }
        }

        private List<Vector3> OptimizeRoadOrder(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            var source = NormalizeOpenRoute(route);
            if (source.Count < 4 || stopLocator == null)
                return source;

            var candidates = new List<List<Vector3>>();
            AddRoadOrderCandidate(candidates, source, nodes, cfg, stopLocator);
            AddRoadOrderCandidate(candidates, BuildAngularRoadLoop(source), nodes, cfg, stopLocator);

            for (int start = 0; start < source.Count; start++)
            {
                AddRoadOrderCandidate(candidates, BuildNearestRoadLoop(source, start, nodes, cfg, stopLocator), nodes, cfg, stopLocator);
                AddRoadOrderCandidate(candidates, BuildCheapestInsertionRoadLoop(source, start, nodes, cfg, stopLocator), nodes, cfg, stopLocator);
            }

            List<Vector3> best = source;
            float bestScore = ScorePlanningLoop(best, nodes, cfg, stopLocator);
            for (int i = 0; i < candidates.Count; i++)
            {
                List<Vector3> candidate = ImproveRoadLoopWithTwoOpt(candidates[i], nodes, cfg, stopLocator);
                float score = ScorePlanningLoop(candidate, nodes, cfg, stopLocator);
                if (score >= bestScore)
                    continue;

                best = candidate;
                bestScore = score;
            }

            return best;
        }

        private void AddRoadOrderCandidate(
            List<List<Vector3>> candidates,
            List<Vector3> candidate,
            List<DemandNode> nodes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (candidates == null || candidate == null || candidate.Count < 2)
                return;

            float length = ComputePlanningClosedLength(candidate, nodes, cfg, stopLocator);
            if (length == float.MaxValue)
                return;

            candidates.Add(candidate);
        }

        private List<Vector3> BuildAngularRoadLoop(List<Vector3> route)
        {
            var ordered = new List<Vector3>(route);
            if (ordered.Count < 4)
                return ordered;

            Vector3 center = Vector3.zero;
            for (int i = 0; i < ordered.Count; i++)
                center += ordered[i];

            center /= Mathf.Max(1, ordered.Count);
            ordered.Sort((a, b) =>
            {
                float angleA = Mathf.Atan2(a.z - center.z, a.x - center.x);
                float angleB = Mathf.Atan2(b.z - center.z, b.x - center.x);
                return angleA.CompareTo(angleB);
            });

            return ordered;
        }

        private List<Vector3> BuildNearestRoadLoop(List<Vector3> route, int startIndex, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            var ordered = new List<Vector3>();
            if (route == null || route.Count == 0)
                return ordered;

            bool[] used = new bool[route.Count];
            int current = Mathf.Clamp(startIndex, 0, route.Count - 1);
            ordered.Add(route[current]);
            used[current] = true;

            while (ordered.Count < route.Count)
            {
                int best = -1;
                float bestDistance = float.MaxValue;
                Vector3 from = route[current];
                for (int i = 0; i < route.Count; i++)
                {
                    if (used[i])
                        continue;

                    float distance;
                    if (!TryGetPlanningLinkDistance(from, route[i], nodes, cfg, stopLocator, out distance))
                        continue;

                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    best = i;
                }

                if (best == -1)
                    return new List<Vector3>(route);

                current = best;
                used[current] = true;
                ordered.Add(route[current]);
            }

            return ordered;
        }

        private List<Vector3> BuildCheapestInsertionRoadLoop(List<Vector3> route, int startIndex, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            var ordered = new List<Vector3>();
            if (route == null || route.Count == 0)
                return ordered;

            bool[] used = new bool[route.Count];
            int start = Mathf.Clamp(startIndex, 0, route.Count - 1);
            int second = FindFarthestReachableRouteStop(route, start, nodes, cfg, stopLocator);
            if (second == -1)
                return new List<Vector3>(route);

            ordered.Add(route[start]);
            ordered.Add(route[second]);
            used[start] = true;
            used[second] = true;

            while (ordered.Count < route.Count)
            {
                int bestRouteIndex = -1;
                int bestInsertAt = -1;
                float bestScore = float.MaxValue;

                for (int candidateIndex = 0; candidateIndex < route.Count; candidateIndex++)
                {
                    if (used[candidateIndex])
                        continue;

                    Vector3 candidate = route[candidateIndex];
                    for (int insertAfter = 0; insertAfter < ordered.Count; insertAfter++)
                    {
                        Vector3 from = ordered[insertAfter];
                        Vector3 to = ordered[(insertAfter + 1) % ordered.Count];
                        float fromToCandidate;
                        float candidateToNext;
                        float direct;
                        if (!TryGetPlanningLinkDistance(from, candidate, nodes, cfg, stopLocator, out fromToCandidate))
                            continue;
                        if (!TryGetPlanningLinkDistance(candidate, to, nodes, cfg, stopLocator, out candidateToNext))
                            continue;
                        if (!TryGetPlanningLinkDistance(from, to, nodes, cfg, stopLocator, out direct))
                            direct = Geometry.DistanceXZ(from, to);

                        float score = fromToCandidate + candidateToNext - direct;
                        score += ComputeInsertionTurnPenalty(ordered, insertAfter, candidate);
                        if (score >= bestScore)
                            continue;

                        bestScore = score;
                        bestRouteIndex = candidateIndex;
                        bestInsertAt = insertAfter + 1;
                    }
                }

                if (bestRouteIndex == -1 || bestInsertAt == -1)
                    return new List<Vector3>(route);

                if (bestInsertAt >= ordered.Count)
                    ordered.Add(route[bestRouteIndex]);
                else
                    ordered.Insert(bestInsertAt, route[bestRouteIndex]);

                used[bestRouteIndex] = true;
            }

            return ordered;
        }

        private int FindFarthestReachableRouteStop(List<Vector3> route, int startIndex, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            int best = -1;
            float bestDistance = -1f;
            Vector3 start = route[startIndex];

            for (int i = 0; i < route.Count; i++)
            {
                if (i == startIndex)
                    continue;

                float distance;
                if (!TryGetPlanningLinkDistance(start, route[i], nodes, cfg, stopLocator, out distance))
                    continue;

                if (distance <= bestDistance)
                    continue;

                bestDistance = distance;
                best = i;
            }

            return best;
        }

        private float ComputeInsertionTurnPenalty(List<Vector3> ordered, int insertAfter, Vector3 candidate)
        {
            if (ordered == null || ordered.Count < 2)
                return 0f;

            Vector3 previous = ordered[insertAfter];
            Vector3 next = ordered[(insertAfter + 1) % ordered.Count];
            return ComputeTurnPenalty(previous, candidate, next) * 0.5f;
        }

        private List<Vector3> ImproveRoadLoopWithTwoOpt(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            var improved = new List<Vector3>(route);
            if (improved.Count < 4)
                return improved;

            bool changed = true;
            int pass = 0;
            while (changed && pass < 12)
            {
                changed = false;
                pass++;

                for (int i = 0; i < improved.Count - 1; i++)
                {
                    for (int k = i + 2; k < improved.Count; k++)
                    {
                        if (i == 0 && k == improved.Count - 1)
                            continue;

                        float current = ComputeTwoOptEdgeCost(improved, i, k, false, nodes, cfg, stopLocator);
                        float swapped = ComputeTwoOptEdgeCost(improved, i, k, true, nodes, cfg, stopLocator);
                        if (swapped >= current - 12f)
                            continue;

                        ReverseRange(improved, i + 1, k);
                        changed = true;
                    }
                }
            }

            return improved;
        }

        private float ComputeTwoOptEdgeCost(List<Vector3> route, int i, int k, bool swapped, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            Vector3 a = route[i];
            Vector3 b = route[(i + 1) % route.Count];
            Vector3 c = route[k];
            Vector3 d = route[(k + 1) % route.Count];

            float first;
            float second;
            if (swapped)
            {
                if (!TryGetPlanningLinkDistance(a, c, nodes, cfg, stopLocator, out first))
                    return float.MaxValue;
                if (!TryGetPlanningLinkDistance(b, d, nodes, cfg, stopLocator, out second))
                    return float.MaxValue;
            }
            else
            {
                if (!TryGetPlanningLinkDistance(a, b, nodes, cfg, stopLocator, out first))
                    return float.MaxValue;
                if (!TryGetPlanningLinkDistance(c, d, nodes, cfg, stopLocator, out second))
                    return float.MaxValue;
            }

            return first + second;
        }

        private float ScorePlanningLoop(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            float length = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);
            if (length == float.MaxValue)
                return float.MaxValue;

            float score = length;
            score += CountRouteCrossings(route) * Mathf.Max(900f, cfg.MaxRoadDistance * 2f);
            score += CountRouteSharpReversals(route) * Mathf.Max(240f, cfg.MaxRoadDistance * 0.8f);
            score += CountRouteNearRepeats(route, Mathf.Max(60f, cfg.GridCellSize * 0.5f)) * Mathf.Max(180f, cfg.GridCellSize);

            float span = Mathf.Max(1f, ComputeRouteSpan(route));
            score += (length / span) * 120f;
            return score;
        }

        private int CountRouteSharpReversals(List<Vector3> route)
        {
            if (route == null || route.Count < 3)
                return 0;

            int reversals = 0;
            for (int i = 0; i < route.Count; i++)
            {
                Vector3 previous = route[(i - 1 + route.Count) % route.Count];
                Vector3 current = route[i];
                Vector3 next = route[(i + 1) % route.Count];
                Vector2 incoming = Geometry.ToXZ(current - previous);
                Vector2 outgoing = Geometry.ToXZ(next - current);
                if (incoming.sqrMagnitude < 1f || outgoing.sqrMagnitude < 1f)
                    continue;

                float angle = Mathf.Abs(Geometry.SignedAngle(incoming.normalized, outgoing.normalized));
                if (angle > 150f)
                    reversals++;
            }

            return reversals;
        }

        private int CountRouteNearRepeats(List<Vector3> route, float maxDistance)
        {
            int repeats = 0;
            if (route == null)
                return repeats;

            for (int i = 0; i < route.Count; i++)
            {
                for (int j = i + 2; j < route.Count; j++)
                {
                    if (i == 0 && j == route.Count - 1)
                        continue;

                    if (Geometry.DistanceXZ(route[i], route[j]) <= maxDistance)
                        repeats++;
                }
            }

            return repeats;
        }

        private int CountRouteCrossings(List<Vector3> route)
        {
            int crossings = 0;
            if (route == null || route.Count < 4)
                return crossings;

            for (int i = 0; i < route.Count; i++)
            {
                int iNext = (i + 1) % route.Count;
                for (int j = i + 1; j < route.Count; j++)
                {
                    int jNext = (j + 1) % route.Count;
                    if (i == j || iNext == j || jNext == i)
                        continue;
                    if (i == 0 && jNext == 0)
                        continue;

                    if (SegmentsIntersect(route[i], route[iNext], route[j], route[jNext]))
                        crossings++;
                }
            }

            return crossings;
        }

        private bool SegmentsIntersect(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (ShareEndpoint(a, b, c, d))
                return false;

            Vector2 p1 = Geometry.ToXZ(a);
            Vector2 p2 = Geometry.ToXZ(b);
            Vector2 q1 = Geometry.ToXZ(c);
            Vector2 q2 = Geometry.ToXZ(d);
            float o1 = Orientation(p1, p2, q1);
            float o2 = Orientation(p1, p2, q2);
            float o3 = Orientation(q1, q2, p1);
            float o4 = Orientation(q1, q2, p2);

            return o1 * o2 < -0.01f && o3 * o4 < -0.01f;
        }

        private bool ShareEndpoint(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            return Geometry.DistanceXZ(a, c) < 1f
                || Geometry.DistanceXZ(a, d) < 1f
                || Geometry.DistanceXZ(b, c) < 1f
                || Geometry.DistanceXZ(b, d) < 1f;
        }

        private float Orientation(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private float ComputeRouteSpan(List<Vector3> route)
        {
            float span = 0f;
            if (route == null)
                return span;

            for (int i = 0; i < route.Count; i++)
            {
                for (int j = i + 1; j < route.Count; j++)
                    span = Mathf.Max(span, Geometry.DistanceXZ(route[i], route[j]));
            }

            return span;
        }

        private float ComputeTurnPenalty(Vector3 previous, Vector3 current, Vector3 next)
        {
            Vector2 incoming = Geometry.ToXZ(current - previous);
            Vector2 outgoing = Geometry.ToXZ(next - current);
            if (incoming.sqrMagnitude < 1f || outgoing.sqrMagnitude < 1f)
                return 0f;

            float angle = Mathf.Abs(Geometry.SignedAngle(incoming.normalized, outgoing.normalized));
            if (angle > 165f)
                return 360f;
            if (angle > 135f)
                return 190f;
            if (angle > 105f)
                return 70f;

            return 0f;
        }

        private float ComputePlanningClosedLength(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            if (route == null || route.Count < 2)
                return 0f;

            float total = 0f;
            for (int i = 0; i < route.Count; i++)
            {
                float link;
                if (!TryGetPlanningLinkDistance(route[i], route[(i + 1) % route.Count], nodes, cfg, stopLocator, out link))
                    return float.MaxValue;

                total += link;
            }

            return total;
        }

        private bool TryGetPlanningLinkDistance(Vector3 from, Vector3 to, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator, out float distance)
        {
            return TryGetPlanningLinkDistance(
                FindNodeIndexForStop(from, nodes),
                FindNodeIndexForStop(to, nodes),
                from,
                to,
                nodes,
                cfg,
                stopLocator,
                out distance);
        }

        private bool TryGetPlanningLinkDistance(
            int fromIndex,
            int toIndex,
            Vector3 from,
            Vector3 to,
            List<DemandNode> nodes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out float distance)
        {
            float direct = Geometry.DistanceXZ(from, to);
            float directLimit = cfg.MaxRoadDistance * 1.15f;
            distance = direct;

            if (fromIndex < 0 || toIndex < 0 || stopLocator == null)
                return direct <= directLimit;

            long cacheKey = GetPlanningLinkCacheKey(fromIndex, toIndex);
            float cachedDistance;
            if (_planningLinkDistanceCache.TryGetValue(cacheKey, out cachedDistance))
            {
                if (cachedDistance < 0f)
                    return false;

                distance = cachedDistance;
                return true;
            }

            DemandNode fromNode = nodes[fromIndex];
            DemandNode toNode = nodes[toIndex];
            float allowed = Mathf.Max(GetAllowedLinkDistance(fromNode, cfg), GetAllowedLinkDistance(toNode, cfg));
            float maxRoadDistance = allowed * 2.35f;
            float roadDistance;
            if (stopLocator.TryEstimateRoadDistance(fromNode.StopSegmentId, toNode.StopSegmentId, maxRoadDistance, out roadDistance))
            {
                distance = Mathf.Max(direct, roadDistance);
                _planningLinkDistanceCache[cacheKey] = distance;
                return true;
            }

            if (fromNode.StopSegmentId != 0 && toNode.StopSegmentId != 0)
            {
                float roadGraphFallbackLimit = GetRoadGraphFailureFallbackDistanceLimit(fromNode, toNode, cfg);
                if (direct <= roadGraphFallbackLimit)
                {
                    LocationFallbackLinkCount++;
                    distance = direct + GetRoadGraphFailureFallbackPenalty(direct, cfg);
                    _planningLinkDistanceCache[cacheKey] = distance;
                    return true;
                }

                RoadGraphRejectedLinkCount++;
                _planningLinkDistanceCache[cacheKey] = -1f;
                return false;
            }

            float fallbackLimit = GetLocationFallbackDistanceLimit(fromNode, toNode, cfg);
            if (direct <= fallbackLimit)
            {
                LocationFallbackLinkCount++;
                distance = direct + GetLocationFallbackPenalty(direct, cfg);
                _planningLinkDistanceCache[cacheKey] = distance;
                return true;
            }

            bool accepted = direct <= Mathf.Min(directLimit, allowed * 0.8f);
            _planningLinkDistanceCache[cacheKey] = accepted ? direct : -1f;
            return accepted;
        }

        private long GetPlanningLinkCacheKey(int fromIndex, int toIndex)
        {
            return ((long)fromIndex << 32) ^ (uint)toIndex;
        }

        private float GetLocationFallbackDistanceLimit(DemandNode fromNode, DemandNode toNode, AutoPublicTransitConfig cfg)
        {
            float limit = Mathf.Max(GetAllowedLinkDistance(fromNode, cfg), GetAllowedLinkDistance(toNode, cfg));
            if (!IsStrategicNode(fromNode) && !IsStrategicNode(toNode))
                limit = Mathf.Min(limit, cfg.MaxRoadDistance);

            return Mathf.Max(120f, limit);
        }

        private float GetRoadGraphFailureFallbackDistanceLimit(DemandNode fromNode, DemandNode toNode, AutoPublicTransitConfig cfg)
        {
            bool fromStrategic = IsStrategicNode(fromNode);
            bool toStrategic = IsStrategicNode(toNode);
            if (!fromStrategic && !toStrategic)
                return Mathf.Max(120f, cfg.MaxRoadDistance * 0.85f);

            float limit = cfg.MaxRoadDistance * (fromStrategic && toStrategic ? 2.45f : 2.05f);
            if (DemandNodePurpose.HasPurpose(fromNode, DemandNodePurpose.TransitHub) ||
                DemandNodePurpose.HasPurpose(toNode, DemandNodePurpose.TransitHub))
            {
                limit = Mathf.Max(limit, cfg.MaxWalkingDistance * 5.25f);
            }

            if (DemandNodePurpose.HasPurpose(fromNode, DemandNodePurpose.TouristAnchor) ||
                DemandNodePurpose.HasPurpose(toNode, DemandNodePurpose.TouristAnchor))
            {
                limit = Mathf.Max(limit, cfg.MaxWalkingDistance * 5.0f);
            }

            return Mathf.Min(Mathf.Max(1800f, cfg.MaxRoadDistance * 3.0f), Mathf.Max(120f, limit));
        }

        private float GetRoadGraphFailureFallbackPenalty(float directDistance, AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(cfg.MaxRoadDistance * 0.75f, Mathf.Min(cfg.MaxRoadDistance * 1.8f, directDistance * 0.45f));
        }

        private float GetLocationFallbackPenalty(float directDistance, AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(70f, Mathf.Min(cfg.MaxRoadDistance * 0.35f, directDistance * 0.25f));
        }

        private int FindNodeIndexForStop(Vector3 stop, List<DemandNode> nodes)
        {
            if (nodes == null)
                return -1;

            int bestIndex = -1;
            float bestSqr = 12f * 12f;
            for (int i = 0; i < nodes.Count; i++)
            {
                float dx = nodes[i].StopPosition.x - stop.x;
                float dz = nodes[i].StopPosition.z - stop.z;
                float sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                bestIndex = i;
            }

            return bestIndex;
        }

        private List<Vector3> NormalizeOpenRoute(List<Vector3> route)
        {
            var normalized = new List<Vector3>();
            if (route == null)
                return normalized;

            for (int i = 0; i < route.Count; i++)
            {
                Vector3 stop = route[i];
                if (normalized.Count > 0 && Geometry.DistanceXZ(normalized[normalized.Count - 1], stop) < 1f)
                    continue;

                normalized.Add(stop);
            }

            if (normalized.Count > 1 && Geometry.DistanceXZ(normalized[0], normalized[normalized.Count - 1]) < 1f)
                normalized.RemoveAt(normalized.Count - 1);

            return normalized;
        }

        private void ReverseRange(List<Vector3> route, int start, int end)
        {
            while (start < end)
            {
                Vector3 tmp = route[start];
                route[start] = route[end];
                route[end] = tmp;
                start++;
                end--;
            }
        }
    }
}
