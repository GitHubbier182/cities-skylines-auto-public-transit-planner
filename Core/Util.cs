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
    public static class Util
    {
        public static BuildingManager BM => BuildingManager.instance;
        public static NetManager NM => NetManager.instance;

        public static bool IsValidBuilding(ushort id)
        {
            var buffer = BM.m_buildings.m_buffer;
            if (id == 0 || id >= buffer.Length)
                return false;

            return (buffer[id].m_flags & Building.Flags.Created) != 0;
        }

        public static Vector3 GetBuildingPosition(ushort id)
        {
            return BM.m_buildings.m_buffer[id].m_position;
        }

        public static bool TryGetNearestRoadNode(Vector3 pos, float maxDistance, out ushort nodeId, out Vector3 nodePos)
        {
            nodeId = 0;
            nodePos = Vector3.zero;

            NetManager nm = NM;
            float maxSqr = maxDistance * maxDistance;
            float bestSqr = maxSqr;

            for (ushort i = 1; i < nm.m_nodes.m_size; i++)
            {
                ref NetNode node = ref nm.m_nodes.m_buffer[i];
                if ((node.m_flags & NetNode.Flags.Created) == 0)
                    continue;

                if ((node.m_flags & NetNode.Flags.Untouchable) != 0)
                    continue;

                Vector3 p = node.m_position;
                float dx = p.x - pos.x;
                float dz = p.z - pos.z;
                float sqr = dx * dx + dz * dz;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    nodeId = i;
                    nodePos = p;
                }
            }

            return nodeId != 0;
        }

        public static bool TryGetNearestBusStopPosition(Vector3 pos, float maxDistance, bool avoidHighways, out Vector3 stopPos)
        {
            CachedStopMatch match;
            if (!TryGetNearestBusStopMatch(pos, maxDistance, avoidHighways, out match))
            {
                stopPos = Vector3.zero;
                return false;
            }

            stopPos = match.StopPosition;
            return true;
        }

        public static bool TryGetNearestBusStopMatch(Vector3 pos, float maxDistance, bool avoidHighways, out CachedStopMatch match)
        {
            match = new CachedStopMatch
            {
                BuildingPosition = pos,
                StopPosition = Vector3.zero,
                SegmentId = 0,
                RoadName = null
            };

            NetManager nm = NM;
            float maxSqr = maxDistance * maxDistance;
            float bestSqr = maxSqr;

            for (ushort segmentId = 1; segmentId < nm.m_segments.m_size; segmentId++)
            {
                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                    continue;

                NetInfo info = segment.Info;
                if (!IsBusStopRoad(info, avoidHighways))
                    continue;

                if (GetRoadSegmentSurfaceRejection(nm, ref segment) != null)
                    continue;

                Vector3 lanePos;
                uint laneId;
                int laneIndex;
                float laneOffset;
                if (!segment.GetClosestLanePosition(
                    pos,
                    NetInfo.LaneType.Vehicle,
                    VehicleInfo.VehicleType.Car,
                    VehicleInfo.VehicleCategory.PassengerCar,
                    out lanePos,
                    out laneId,
                    out laneIndex,
                    out laneOffset))
                {
                    continue;
                }

                ref NetLane lane = ref nm.m_lanes.m_buffer[laneId];
                NetInfo.Lane laneInfo = info.m_lanes[laneIndex];
                if (laneInfo.m_stopOffset <= 0.01f)
                    continue;

                Vector3 candidatePos;
                Vector3 direction;
                lane.CalculateStopPositionAndDirection(laneOffset, laneInfo.m_stopOffset, out candidatePos, out direction);

                float dx = candidatePos.x - pos.x;
                float dz = candidatePos.z - pos.z;
                float sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                match.StopPosition = candidatePos;
                match.SegmentId = segmentId;
                match.RoadName = GetNetInfoName(info);
            }

            return bestSqr < maxSqr && match.SegmentId != 0;
        }

        public static bool IsCachedBusStopStillValid(CachedStopMatch cached, bool avoidHighways)
        {
            if (cached.SegmentId == 0)
                return false;

            NetManager nm = NM;
            if (cached.SegmentId >= nm.m_segments.m_size)
                return false;

            ref NetSegment segment = ref nm.m_segments.m_buffer[cached.SegmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return false;

            NetInfo info = segment.Info;
            if (!IsBusStopRoad(info, avoidHighways))
                return false;

            if (GetRoadSegmentSurfaceRejection(nm, ref segment) != null)
                return false;

            string roadName = GetNetInfoName(info);
            if (!string.Equals(roadName, cached.RoadName, StringComparison.Ordinal))
                return false;

            Vector3 refreshed;
            if (!TryGetNearestBusStopPosition(cached.StopPosition, 48f, avoidHighways, out refreshed))
                return false;

            return Geometry.DistanceXZ(refreshed, cached.StopPosition) <= 48f;
        }

        public static bool IsValidBusStopPosition(Vector3 stopPos, float maxDistance, bool avoidHighways)
        {
            Vector3 refreshed;
            return TryGetNearestBusStopPosition(stopPos, maxDistance, avoidHighways, out refreshed)
                && Geometry.DistanceXZ(refreshed, stopPos) <= maxDistance;
        }

        public static bool IsBusStopRoad(NetInfo info, bool avoidHighways)
        {
            if (info == null || info.m_lanes == null)
                return false;

            RoadBaseAI roadAi = info.m_netAI as RoadBaseAI;
            if (roadAi == null)
                return false;

            if (avoidHighways && roadAi.m_highwayRules)
                return false;

            for (int i = 0; i < info.m_lanes.Length; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) == 0)
                    continue;

                if (lane.m_stopOffset > 0.01f)
                    return true;
            }

            return false;
        }

        public static string GetRoadSegmentSurfaceRejection(NetManager nm, ref NetSegment segment)
        {
            if ((segment.m_flags & NetSegment.Flags.Collapsed) != 0)
                return "collapsed";

            if (nm == null)
                return "no-net-manager";

            if (segment.m_startNode == 0 || segment.m_endNode == 0)
                return "missing-node";

            if (segment.m_startNode >= nm.m_nodes.m_size || segment.m_endNode >= nm.m_nodes.m_size)
                return "missing-node";

            NetNode.Flags startFlags = nm.m_nodes.m_buffer[segment.m_startNode].m_flags;
            NetNode.Flags endFlags = nm.m_nodes.m_buffer[segment.m_endNode].m_flags;

            if ((startFlags & NetNode.Flags.Created) == 0 || (endFlags & NetNode.Flags.Created) == 0)
                return "missing-node";

            if ((startFlags & NetNode.Flags.Underground) != 0 || (endFlags & NetNode.Flags.Underground) != 0)
                return "underground";

            return null;
        }

        public static bool TryGetNearestRoadInfo(Vector3 pos, float maxDistance, out NetInfo info)
        {
            info = null;
            ushort segmentId;
            return TryGetNearestRoadSegment(pos, maxDistance, out segmentId, out info);
        }

        public static bool TryGetNearestRoadSegment(Vector3 pos, float maxDistance, out ushort segmentId, out NetInfo info)
        {
            segmentId = 0;
            info = null;

            NetManager nm = NM;
            float maxSqr = maxDistance * maxDistance;
            float bestSqr = maxSqr;

            for (ushort i = 1; i < nm.m_segments.m_size; i++)
            {
                ref NetSegment segment = ref nm.m_segments.m_buffer[i];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                    continue;

                Vector3 a = nm.m_nodes.m_buffer[segment.m_startNode].m_position;
                Vector3 b = nm.m_nodes.m_buffer[segment.m_endNode].m_position;
                Vector3 nearest = Geometry.ClosestPointOnSegmentXZ(a, b, pos);
                float sqr = (nearest - pos).sqrMagnitude;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                segmentId = i;
                info = segment.Info;
            }

            return segmentId != 0 && info != null;
        }

        public static string GetNetInfoName(NetInfo info)
        {
            if (info == null)
                return null;

            if (!string.IsNullOrEmpty(info.name))
                return info.name;

            return info.GetUncheckedLocalizedTitle();
        }
    }
}
