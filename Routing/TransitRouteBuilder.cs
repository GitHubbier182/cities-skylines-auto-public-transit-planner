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

        public int LocationFallbackLinkCount { get; private set; }
        public int RoadGraphRejectedLinkCount { get; private set; }

        public List<List<Vector3>> BuildRoutes(List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            LocationFallbackLinkCount = 0;
            RoadGraphRejectedLinkCount = 0;

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
                AddNodeToRoute(i, nodes, route, routeIndexes, used, routeUseCount);

                while (route.Count < cfg.MaxStopsPerRoute)
                {
                    int bestIndex = FindBestNextNode(route, nodes, used, routeUseCount, cfg, stopLocator);
                    if (bestIndex == -1)
                        break;

                    AddNodeToRoute(bestIndex, nodes, route, routeIndexes, used, routeUseCount);
                }

                TryFillRouteGaps(route, routeIndexes, nodes, used, routeUseCount, cfg, stopLocator);
                route = OptimizeRoadOrder(route, nodes, cfg, stopLocator);
                route = PruneToFeasiblePlanningLoop(route, nodes, routeIndexes, used, routeUseCount, cfg, stopLocator);
                routeIndexes = GetRouteNodeIndexes(route, nodes);
                float routeLength = ComputePlanningClosedLength(route, nodes, cfg, stopLocator);

                if (route.Count >= cfg.MinStopsPerRoute &&
                    routeLength != float.MaxValue &&
                    routeLength <= cfg.MaxLineLengthKm * 1000f &&
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

            for (int i = routes.Count - 1; i >= 0; i--)
            {
                routes[i] = OptimizeRoadOrder(routes[i], nodes, cfg, stopLocator);
                routes[i] = PruneToFeasiblePlanningLoop(routes[i], nodes, null, used, routeUseCount, cfg, stopLocator);
                List<int> routeIndexes = GetRouteNodeIndexes(routes[i], nodes);
                float routeLength = ComputePlanningClosedLength(routes[i], nodes, cfg, stopLocator);
                if (routes[i].Count < cfg.MinStopsPerRoute ||
                    routeLength == float.MaxValue ||
                    routeLength > cfg.MaxLineLengthKm * 1000f ||
                    !HasUsefulRoutePurpose(routes[i], nodes, routeIndexes, cfg))
                {
                    routes.RemoveAt(i);
                    continue;
                }

                if (routes[i].Count > 1)
                    routes[i].Add(routes[i][0]);
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
            for (int i = 0; i < routeIndexes.Count; i++)
            {
                int index = routeIndexes[i];
                routeUseCount[index] = Mathf.Max(0, routeUseCount[index] - 1);
                if (!IsStrategicNode(nodes[index]))
                    used[index] = false;
            }
        }

        private int FindBestNextNode(
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
            bool routeHasStrategicNode = RouteHasStrategicNode(route, nodes);

            for (int j = 0; j < nodes.Count; j++)
            {
                if (!CanUseNode(j, nodes, used, routeUseCount))
                    continue;

                if (IsTooCloseToExistingRouteStop(route, nodes[j].StopPosition, cfg))
                    continue;

                float dist;
                if (!TryGetPlanningLinkDistance(currentIndex, j, current, nodes[j].StopPosition, nodes, cfg, stopLocator, out dist))
                    continue;

                float projectedLength = EstimateClosedRouteLength(route, nodes[j].StopPosition, nodes, cfg, stopLocator);
                if (projectedLength > cfg.MaxLineLengthKm * 1000f)
                    continue;

                float score = dist - GetNodeBenefit(nodes[j]);
                score += ComputeBacktrackPenalty(route, nodes[j].StopPosition);

                if (!routeHasStrategicNode && IsStrategicNode(nodes[j]))
                    score -= 180f;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestIndex = j;
            }

            return bestIndex;
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

            return totalDemand >= cfg.DemandThreshold * cfg.MinStopsPerRoute;
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
            bool inserted = true;
            int safety = 0;

            while (inserted && route.Count < cfg.MaxStopsPerRoute && safety < 24)
            {
                inserted = false;
                safety++;

                float largestGap = cfg.MaxRoadDistance * 1.15f;
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
                if (!CanUseNode(i, nodes, used, routeUseCount))
                    continue;

                if (TryInsertNodeIntoExistingRoutes(i, nodes, routes, cfg, stopLocator))
                    RecordNodeUse(i, nodes, used, routeUseCount);
            }
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
            var current = NormalizeOpenRoute(route);
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            int safety = 0;

            while (current.Count >= minimumStops && safety < 16)
            {
                float length = ComputePlanningClosedLength(current, nodes, cfg, stopLocator);
                if (length != float.MaxValue && length <= cfg.MaxLineLengthKm * 1000f)
                {
                    ReleaseDroppedRouteIndexes(trackedRouteIndexes, current, nodes, used, routeUseCount);
                    return current;
                }

                if (current.Count <= minimumStops)
                    break;

                int removeIndex = FindBestPlanningLoopRemoval(current, nodes, cfg, stopLocator);
                if (removeIndex < 0)
                    break;

                current.RemoveAt(removeIndex);
                current = OptimizeRoadOrder(current, nodes, cfg, stopLocator);
                safety++;
            }

            ReleaseDroppedRouteIndexes(trackedRouteIndexes, current, nodes, used, routeUseCount);
            return current;
        }

        private int FindBestPlanningLoopRemoval(List<Vector3> route, List<DemandNode> nodes, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
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

            List<Vector3> best = new List<Vector3>(source);
            float bestLength = ComputePlanningClosedLength(best, nodes, cfg, stopLocator);

            for (int start = 0; start < source.Count; start++)
            {
                List<Vector3> candidate = BuildNearestRoadLoop(source, start, nodes, cfg, stopLocator);
                candidate = ImproveRoadLoopWithTwoOpt(candidate, nodes, cfg, stopLocator);
                float length = ComputePlanningClosedLength(candidate, nodes, cfg, stopLocator);
                if (length < bestLength)
                {
                    best = candidate;
                    bestLength = length;
                }
            }

            return best;
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

            DemandNode fromNode = nodes[fromIndex];
            DemandNode toNode = nodes[toIndex];
            float allowed = Mathf.Max(GetAllowedLinkDistance(fromNode, cfg), GetAllowedLinkDistance(toNode, cfg));
            float maxRoadDistance = allowed * 2.35f;
            float roadDistance;
            if (stopLocator.TryEstimateRoadDistance(fromNode.StopSegmentId, toNode.StopSegmentId, maxRoadDistance, out roadDistance))
            {
                distance = Mathf.Max(direct, roadDistance);
                return true;
            }

            float fallbackLimit = GetLocationFallbackDistanceLimit(fromNode, toNode, cfg);
            if (direct <= fallbackLimit)
            {
                LocationFallbackLinkCount++;
                distance = direct + GetLocationFallbackPenalty(direct, cfg);
                return true;
            }

            if (fromNode.StopSegmentId != 0 && toNode.StopSegmentId != 0)
            {
                RoadGraphRejectedLinkCount++;
                return false;
            }

            return direct <= Mathf.Min(directLimit, allowed * 0.8f);
        }

        private float GetLocationFallbackDistanceLimit(DemandNode fromNode, DemandNode toNode, AutoPublicTransitConfig cfg)
        {
            float limit = Mathf.Max(GetAllowedLinkDistance(fromNode, cfg), GetAllowedLinkDistance(toNode, cfg));
            if (!IsStrategicNode(fromNode) && !IsStrategicNode(toNode))
                limit = Mathf.Min(limit, cfg.MaxRoadDistance);

            return Mathf.Max(120f, limit);
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
