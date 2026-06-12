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
        private List<BusLaneUpgradeRecommendation> BuildBusLaneUpgradeRecommendations(List<ExistingLineSnapshot> busLines, AutoPublicTransitConfig cfg)
        {
            var recommendations = new List<BusLaneUpgradeRecommendation>();
            if (!cfg.EnableBusLaneRecommendations)
            {
                TransitLogging.Log("Bus-lane upgrade recommendations are disabled.");
                return recommendations;
            }

            var busLineCountsBySegment = CountBusLinesByRoadSegment(busLines);
            NetManager nm = NetManager.instance;

            for (ushort segmentId = 1; segmentId < nm.m_segments.m_size; segmentId++)
            {
                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                    continue;

                int busLineCount = 0;
                HashSet<ushort> lineIds;
                if (busLineCountsBySegment.TryGetValue(segmentId, out lineIds))
                    busLineCount = lineIds.Count;

                int trafficDensity = segment.m_trafficDensity;
                if (busLineCount < cfg.BusLaneRouteThreshold && trafficDensity < cfg.BusLaneTrafficDensityThreshold)
                    continue;

                NetInfo currentInfo = segment.Info;
                if (!IsBusLaneRecommendationCandidate(segmentId, ref segment, currentInfo, cfg))
                    continue;

                NetInfo recommendedInfo = FindBusLaneUpgradeTarget(currentInfo);
                if (recommendedInfo == null)
                {
                    TransitLogging.Log("Bus-lane recommendation skipped segment " + segmentId + ": no compatible bus-lane road found for " + GetNetInfoName(currentInfo) + ".");
                    continue;
                }

                bool routeHit = busLineCount >= cfg.BusLaneRouteThreshold;
                bool trafficHit = trafficDensity >= cfg.BusLaneTrafficDensityThreshold;
                if (!routeHit)
                    continue;

                string reason = BuildBusLaneRecommendationReason(routeHit, trafficHit, busLineCount, trafficDensity);
                int score = ComputeBusLaneRecommendationScore(busLineCount, trafficDensity, segment.m_averageLength, routeHit, trafficHit);

                recommendations.Add(new BusLaneUpgradeRecommendation
                {
                    SegmentId = segmentId,
                    CurrentRoadName = GetNetInfoName(currentInfo),
                    RecommendedRoadName = GetNetInfoName(recommendedInfo),
                    Score = score,
                    BusLineCount = busLineCount,
                    TrafficDensity = trafficDensity,
                    Length = segment.m_averageLength,
                    Position = GetSegmentMidpoint(ref segment),
                    Reason = reason
                });
            }

            recommendations.Sort((a, b) => b.Score.CompareTo(a.Score));
            int maxRecommendations = Mathf.Max(0, cfg.MaxBusLaneRecommendations);
            if (recommendations.Count > maxRecommendations)
                recommendations.RemoveRange(maxRecommendations, recommendations.Count - maxRecommendations);

            LogBusLaneRecommendations(recommendations);
            return recommendations;
        }

        private Dictionary<ushort, HashSet<ushort>> CountBusLinesByRoadSegment(List<ExistingLineSnapshot> busLines)
        {
            var result = new Dictionary<ushort, HashSet<ushort>>();
            if (busLines == null || busLines.Count == 0)
                return result;

            for (int i = 0; i < busLines.Count; i++)
            {
                ExistingLineSnapshot line = busLines[i];
                if (line == null || line.Stops == null || line.Stops.Count < 2)
                    continue;

                for (int j = 0; j < line.Stops.Count; j++)
                {
                    Vector3 from = line.Stops[j];
                    Vector3 to = line.Stops[(j + 1) % line.Stops.Count];
                    SampleLineLegSegments(from, to, line.LineId, result);
                }
            }

            return result;
        }

        private void SampleLineLegSegments(Vector3 from, Vector3 to, ushort lineId, Dictionary<ushort, HashSet<ushort>> result)
        {
            float distance = Geometry.DistanceXZ(from, to);
            int samples = Mathf.Clamp(Mathf.CeilToInt(distance / 90f), 2, 18);

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                Vector3 sample = Vector3.Lerp(from, to, t);
                ushort segmentId;
                NetInfo info;
                if (!Util.TryGetNearestRoadSegment(sample, 80f, out segmentId, out info))
                    continue;

                HashSet<ushort> lineIds;
                if (!result.TryGetValue(segmentId, out lineIds))
                {
                    lineIds = new HashSet<ushort>();
                    result[segmentId] = lineIds;
                }

                lineIds.Add(lineId);
            }
        }

        private bool IsBusLaneRecommendationCandidate(ushort segmentId, ref NetSegment segment, NetInfo info, AutoPublicTransitConfig cfg)
        {
            if (info == null || info.m_netAI == null)
                return false;

            RoadBaseAI roadAi = info.m_netAI as RoadBaseAI;
            if (roadAi == null)
                return false;

            if (cfg.AvoidHighways && roadAi.m_highwayRules)
                return false;

            if ((segment.m_flags & NetSegment.Flags.Collapsed) != 0)
                return false;

            NetManager nm = NetManager.instance;
            NetNode.Flags startFlags = nm.m_nodes.m_buffer[segment.m_startNode].m_flags;
            NetNode.Flags endFlags = nm.m_nodes.m_buffer[segment.m_endNode].m_flags;
            if ((startFlags & NetNode.Flags.Untouchable) != 0 || (endFlags & NetNode.Flags.Untouchable) != 0)
                return false;

            if ((startFlags & NetNode.Flags.Outside) != 0 || (endFlags & NetNode.Flags.Outside) != 0)
                return false;

            if ((startFlags & NetNode.Flags.Underground) != 0 || (endFlags & NetNode.Flags.Underground) != 0)
                return false;

            if (HasBusLane(info))
                return false;

            return true;
        }

        private NetInfo FindBusLaneUpgradeTarget(NetInfo currentInfo)
        {
            if (currentInfo == null || currentInfo.m_netAI == null)
                return null;

            RoadBaseAI currentRoadAi = currentInfo.m_netAI as RoadBaseAI;
            if (currentRoadAi == null)
                return null;

            NetInfo best = null;
            float bestScore = float.MaxValue;
            int loaded = PrefabCollection<NetInfo>.LoadedCount();

            for (uint i = 0; i < loaded; i++)
            {
                NetInfo candidate = PrefabCollection<NetInfo>.GetPrefab(i);
                if (candidate == null || candidate == currentInfo || candidate.m_netAI == null)
                    continue;

                RoadBaseAI candidateRoadAi = candidate.m_netAI as RoadBaseAI;
                if (candidateRoadAi == null)
                    continue;

                if (!HasBusLane(candidate))
                    continue;

                if (!IsBroadlyCompatibleRoad(currentInfo, candidate))
                    continue;

                if (!CanRoadUpgradeTo(currentRoadAi, candidate))
                    continue;

                float score = Mathf.Abs(candidate.m_halfWidth - currentInfo.m_halfWidth) * 10f;
                score += Mathf.Abs(candidate.m_forwardVehicleLaneCount - currentInfo.m_forwardVehicleLaneCount) * 18f;
                score += Mathf.Abs(candidate.m_backwardVehicleLaneCount - currentInfo.m_backwardVehicleLaneCount) * 18f;

                if (candidateRoadAi.m_highwayRules != currentRoadAi.m_highwayRules)
                    score += 500f;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = candidate;
            }

            return best;
        }

        private bool CanRoadUpgradeTo(RoadBaseAI currentRoadAi, NetInfo candidate)
        {
            try
            {
                return currentRoadAi.CanUpgradeTo(candidate);
            }
            catch
            {
                return true;
            }
        }

        private bool IsBroadlyCompatibleRoad(NetInfo currentInfo, NetInfo candidate)
        {
            if (currentInfo.m_class == null || candidate.m_class == null)
                return false;

            if (currentInfo.m_class.m_service != candidate.m_class.m_service)
                return false;

            if (currentInfo.m_class.m_subService != candidate.m_class.m_subService)
                return false;

            if (Mathf.Abs(candidate.m_halfWidth - currentInfo.m_halfWidth) > 12f)
                return false;

            bool currentOneWay = currentInfo.m_hasForwardVehicleLanes != currentInfo.m_hasBackwardVehicleLanes;
            bool candidateOneWay = candidate.m_hasForwardVehicleLanes != candidate.m_hasBackwardVehicleLanes;
            if (currentOneWay != candidateOneWay)
                return false;

            return true;
        }

        private bool HasBusLane(NetInfo info)
        {
            if (info == null || info.m_lanes == null)
                return false;

            string name = GetNetInfoName(info).ToLowerInvariant();
            if (HasBusLaneRoadName(name))
                return true;

            for (int i = 0; i < info.m_lanes.Length; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if ((lane.m_laneType & NetInfo.LaneType.PublicTransport) != 0)
                    return true;
            }

            return false;
        }

        private bool HasBusLaneRoadName(string roadName)
        {
            if (string.IsNullOrEmpty(roadName))
                return false;

            if (roadName.Contains("bus depot"))
                return false;

            string compact = roadName.Replace(" ", "").Replace("-", "");
            return roadName.Contains("bus lane")
                || roadName.Contains("bus lanes")
                || roadName.Contains("bus only")
                || compact.Contains("buslane")
                || compact.Contains("buslanes")
                || compact.Contains("busonly");
        }

        private int ComputeBusLaneRecommendationScore(int busLineCount, int trafficDensity, float length, bool routeHit, bool trafficHit)
        {
            float score = busLineCount * 140f + trafficDensity * 2f + Mathf.Min(length, 800f) * 0.08f;
            if (routeHit && trafficHit)
                score += 240f;

            return Mathf.RoundToInt(score);
        }

        private string BuildBusLaneRecommendationReason(bool routeHit, bool trafficHit, int busLineCount, int trafficDensity)
        {
            if (routeHit && trafficHit)
                return busLineCount + " bus routes + high traffic (" + trafficDensity + ")";

            if (routeHit)
                return busLineCount + " bus routes";

            return busLineCount + " bus routes";
        }

        private Vector3 GetSegmentMidpoint(ref NetSegment segment)
        {
            NetManager nm = NetManager.instance;
            Vector3 start = nm.m_nodes.m_buffer[segment.m_startNode].m_position;
            Vector3 end = nm.m_nodes.m_buffer[segment.m_endNode].m_position;
            return Vector3.Lerp(start, end, 0.5f);
        }

        private string GetNetInfoName(NetInfo info)
        {
            if (info == null)
                return "(unknown)";

            if (!string.IsNullOrEmpty(info.name))
                return info.name;

            return info.GetUncheckedLocalizedTitle();
        }

        private void LogBusLaneRecommendations(List<BusLaneUpgradeRecommendation> recommendations)
        {
            if (recommendations == null || recommendations.Count == 0)
            {
                TransitLogging.Log("Bus-lane upgrade recommendations: 0.");
                return;
            }

            TransitLogging.Log("Bus-lane upgrade recommendations: " + recommendations.Count + ".");
            for (int i = 0; i < recommendations.Count; i++)
            {
                BusLaneUpgradeRecommendation r = recommendations[i];
                TransitLogging.Log(
                    "Bus-lane candidate " + (i + 1) +
                    ": segment " + r.SegmentId +
                    ", " + r.CurrentRoadName +
                    " -> " + r.RecommendedRoadName +
                    ", busLines=" + r.BusLineCount +
                    ", traffic=" + r.TrafficDensity +
                    ", score=" + r.Score +
                    ", reason=" + r.Reason + ".");
            }
        }
    }
}
