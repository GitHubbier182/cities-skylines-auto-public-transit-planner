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
    public class BusStopLocator
    {
        private readonly bool _avoidHighways;
        private readonly float _cellSize;
        private readonly Dictionary<long, List<ushort>> _segmentsByCell = new Dictionary<long, List<ushort>>();
        private readonly Dictionary<ushort, List<ushort>> _segmentsByNode = new Dictionary<ushort, List<ushort>>();
        private readonly Dictionary<ushort, List<ushort>> _neighborsBySegment = new Dictionary<ushort, List<ushort>>();
        private readonly Dictionary<ushort, List<DirectedNodeEdge>> _outgoingEdgesByNode = new Dictionary<ushort, List<DirectedNodeEdge>>();
        private readonly Dictionary<ushort, SegmentTravelInfo> _travelBySegment = new Dictionary<ushort, SegmentTravelInfo>();
        private readonly List<ushort> _indexedSegmentIds = new List<ushort>();

        public int IndexedSegments { get; private set; }
        public int RejectedUndergroundSegments { get; private set; }
        public int RejectedCollapsedSegments { get; private set; }
        public int RejectedMissingNodeSegments { get; private set; }

        private struct DirectedNodeEdge
        {
            public ushort ToNodeId;
            public ushort SegmentId;
            public float Cost;
        }

        private struct SegmentTravelInfo
        {
            public ushort StartNodeId;
            public ushort EndNodeId;
            public bool Forward;
            public bool Backward;
            public float Cost;
        }

        public BusStopLocator(bool avoidHighways, float cellSize)
        {
            _avoidHighways = avoidHighways;
            _cellSize = Mathf.Max(128f, cellSize);
            BuildIndex();
        }

        public bool TryGetNearestBusStopPosition(Vector3 pos, float maxDistance, out Vector3 stopPos)
        {
            CachedStopMatch match;
            if (!TryGetNearestBusStopMatch(pos, maxDistance, out match))
            {
                stopPos = Vector3.zero;
                return false;
            }

            stopPos = match.StopPosition;
            return true;
        }

        public bool TryGetNearestBusStopMatch(Vector3 pos, float maxDistance, out CachedStopMatch match)
        {
            match = new CachedStopMatch
            {
                BuildingPosition = pos,
                StopPosition = Vector3.zero,
                SegmentId = 0,
                RoadName = null
            };

            if (_segmentsByCell.Count == 0)
                return false;

            NetManager nm = NetManager.instance;
            float maxSqr = maxDistance * maxDistance;
            float bestSqr = maxSqr;
            float bestScore = float.MaxValue;
            int minX = Mathf.FloorToInt((pos.x - maxDistance) / _cellSize);
            int maxX = Mathf.FloorToInt((pos.x + maxDistance) / _cellSize);
            int minZ = Mathf.FloorToInt((pos.z - maxDistance) / _cellSize);
            int maxZ = Mathf.FloorToInt((pos.z + maxDistance) / _cellSize);
            var checkedSegments = new HashSet<ushort>();

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    List<ushort> segmentIds;
                    if (!_segmentsByCell.TryGetValue(Key(x, z), out segmentIds))
                        continue;

                    for (int i = 0; i < segmentIds.Count; i++)
                    {
                        ushort segmentId = segmentIds[i];
                        if (!checkedSegments.Add(segmentId))
                            continue;

                        CachedStopMatch candidateMatch;
                        float candidateSqr;
                        float candidateScore;
                        if (!TryGetStopMatchOnSegment(nm, segmentId, pos, maxSqr, out candidateMatch, out candidateSqr, out candidateScore))
                            continue;

                        if (candidateScore >= bestScore)
                            continue;

                        bestSqr = candidateSqr;
                        bestScore = candidateScore;
                        match = candidateMatch;
                    }
                }
            }

            return bestSqr < maxSqr && match.SegmentId != 0;
        }

        public bool IsCachedBusStopStillValid(CachedStopMatch cached)
        {
            if (cached.SegmentId == 0)
                return false;

            NetManager nm = NetManager.instance;
            if (cached.SegmentId >= nm.m_segments.m_size)
                return false;

            ref NetSegment segment = ref nm.m_segments.m_buffer[cached.SegmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return false;

            NetInfo info = segment.Info;
            if (!Util.IsBusStopRoad(info, _avoidHighways))
                return false;

            if (!IsSurfaceStopSegment(nm, ref segment, false))
                return false;

            string roadName = Util.GetNetInfoName(info);
            if (!string.Equals(roadName, cached.RoadName, StringComparison.Ordinal))
                return false;

            Vector3 refreshed;
            if (!TryGetNearestBusStopPosition(cached.StopPosition, 48f, out refreshed))
                return false;

            return Geometry.DistanceXZ(refreshed, cached.StopPosition) <= 48f;
        }

        public bool IsValidBusStopPosition(Vector3 stopPos, float maxDistance)
        {
            Vector3 refreshed;
            return TryGetNearestBusStopPosition(stopPos, maxDistance, out refreshed)
                && Geometry.DistanceXZ(refreshed, stopPos) <= maxDistance;
        }

        public bool TryEstimateRoadDistance(ushort fromSegmentId, ushort toSegmentId, float maxDistance, out float distance)
        {
            distance = 0f;
            if (fromSegmentId == 0 || toSegmentId == 0)
                return false;

            SegmentTravelInfo fromInfo;
            SegmentTravelInfo toInfo;
            if (!_travelBySegment.TryGetValue(fromSegmentId, out fromInfo) ||
                !_travelBySegment.TryGetValue(toSegmentId, out toInfo))
                return false;

            if (fromSegmentId == toSegmentId)
            {
                distance = Mathf.Max(24f, fromInfo.Cost * 0.5f);
                return true;
            }

            var targetNodes = new HashSet<ushort>();
            if (toInfo.Forward)
                targetNodes.Add(toInfo.StartNodeId);
            if (toInfo.Backward)
                targetNodes.Add(toInfo.EndNodeId);

            if (targetNodes.Count == 0)
                return false;

            var distances = new Dictionary<ushort, float>();
            var visited = new HashSet<ushort>();
            float exitCost = Mathf.Max(12f, fromInfo.Cost * 0.5f);
            if (fromInfo.Forward)
                AddCandidateNodeDistance(distances, fromInfo.EndNodeId, exitCost);
            if (fromInfo.Backward)
                AddCandidateNodeDistance(distances, fromInfo.StartNodeId, exitCost);

            if (distances.Count == 0)
                return false;

            float entryCost = Mathf.Max(12f, toInfo.Cost * 0.5f);

            while (distances.Count > visited.Count)
            {
                ushort current = 0;
                float currentDistance = float.MaxValue;
                foreach (var kvp in distances)
                {
                    if (visited.Contains(kvp.Key) || kvp.Value >= currentDistance)
                        continue;

                    current = kvp.Key;
                    currentDistance = kvp.Value;
                }

                if (current == 0 || currentDistance > maxDistance)
                    break;

                if (targetNodes.Contains(current))
                {
                    float totalDistance = currentDistance + entryCost;
                    if (totalDistance <= maxDistance)
                    {
                        distance = totalDistance;
                        return true;
                    }

                    break;
                }

                visited.Add(current);
                List<DirectedNodeEdge> edges;
                if (!_outgoingEdgesByNode.TryGetValue(current, out edges))
                    continue;

                for (int i = 0; i < edges.Count; i++)
                {
                    DirectedNodeEdge edge = edges[i];
                    ushort neighbor = edge.ToNodeId;
                    if (visited.Contains(neighbor))
                        continue;

                    float nextDistance = currentDistance + edge.Cost;
                    if (nextDistance > maxDistance)
                        continue;

                    float previous;
                    if (distances.TryGetValue(neighbor, out previous) && previous <= nextDistance)
                        continue;

                    distances[neighbor] = nextDistance;
                }
            }

            return false;
        }

        private void BuildIndex()
        {
            NetManager nm = NetManager.instance;
            for (ushort segmentId = 1; segmentId < nm.m_segments.m_size; segmentId++)
            {
                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                    continue;

                if (!Util.IsBusStopRoad(segment.Info, _avoidHighways))
                    continue;

                if (!IsSurfaceStopSegment(nm, ref segment, true))
                    continue;

                IndexSegment(nm, segmentId, ref segment);
                RegisterSegmentNodes(segmentId, ref segment);
                RegisterSegmentTravel(nm, segmentId, ref segment);
                _indexedSegmentIds.Add(segmentId);
                IndexedSegments++;
            }

            BuildSegmentNeighbors();
        }

        private void IndexSegment(NetManager nm, ushort segmentId, ref NetSegment segment)
        {
            Vector3 start = nm.m_nodes.m_buffer[segment.m_startNode].m_position;
            Vector3 end = nm.m_nodes.m_buffer[segment.m_endNode].m_position;
            Vector3 middle = segment.m_middlePosition;
            float padding = 64f;
            float minX = Mathf.Min(start.x, Mathf.Min(end.x, middle.x)) - padding;
            float maxX = Mathf.Max(start.x, Mathf.Max(end.x, middle.x)) + padding;
            float minZ = Mathf.Min(start.z, Mathf.Min(end.z, middle.z)) - padding;
            float maxZ = Mathf.Max(start.z, Mathf.Max(end.z, middle.z)) + padding;

            int cellMinX = Mathf.FloorToInt(minX / _cellSize);
            int cellMaxX = Mathf.FloorToInt(maxX / _cellSize);
            int cellMinZ = Mathf.FloorToInt(minZ / _cellSize);
            int cellMaxZ = Mathf.FloorToInt(maxZ / _cellSize);

            for (int x = cellMinX; x <= cellMaxX; x++)
            {
                for (int z = cellMinZ; z <= cellMaxZ; z++)
                {
                    long key = Key(x, z);
                    List<ushort> segmentIds;
                    if (!_segmentsByCell.TryGetValue(key, out segmentIds))
                    {
                        segmentIds = new List<ushort>();
                        _segmentsByCell[key] = segmentIds;
                    }

                    segmentIds.Add(segmentId);
                }
            }
        }

        private void RegisterSegmentNodes(ushort segmentId, ref NetSegment segment)
        {
            AddSegmentToNode(segment.m_startNode, segmentId);
            AddSegmentToNode(segment.m_endNode, segmentId);
        }

        private void AddSegmentToNode(ushort nodeId, ushort segmentId)
        {
            if (nodeId == 0)
                return;

            List<ushort> segments;
            if (!_segmentsByNode.TryGetValue(nodeId, out segments))
            {
                segments = new List<ushort>();
                _segmentsByNode[nodeId] = segments;
            }

            segments.Add(segmentId);
        }

        private void BuildSegmentNeighbors()
        {
            for (int i = 0; i < _indexedSegmentIds.Count; i++)
                _neighborsBySegment[_indexedSegmentIds[i]] = new List<ushort>();

            foreach (var kvp in _segmentsByNode)
            {
                List<ushort> segments = kvp.Value;
                for (int i = 0; i < segments.Count; i++)
                {
                    List<ushort> neighbors = _neighborsBySegment[segments[i]];
                    for (int j = 0; j < segments.Count; j++)
                    {
                        if (i == j)
                            continue;

                        ushort neighbor = segments[j];
                        if (!neighbors.Contains(neighbor))
                            neighbors.Add(neighbor);
                    }
                }
            }
        }

        private void RegisterSegmentTravel(NetManager nm, ushort segmentId, ref NetSegment segment)
        {
            bool forward;
            bool backward;
            GetVehicleTravelDirections(segment.Info, out forward, out backward);
            if (!forward && !backward)
                return;

            float cost = GetSegmentTraversalCost(nm, segmentId);
            var travel = new SegmentTravelInfo
            {
                StartNodeId = segment.m_startNode,
                EndNodeId = segment.m_endNode,
                Forward = forward,
                Backward = backward,
                Cost = cost
            };
            _travelBySegment[segmentId] = travel;

            if (forward)
                AddOutgoingEdge(segment.m_startNode, segment.m_endNode, segmentId, cost);
            if (backward)
                AddOutgoingEdge(segment.m_endNode, segment.m_startNode, segmentId, cost);
        }

        private void AddOutgoingEdge(ushort fromNodeId, ushort toNodeId, ushort segmentId, float cost)
        {
            if (fromNodeId == 0 || toNodeId == 0)
                return;

            List<DirectedNodeEdge> edges;
            if (!_outgoingEdgesByNode.TryGetValue(fromNodeId, out edges))
            {
                edges = new List<DirectedNodeEdge>();
                _outgoingEdgesByNode[fromNodeId] = edges;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].ToNodeId == toNodeId && edges[i].SegmentId == segmentId)
                    return;
            }

            edges.Add(new DirectedNodeEdge
            {
                ToNodeId = toNodeId,
                SegmentId = segmentId,
                Cost = cost
            });
        }

        private void AddCandidateNodeDistance(Dictionary<ushort, float> distances, ushort nodeId, float distance)
        {
            if (nodeId == 0)
                return;

            float existing;
            if (distances.TryGetValue(nodeId, out existing) && existing <= distance)
                return;

            distances[nodeId] = distance;
        }

        private bool TryGetStopMatchOnSegment(
            NetManager nm,
            ushort segmentId,
            Vector3 pos,
            float maxSqr,
            out CachedStopMatch match,
            out float candidateSqr,
            out float candidateScore)
        {
            match = new CachedStopMatch();
            candidateSqr = maxSqr;
            candidateScore = float.MaxValue;

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return false;

            NetInfo info = segment.Info;
            if (!Util.IsBusStopRoad(info, _avoidHighways))
                return false;

            if (!IsSurfaceStopSegment(nm, ref segment, false))
                return false;

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
                return false;
            }

            ref NetLane lane = ref nm.m_lanes.m_buffer[laneId];
            NetInfo.Lane laneInfo = info.m_lanes[laneIndex];
            if (laneInfo.m_stopOffset <= 0.01f)
                return false;

            Vector3 candidatePos;
            Vector3 direction;
            lane.CalculateStopPositionAndDirection(laneOffset, laneInfo.m_stopOffset, out candidatePos, out direction);

            float dx = candidatePos.x - pos.x;
            float dz = candidatePos.z - pos.z;
            float sqr = dx * dx + dz * dz;
            if (sqr >= maxSqr)
                return false;

            candidateSqr = sqr;
            candidateScore = Mathf.Sqrt(sqr) + GetStopPlacementPenalty(info, segmentId);
            match = new CachedStopMatch
            {
                BuildingPosition = pos,
                StopPosition = candidatePos,
                SegmentId = segmentId,
                RoadName = Util.GetNetInfoName(info)
            };
            return true;
        }

        private bool IsSurfaceStopSegment(NetManager nm, ref NetSegment segment, bool recordRejection)
        {
            string reason = Util.GetRoadSegmentSurfaceRejection(nm, ref segment);
            if (reason == null)
                return true;

            if (recordRejection)
            {
                if (reason == "underground")
                    RejectedUndergroundSegments++;
                else if (reason == "collapsed")
                    RejectedCollapsedSegments++;
                else if (reason == "missing-node")
                    RejectedMissingNodeSegments++;
            }

            return false;
        }

        private float GetStopPlacementPenalty(NetInfo info, ushort segmentId)
        {
            float penalty = 0f;
            if (!HasOpposingVehicleLanes(info))
                penalty += 180f;

            List<ushort> neighbors;
            if (!_neighborsBySegment.TryGetValue(segmentId, out neighbors) || neighbors.Count <= 1)
                penalty += 120f;

            RoadBaseAI roadAi = info != null ? info.m_netAI as RoadBaseAI : null;
            if (roadAi != null && roadAi.m_highwayRules)
                penalty += 260f;

            return penalty;
        }

        private bool HasOpposingVehicleLanes(NetInfo info)
        {
            bool forward = false;
            bool backward = false;
            GetVehicleTravelDirections(info, out forward, out backward);
            return forward && backward;
        }

        private void GetVehicleTravelDirections(NetInfo info, out bool forward, out bool backward)
        {
            forward = false;
            backward = false;
            if (info == null || info.m_lanes == null)
                return;

            for (int i = 0; i < info.m_lanes.Length; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) == 0)
                    continue;

                if ((lane.m_direction & NetInfo.Direction.Forward) != 0)
                    forward = true;
                if ((lane.m_direction & NetInfo.Direction.Backward) != 0)
                    backward = true;
            }
        }

        private float GetSegmentTraversalCost(NetManager nm, ushort segmentId)
        {
            if (segmentId == 0 || segmentId >= nm.m_segments.m_size)
                return 0f;

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return 0f;

            return Mathf.Max(24f, segment.m_averageLength);
        }

        private long Key(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }
    }
}
