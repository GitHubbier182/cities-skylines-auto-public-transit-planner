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
    public partial class Manager : MonoBehaviour
    {
        private const int DepotAdvisoryLinesPerDepot = 12;
        private const int DepotAdvisoryMaxSuggestions = 4;
        private const float DepotAdvisoryClusterRadius = 1400f;
        private const float DepotAdvisoryStopRadius = 900f;
        private const float DepotAdvisoryExistingCoverageDistance = 1600f;
        private const float DepotAdvisoryMinSuggestionSpacing = 1100f;
        private const float DepotAdvisoryRoadSnapDistance = 1200f;

        private class DepotLineCluster
        {
            public Vector3 Position;
            public int StopCount;
        }

        private class DepotClusterCandidate
        {
            public Vector3 Position;
            public string RoadName;
            public int NearbyLineCount;
            public int NearbyStopCount;
            public float AverageLineDistance;
            public float DistanceToNearestDepot;
            public float Score;
            public string Reason;
        }

        private void EnsureBusDepotCoverage(List<DemandNode> nodes)
        {
            TransportManager tm = TransportManager.instance;
            TransportInfo busInfo = tm.GetTransportInfo(TransportInfo.TransportType.Bus);
            if (busInfo == null)
            {
                TransitLogging.Warn("Could not check bus depots because bus transport info was unavailable.");
                return;
            }

            int depotCount = CountBusDepots();

            TransitLogging.Log("Bus depots found: " + depotCount + ". Extra depot guidance is handled by the bus economics advisory.");

            if (depotCount > 0)
            {
                TransitLogging.Log("Existing bus depots will be reused; no extra depot is placed during this scan.");
                return;
            }

            TransitLogging.Warn("No bus depots were found. APT will continue the scan and show depot placement guidance instead of placing a depot automatically.");
        }

        private int CountBusDepots()
        {
            return CollectBusDepotPositions().Count;
        }

        private List<DepotPlacementRecommendation> BuildDepotPlacementRecommendations(
            List<ExistingLineSnapshot> existingLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            BusEconomicsSummary economics)
        {
            var recommendations = new List<DepotPlacementRecommendation>();
            List<DepotLineCluster> lineClusters = CollectDepotLineClusters(existingLines);
            if (lineClusters.Count == 0)
                return recommendations;

            List<Vector3> depotPositions = CollectBusDepotPositions();
            int currentDepotCount = depotPositions.Count;
            int recommendedDepotCount = economics != null && economics.RecommendedDepotCount > 0
                ? economics.RecommendedDepotCount
                : CalculateRecommendedDepotCount(lineClusters.Count);
            bool hasDistantCluster = HasDistantLineCluster(lineClusters, depotPositions);
            int desiredSuggestions = Mathf.Clamp(recommendedDepotCount - currentDepotCount, 0, DepotAdvisoryMaxSuggestions);
            if (desiredSuggestions <= 0 && hasDistantCluster)
                desiredSuggestions = 1;

            if (desiredSuggestions <= 0)
                return recommendations;

            List<DepotClusterCandidate> candidates = BuildDepotClusterCandidates(existingLines, lineClusters, depotPositions, cfg, stopLocator, currentDepotCount, recommendedDepotCount);
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            for (int i = 0; i < candidates.Count && recommendations.Count < desiredSuggestions; i++)
            {
                DepotClusterCandidate candidate = candidates[i];
                if (IsTooCloseToSelectedDepotRecommendation(candidate.Position, recommendations))
                    continue;

                recommendations.Add(new DepotPlacementRecommendation
                {
                    Rank = recommendations.Count + 1,
                    Position = candidate.Position,
                    RoadName = candidate.RoadName,
                    NearbyLineCount = candidate.NearbyLineCount,
                    NearbyStopCount = candidate.NearbyStopCount,
                    AverageLineDistance = candidate.AverageLineDistance,
                    DistanceToNearestDepot = candidate.DistanceToNearestDepot,
                    Reason = candidate.Reason
                });
            }

            return recommendations;
        }

        private List<DepotLineCluster> CollectDepotLineClusters(List<ExistingLineSnapshot> existingLines)
        {
            var clusters = new List<DepotLineCluster>();
            if (existingLines == null)
                return clusters;

            for (int i = 0; i < existingLines.Count; i++)
            {
                ExistingLineSnapshot line = existingLines[i];
                if (line == null || line.IsProtectedFromAptManagement || line.Stops == null || line.Stops.Count < 2)
                    continue;

                Vector3 center = Vector3.zero;
                for (int stopIndex = 0; stopIndex < line.Stops.Count; stopIndex++)
                    center += line.Stops[stopIndex];

                center /= line.Stops.Count;
                clusters.Add(new DepotLineCluster
                {
                    Position = center,
                    StopCount = line.Stops.Count
                });
            }

            return clusters;
        }

        private List<DepotClusterCandidate> BuildDepotClusterCandidates(
            List<ExistingLineSnapshot> existingLines,
            List<DepotLineCluster> lineClusters,
            List<Vector3> depotPositions,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            int currentDepotCount,
            int recommendedDepotCount)
        {
            var candidates = new List<DepotClusterCandidate>();
            float snapDistance = Mathf.Max(DepotAdvisoryRoadSnapDistance, cfg != null ? cfg.MaxRoadDistance * 1.5f : DepotAdvisoryRoadSnapDistance);

            for (int i = 0; i < lineClusters.Count; i++)
            {
                DepotLineCluster cluster = lineClusters[i];
                Vector3 position = cluster.Position;
                string roadName = null;

                CachedStopMatch match;
                if (stopLocator != null && stopLocator.TryGetNearestBusStopMatch(cluster.Position, snapDistance, out match))
                {
                    position = match.StopPosition;
                    roadName = match.RoadName;
                }

                int nearbyLines;
                float averageLineDistance;
                CountNearbyLineClusters(lineClusters, position, out nearbyLines, out averageLineDistance);
                if (nearbyLines < 2 && currentDepotCount > 0)
                    continue;

                int nearbyStops = CountNearbyStops(existingLines, position);
                float nearestDepotDistance = GetNearestDepotDistance(position, depotPositions);
                if (currentDepotCount >= recommendedDepotCount && nearestDepotDistance < DepotAdvisoryExistingCoverageDistance)
                    continue;

                string reason = BuildDepotPlacementReason(currentDepotCount, recommendedDepotCount, nearestDepotDistance);
                float depotDistanceScore = float.IsPositiveInfinity(nearestDepotDistance) ? DepotAdvisoryExistingCoverageDistance : nearestDepotDistance;
                float score = nearbyLines * 1000f + nearbyStops * 35f + depotDistanceScore * 0.35f - averageLineDistance * 0.25f;
                candidates.Add(new DepotClusterCandidate
                {
                    Position = position,
                    RoadName = roadName,
                    NearbyLineCount = nearbyLines,
                    NearbyStopCount = nearbyStops,
                    AverageLineDistance = averageLineDistance,
                    DistanceToNearestDepot = nearestDepotDistance,
                    Score = score,
                    Reason = reason
                });
            }

            return candidates;
        }

        private int CalculateRecommendedDepotCount(int completeLineCount)
        {
            if (completeLineCount <= 0)
                return 0;

            return Mathf.Max(1, Mathf.CeilToInt(completeLineCount / (float)DepotAdvisoryLinesPerDepot));
        }

        private bool HasDistantLineCluster(List<DepotLineCluster> lineClusters, List<Vector3> depotPositions)
        {
            if (lineClusters == null || lineClusters.Count < 8)
                return false;

            if (depotPositions == null || depotPositions.Count == 0)
                return true;

            int distantClusters = 0;
            for (int i = 0; i < lineClusters.Count; i++)
            {
                if (GetNearestDepotDistance(lineClusters[i].Position, depotPositions) > DepotAdvisoryExistingCoverageDistance)
                    distantClusters++;
            }

            return distantClusters >= Mathf.Max(3, lineClusters.Count / 4);
        }

        private void CountNearbyLineClusters(List<DepotLineCluster> lineClusters, Vector3 position, out int nearbyLines, out float averageDistance)
        {
            nearbyLines = 0;
            float distanceTotal = 0f;

            for (int i = 0; i < lineClusters.Count; i++)
            {
                float distance = Geometry.DistanceXZ(position, lineClusters[i].Position);
                if (distance > DepotAdvisoryClusterRadius)
                    continue;

                nearbyLines++;
                distanceTotal += distance;
            }

            averageDistance = nearbyLines > 0 ? distanceTotal / nearbyLines : 0f;
        }

        private int CountNearbyStops(List<ExistingLineSnapshot> existingLines, Vector3 position)
        {
            if (existingLines == null)
                return 0;

            int count = 0;
            for (int i = 0; i < existingLines.Count; i++)
            {
                ExistingLineSnapshot line = existingLines[i];
                if (line == null || line.IsProtectedFromAptManagement || line.Stops == null)
                    continue;

                for (int stopIndex = 0; stopIndex < line.Stops.Count; stopIndex++)
                {
                    if (Geometry.DistanceXZ(position, line.Stops[stopIndex]) <= DepotAdvisoryStopRadius)
                        count++;
                }
            }

            return count;
        }

        private float GetNearestDepotDistance(Vector3 position, List<Vector3> depotPositions)
        {
            if (depotPositions == null || depotPositions.Count == 0)
                return float.PositiveInfinity;

            float best = float.MaxValue;
            for (int i = 0; i < depotPositions.Count; i++)
                best = Mathf.Min(best, Geometry.DistanceXZ(position, depotPositions[i]));

            return best;
        }

        private bool IsTooCloseToSelectedDepotRecommendation(Vector3 position, List<DepotPlacementRecommendation> selected)
        {
            for (int i = 0; i < selected.Count; i++)
            {
                if (Geometry.DistanceXZ(position, selected[i].Position) < DepotAdvisoryMinSuggestionSpacing)
                    return true;
            }

            return false;
        }

        private string BuildDepotPlacementReason(int currentDepotCount, int recommendedDepotCount, float nearestDepotDistance)
        {
            if (currentDepotCount <= 0)
                return "No bus depot is currently available for the scanned network.";

            if (currentDepotCount < recommendedDepotCount)
                return "The scanned network is larger than current depot coverage.";

            if (nearestDepotDistance > DepotAdvisoryExistingCoverageDistance)
                return "This line cluster is far from the nearest existing depot.";

            return "This is the strongest central dispatch point in the scanned layout.";
        }

        private List<Vector3> CollectBusDepotPositions()
        {
            BuildingManager bm = BuildingManager.instance;
            var depotPositions = new List<Vector3>();

            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null || building.Info.m_class == null)
                    continue;

                if (building.Info.m_class.m_service != ItemClass.Service.PublicTransport)
                    continue;

                if (building.Info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                    continue;

                if (building.Info.m_buildingAI is DepotAI)
                    depotPositions.Add(building.m_position);
            }

            return depotPositions;
        }

        private void LogDepotPlacementRecommendations(List<DepotPlacementRecommendation> recommendations, BusEconomicsSummary economics)
        {
            int count = recommendations != null ? recommendations.Count : 0;
            TransitLogging.Log(
                "Depot placement advisory: currentDepots=" + (economics != null ? economics.DepotCount : CountBusDepots()) +
                ", completeLines=" + (economics != null ? economics.CompleteLineCount : 0) +
                ", recommendedDepots=" + (economics != null ? economics.RecommendedDepotCount : 0) +
                ", suggestions=" + count + ".");

            if (recommendations == null)
                return;

            for (int i = 0; i < recommendations.Count; i++)
            {
                DepotPlacementRecommendation recommendation = recommendations[i];
                string nearestDepot = float.IsPositiveInfinity(recommendation.DistanceToNearestDepot)
                    ? "none"
                    : recommendation.DistanceToNearestDepot.ToString("0", CultureInfo.InvariantCulture) + "m";
                string road = string.IsNullOrEmpty(recommendation.RoadName) ? "unnamed road area" : recommendation.RoadName;
                TransitLogging.Log(
                    "Depot placement suggestion #" + recommendation.Rank +
                    ": road=" + road +
                    ", pos=(" + recommendation.Position.x.ToString("0", CultureInfo.InvariantCulture) +
                    "," + recommendation.Position.z.ToString("0", CultureInfo.InvariantCulture) + ")" +
                    ", nearbyLines=" + recommendation.NearbyLineCount +
                    ", nearbyStops=" + recommendation.NearbyStopCount +
                    ", averageLineDistance=" + recommendation.AverageLineDistance.ToString("0", CultureInfo.InvariantCulture) + "m" +
                    ", nearestDepot=" + nearestDepot +
                    ", reason=" + recommendation.Reason);
            }
        }
    }
}
