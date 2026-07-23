using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace AutoPublicTransit
{
    internal sealed class BusRouteRoadUpgradeService
    {
        private const int MaxLineStops = 512;
        private const int MaxPathUnitChain = 4096;
        private const int MaxPreviewLogItems = 12;
        internal const int MaxNoTargetLogItems = 20;
        private const int DefaultApplyChunkSize = 20;
        private const float ExactWidthTolerance = 0.1f;
        private const float HighwayWidthTolerance = 8.1f;
        private readonly Dictionary<NetInfo, RoadVariantKind> _roadVariantKindByInfo = new Dictionary<NetInfo, RoadVariantKind>();
        private readonly Dictionary<NetInfo, NetInfo> _surfaceParentByVariantInfo = new Dictionary<NetInfo, NetInfo>();

        public BusRouteRoadUpgradePlan BuildRouteRoadUpgradePlan()
        {
            var plan = new BusRouteRoadUpgradePlan();
            var lineIdsBySegment = new Dictionary<ushort, HashSet<ushort>>();

            CollectLiveBusRouteSegments(lineIdsBySegment, plan.Result);
            TransitLogging.Log(
                "Route road-upgrade path extraction: lines=" + plan.Result.LiveBusLineCount +
                ", stopLinks=" + plan.Result.StopLinkCount +
                ", pathUnits=" + plan.Result.PathUnitCount +
                ", pathPositions=" + plan.Result.PathPositionCount +
                ", uniquePathSegments=" + lineIdsBySegment.Count +
                ", pathIssues=" + plan.Result.PathIssueCount + ".");

            List<BusRouteRoadUpgradeCandidate> candidates = BuildUpgradeCandidates(lineIdsBySegment, plan.Result);
            BuildUpgradeGroups(plan, candidates);
            LogPlanPreview(plan);
            return plan;
        }

        public BusRouteRoadUpgradeApplyState BeginRouteRoadUpgradeApply(BusRouteRoadUpgradePlan plan)
        {
            BusRouteRoadUpgradeResult result = plan != null
                ? plan.Result.CloneForApply()
                : new BusRouteRoadUpgradeResult();
            var state = new BusRouteRoadUpgradeApplyState(plan, result, new RonNetworkReplacer());

            if (plan == null)
            {
                result.Failed++;
                TransitLogging.Warn("Route road upgrades cannot apply: no pending RON batch plan.");
                state.Completed = true;
                return state;
            }

            TransitLogging.Log(
                "Route road upgrades confirmed by player: groups=" + plan.Groups.Count +
                ", segments=" + plan.TotalSegmentCount +
                ", chunkSize=" + DefaultApplyChunkSize + ".");

            if (!state.Ron.IsAvailable)
            {
                result.Failed += plan.TotalSegmentCount;
                TransitLogging.Warn("Route road upgrades require RON: " + state.Ron.UnavailableReason + ".");
                state.Completed = true;
                return state;
            }

            if (plan.Groups.Count == 0)
            {
                TransitLogging.Log("Route road upgrades found no safe RON batches to apply.");
                state.Completed = true;
                return state;
            }

            return state;
        }

        public BusRouteRoadUpgradeResult ApplyRouteRoadUpgradePlan(BusRouteRoadUpgradePlan plan)
        {
            BusRouteRoadUpgradeApplyState state = BeginRouteRoadUpgradeApply(plan);
            while (!state.Completed)
                ApplyNextRouteRoadUpgradeChunk(state, DefaultApplyChunkSize);

            return state.Result;
        }

        public void ApplyNextRouteRoadUpgradeChunk(BusRouteRoadUpgradeApplyState state, int maxSegments)
        {
            if (state == null || state.Completed)
                return;

            if (maxSegments <= 0)
                maxSegments = DefaultApplyChunkSize;

            NetManager nm = NetManager.instance;
            if (nm == null)
            {
                if (state.PendingPostUpgradeRepairCandidates != null)
                {
                    state.Result.PostUpgradeRepairFailed += state.PendingPostUpgradeRepairCandidates.Count;
                    state.PendingPostUpgradeRepairCandidates = null;
                }

                state.Result.Failed += CountRemainingSegments(state);
                CompleteRouteRoadUpgradeApply(state);
                return;
            }

            if (state.PendingPostUpgradeRepairCandidates != null)
            {
                AuditAndRepairPostUpgradeCollapsedSegments(nm, state);
                return;
            }

            while (state.GroupIndex < state.Plan.Groups.Count)
            {
                BusRouteRoadUpgradeGroup group = state.Plan.Groups[state.GroupIndex];
                List<ushort> liveSegments = RevalidateGroupSegmentChunk(
                    nm,
                    group,
                    state.Result,
                    ref state.SegmentIndex,
                    maxSegments);

                if (liveSegments.Count == 0)
                {
                    if (state.SegmentIndex >= group.SegmentIds.Count)
                    {
                        state.GroupIndex++;
                        state.SegmentIndex = 0;
                        continue;
                    }

                    continue;
                }

                List<BusRouteRoadUpgradeRepairCandidate> repairCandidates = CapturePostUpgradeRepairCandidates(group, liveSegments);
                string error;
                if (!state.Ron.TryReplace(group.CurrentInfo, group.TargetInfo, liveSegments, out error))
                {
                    state.Result.Failed += liveSegments.Count;
                    state.PendingPostUpgradeRepairCandidates = repairCandidates;
                    TransitLogging.Warn(
                        "RON route road-upgrade chunk failed: " +
                        group.CurrentName + " -> " + group.TargetName +
                        ", segments=" + liveSegments.Count +
                        ", firstSegment=" + liveSegments[0] +
                        ", lastSegment=" + liveSegments[liveSegments.Count - 1] +
                        ", error=" + error + ".");
                }
                else
                {
                    state.Result.Applied += liveSegments.Count;
                    state.Result.RonBatchCount++;
                    state.PendingPostUpgradeRepairCandidates = repairCandidates;
                    TransitLogging.Log(
                        "RON route road-upgrade chunk requested: " +
                        group.CurrentName + " -> " + group.TargetName +
                        ", group=" + (state.GroupIndex + 1) + "/" + state.Plan.Groups.Count +
                        ", segments=" + liveSegments.Count +
                        ", firstSegment=" + liveSegments[0] +
                        ", lastSegment=" + liveSegments[liveSegments.Count - 1] +
                        ", requestedSoFar=" + state.Result.Applied +
                        "/" + state.Plan.TotalSegmentCount +
                        ", busLineUses=" + group.BusLineUseCount +
                        ", length=" + group.TotalLength.ToString("0.0", CultureInfo.InvariantCulture) + ".");
                }

                if (state.SegmentIndex >= group.SegmentIds.Count)
                {
                    state.GroupIndex++;
                    state.SegmentIndex = 0;
                }

                return;
            }

            CompleteRouteRoadUpgradeApply(state);
        }

        private void CompleteRouteRoadUpgradeApply(BusRouteRoadUpgradeApplyState state)
        {
            if (state == null || state.Completed)
                return;

            if ((state.Result.Applied > 0 || state.Result.PostUpgradeCollapsedRepaired > 0) && !state.StopCacheCleared)
            {
                State.StopCache.Clear();
                state.StopCacheCleared = true;
            }

            TransitLogging.Log(
                "Route road upgrades complete via RON: requested=" + state.Result.Applied +
                ", failed=" + state.Result.Failed +
                ", stale=" + state.Result.StaleSkipped +
                ", ronCalls=" + state.Result.RonBatchCount +
                ", candidateGroups=" + state.Result.GroupCount +
                ", alreadyBusLane=" + state.Result.AlreadyBusLaneSkipped +
                ", unsafe=" + state.Result.UnsafeSkipped +
                ", nonRoad=" + state.Result.NonRoadSkipped +
                ", noTarget=" + state.Result.NoTargetSkipped +
                ", postUpgradeCollapsed=" + state.Result.PostUpgradeCollapsedDetected +
                ", postUpgradeRepaired=" + state.Result.PostUpgradeCollapsedRepaired +
                ", postUpgradeRepairFailed=" + state.Result.PostUpgradeRepairFailed +
                ", postUpgradeRepairStale=" + state.Result.PostUpgradeRepairStale +
                ", stopCacheCleared=" + state.StopCacheCleared + ".");

            state.Completed = true;
        }

        private int CountRemainingSegments(BusRouteRoadUpgradeApplyState state)
        {
            if (state == null || state.Plan == null)
                return 0;

            int count = 0;
            for (int i = state.GroupIndex; i < state.Plan.Groups.Count; i++)
            {
                BusRouteRoadUpgradeGroup group = state.Plan.Groups[i];
                int start = i == state.GroupIndex ? state.SegmentIndex : 0;
                count += Mathf.Max(0, group.SegmentIds.Count - start);
            }

            return count;
        }

        private void CollectLiveBusRouteSegments(Dictionary<ushort, HashSet<ushort>> lineIdsBySegment, BusRouteRoadUpgradeResult result)
        {
            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if (!IsLiveCompleteBusLine(ref line))
                    continue;

                result.LiveBusLineCount++;
                CollectLineRouteSegments(lineId, ref line, lineIdsBySegment, result);
            }
        }

        private bool IsLiveCompleteBusLine(ref TransportLine line)
        {
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
                return false;
            if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden | TransportLine.Flags.Invalid)) != 0)
                return false;
            if ((line.m_flags & TransportLine.Flags.Complete) == 0)
                return false;

            TransportInfo info = line.Info;
            return info != null && info.m_transportType == TransportInfo.TransportType.Bus && line.m_stops != 0;
        }

        private void CollectLineRouteSegments(
            ushort lineId,
            ref TransportLine line,
            Dictionary<ushort, HashSet<ushort>> lineIdsBySegment,
            BusRouteRoadUpgradeResult result)
        {
            ushort firstStop = line.m_stops;
            ushort currentStop = firstStop;
            int checkedStops = 0;

            while (currentStop != 0 && checkedStops < MaxLineStops)
            {
                ushort nextStop = TransportLine.GetNextStop(currentStop);
                if (nextStop == 0)
                {
                    result.PathIssueCount++;
                    TransitLogging.Warn("Route road-upgrade skipped remaining links for line " + lineId + ": stop chain ended at index " + checkedStops + ".");
                    return;
                }

                ushort lineSegmentId = TransportLine.GetNextSegment(currentStop);
                result.StopLinkCount++;
                CollectPathSegments(lineId, checkedStops, lineSegmentId, lineIdsBySegment, result);

                checkedStops++;
                currentStop = nextStop;
                if (currentStop == firstStop)
                    return;
            }

            if (checkedStops >= MaxLineStops)
            {
                result.PathIssueCount++;
                TransitLogging.Warn("Route road-upgrade stopped reading line " + lineId + " after " + MaxLineStops + " stops.");
            }
        }

        private void CollectPathSegments(
            ushort lineId,
            int stopIndex,
            ushort lineSegmentId,
            Dictionary<ushort, HashSet<ushort>> lineIdsBySegment,
            BusRouteRoadUpgradeResult result)
        {
            NetManager nm = NetManager.instance;
            if (nm == null || lineSegmentId == 0 || lineSegmentId >= nm.m_segments.m_size)
            {
                result.PathIssueCount++;
                TransitLogging.Warn("Route road-upgrade line " + lineId + " stopLink " + stopIndex + " has no transport path segment.");
                return;
            }

            ref NetSegment lineSegment = ref nm.m_segments.m_buffer[lineSegmentId];
            if ((lineSegment.m_flags & NetSegment.Flags.PathFailed) != 0)
            {
                result.PathIssueCount++;
                TransitLogging.Warn("Route road-upgrade line " + lineId + " stopLink " + stopIndex + " has failed path.");
                return;
            }

            if ((lineSegment.m_flags & NetSegment.Flags.WaitingPath) != 0 || lineSegment.m_path == 0)
            {
                result.PathIssueCount++;
                TransitLogging.Warn("Route road-upgrade line " + lineId + " stopLink " + stopIndex + " has pending or missing path.");
                return;
            }

            PathManager pm = PathManager.instance;
            if (pm == null)
            {
                result.PathIssueCount++;
                return;
            }

            uint pathUnitId = lineSegment.m_path;
            int safety = 0;
            while (pathUnitId != 0 && safety < MaxPathUnitChain)
            {
                if (pathUnitId >= pm.m_pathUnits.m_size)
                {
                    result.PathIssueCount++;
                    TransitLogging.Warn("Route road-upgrade line " + lineId + " stopLink " + stopIndex + " path unit out of range: " + pathUnitId + ".");
                    return;
                }

                ref PathUnit pathUnit = ref pm.m_pathUnits.m_buffer[(int)pathUnitId];
                result.PathUnitCount++;
                int positionCount = Mathf.Min(pathUnit.m_positionCount, 12);
                for (int i = 0; i < positionCount; i++)
                {
                    PathUnit.Position position;
                    if (!pathUnit.GetPosition(i, out position))
                        continue;

                    result.PathPositionCount++;
                    AddPathPositionSegment(position.m_segment, lineId, lineIdsBySegment);
                }

                uint nextPathUnitId = pathUnit.m_nextPathUnit;
                if (nextPathUnitId == pathUnitId)
                {
                    result.PathIssueCount++;
                    TransitLogging.Warn("Route road-upgrade line " + lineId + " stopLink " + stopIndex + " path unit loop at " + pathUnitId + ".");
                    return;
                }

                pathUnitId = nextPathUnitId;
                safety++;
            }

            if (safety >= MaxPathUnitChain)
            {
                result.PathIssueCount++;
                TransitLogging.Warn("Route road-upgrade line " + lineId + " stopLink " + stopIndex + " exceeded path unit safety limit.");
            }
        }

        private void AddPathPositionSegment(ushort segmentId, ushort lineId, Dictionary<ushort, HashSet<ushort>> lineIdsBySegment)
        {
            if (segmentId == 0)
                return;

            HashSet<ushort> lineIds;
            if (!lineIdsBySegment.TryGetValue(segmentId, out lineIds))
            {
                lineIds = new HashSet<ushort>();
                lineIdsBySegment[segmentId] = lineIds;
            }

            lineIds.Add(lineId);
        }

        private List<BusRouteRoadUpgradeCandidate> BuildUpgradeCandidates(
            Dictionary<ushort, HashSet<ushort>> lineIdsBySegment,
            BusRouteRoadUpgradeResult result)
        {
            var candidates = new List<BusRouteRoadUpgradeCandidate>();
            if (lineIdsBySegment == null || lineIdsBySegment.Count == 0)
                return candidates;

            NetManager nm = NetManager.instance;
            if (nm == null)
                return candidates;

            foreach (KeyValuePair<ushort, HashSet<ushort>> item in lineIdsBySegment)
            {
                ushort segmentId = item.Key;
                if (segmentId == 0 || segmentId >= nm.m_segments.m_size)
                {
                    result.UnsafeSkipped++;
                    continue;
                }

                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                {
                    result.UnsafeSkipped++;
                    continue;
                }

                NetInfo currentInfo = segment.Info;
                if (!IsEligibleSourceRoad(currentInfo))
                {
                    if (!IsRoadInfo(currentInfo))
                        result.NonRoadSkipped++;
                    else if (HasBusLane(currentInfo))
                        result.AlreadyBusLaneSkipped++;
                    else
                        result.UnsafeSkipped++;
                    continue;
                }

                string unsafeReason = GetUnsafeSegmentReason(nm, ref segment);
                if (unsafeReason != null)
                {
                    result.UnsafeSkipped++;
                    continue;
                }

                NetInfo targetInfo = FindVanillaBusLaneTarget(currentInfo);
                if (targetInfo == null)
                {
                    result.NoTargetSkipped++;
                    result.RecordNoTarget(currentInfo);
                    continue;
                }

                candidates.Add(new BusRouteRoadUpgradeCandidate
                {
                    SegmentId = segmentId,
                    CurrentInfo = currentInfo,
                    TargetInfo = targetInfo,
                    BusLineCount = item.Value != null ? item.Value.Count : 0,
                    Length = segment.m_averageLength
                });
            }

            candidates.Sort((a, b) =>
            {
                int currentCompare = string.Compare(GetNetInfoName(a.CurrentInfo), GetNetInfoName(b.CurrentInfo), StringComparison.OrdinalIgnoreCase);
                if (currentCompare != 0)
                    return currentCompare;

                int targetCompare = string.Compare(GetNetInfoName(a.TargetInfo), GetNetInfoName(b.TargetInfo), StringComparison.OrdinalIgnoreCase);
                if (targetCompare != 0)
                    return targetCompare;

                return b.BusLineCount.CompareTo(a.BusLineCount);
            });

            result.CandidateCount = candidates.Count;
            return candidates;
        }

        private void BuildUpgradeGroups(BusRouteRoadUpgradePlan plan, List<BusRouteRoadUpgradeCandidate> candidates)
        {
            if (plan == null || candidates == null)
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                BusRouteRoadUpgradeCandidate candidate = candidates[i];
                BusRouteRoadUpgradeGroup group = FindGroup(plan.Groups, candidate.CurrentInfo, candidate.TargetInfo);
                if (group == null)
                {
                    group = new BusRouteRoadUpgradeGroup(candidate.CurrentInfo, candidate.TargetInfo);
                    plan.Groups.Add(group);
                }

                group.SegmentIds.Add(candidate.SegmentId);
                group.BusLineUseCount += candidate.BusLineCount;
                group.MaxBusLineCount = Mathf.Max(group.MaxBusLineCount, candidate.BusLineCount);
                group.TotalLength += candidate.Length;
            }

            plan.Groups.Sort((a, b) =>
            {
                int countCompare = b.SegmentCount.CompareTo(a.SegmentCount);
                if (countCompare != 0)
                    return countCompare;

                return string.Compare(a.CurrentName, b.CurrentName, StringComparison.OrdinalIgnoreCase);
            });

            plan.Result.GroupCount = plan.Groups.Count;
        }

        private BusRouteRoadUpgradeGroup FindGroup(List<BusRouteRoadUpgradeGroup> groups, NetInfo currentInfo, NetInfo targetInfo)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (ReferenceEquals(groups[i].CurrentInfo, currentInfo) && ReferenceEquals(groups[i].TargetInfo, targetInfo))
                    return groups[i];
            }

            return null;
        }

        private List<BusRouteRoadUpgradeRepairCandidate> CapturePostUpgradeRepairCandidates(
            BusRouteRoadUpgradeGroup group,
            List<ushort> segmentIds)
        {
            var candidates = new List<BusRouteRoadUpgradeRepairCandidate>();
            if (group == null || segmentIds == null)
                return candidates;

            for (int i = 0; i < segmentIds.Count; i++)
            {
                candidates.Add(new BusRouteRoadUpgradeRepairCandidate
                {
                    SegmentId = segmentIds[i],
                    SourceInfo = group.CurrentInfo,
                    TargetInfo = group.TargetInfo,
                    SourceName = group.CurrentName,
                    TargetName = group.TargetName
                });
            }

            return candidates;
        }

        private void AuditAndRepairPostUpgradeCollapsedSegments(NetManager nm, BusRouteRoadUpgradeApplyState state)
        {
            List<BusRouteRoadUpgradeRepairCandidate> candidates = state.PendingPostUpgradeRepairCandidates;
            state.PendingPostUpgradeRepairCandidates = null;
            if (candidates == null || candidates.Count == 0)
                return;

            int detected = 0;
            int repaired = 0;
            int failed = 0;
            int stale = 0;

            for (int i = 0; i < candidates.Count; i++)
            {
                BusRouteRoadUpgradeRepairCandidate candidate = candidates[i];
                state.Result.PostUpgradeAuditCount++;

                string collapseReason;
                bool segmentStillExists;
                if (!TryGetPostUpgradeCollapseReason(nm, candidate.SegmentId, out collapseReason, out segmentStillExists))
                {
                    if (!segmentStillExists)
                    {
                        stale++;
                        state.Result.PostUpgradeRepairStale++;
                    }

                    continue;
                }

                detected++;
                state.Result.PostUpgradeCollapsedDetected++;
                TransitLogging.Warn(
                    "Route road-upgrade detected post-RON collapsed road before next chunk: segment=" + candidate.SegmentId +
                    ", " + candidate.SourceName + " -> " + candidate.TargetName +
                    ", reason=" + collapseReason + ".");

                string repairError;
                ushort resultingSegmentId;
                if (TryRepairPostUpgradeCollapsedSegment(nm, candidate, out resultingSegmentId, out repairError))
                {
                    repaired++;
                    state.Result.PostUpgradeCollapsedRepaired++;
                    TransitLogging.Log(
                        "Route road-upgrade repaired post-RON collapsed road with the same prefab before next chunk: oldSegment=" + candidate.SegmentId +
                        ", resultingSegment=" + resultingSegmentId +
                        ", " + candidate.SourceName + " -> " + candidate.TargetName + ".");
                }
                else
                {
                    failed++;
                    state.Result.PostUpgradeRepairFailed++;
                    TransitLogging.Warn(
                        "Route road-upgrade could not repair post-RON collapsed road before next chunk: segment=" + candidate.SegmentId +
                        ", " + candidate.SourceName + " -> " + candidate.TargetName +
                        ", error=" + repairError + ".");
                }
            }

            if (detected > 0)
            {
                TransitLogging.Log(
                    "Route road-upgrade post-RON collapse audit: checked=" + candidates.Count +
                    ", collapsed=" + detected +
                    ", repaired=" + repaired +
                    ", failed=" + failed +
                    ", stale=" + stale + ".");
            }
        }

        private bool IsEligibleSourceRoad(NetInfo info)
        {
            if (!IsRoadInfo(info))
                return false;
            if (info.m_isCustomContent)
                return false;
            if (HasBusLane(info))
                return false;
            if (IsBusOnlyRoad(info))
                return false;

            if (!IsSupportedRoadAi(info))
                return false;

            return true;
        }

        private bool IsRoadInfo(NetInfo info)
        {
            return info != null && info.m_netAI is RoadBaseAI;
        }

        private bool IsSupportedRoadAi(NetInfo info)
        {
            if (info == null || info.m_netAI == null)
                return false;

            Type aiType = info.m_netAI.GetType();
            return aiType == typeof(RoadAI) ||
                   aiType == typeof(RoadBridgeAI) ||
                   aiType == typeof(RoadTunnelAI);
        }

        private string GetUnsafeSegmentReason(NetManager nm, ref NetSegment segment)
        {
            if ((segment.m_flags & (NetSegment.Flags.Collapsed | NetSegment.Flags.Untouchable | NetSegment.Flags.PathFailed | NetSegment.Flags.WaitingPath)) != 0)
                return "unsafe-flags";

            if (segment.m_startNode == 0 || segment.m_endNode == 0)
                return "missing-node";
            if (segment.m_startNode >= nm.m_nodes.m_size || segment.m_endNode >= nm.m_nodes.m_size)
                return "missing-node";

            NetNode.Flags startFlags = nm.m_nodes.m_buffer[segment.m_startNode].m_flags;
            NetNode.Flags endFlags = nm.m_nodes.m_buffer[segment.m_endNode].m_flags;

            if ((startFlags & NetNode.Flags.Created) == 0 || (endFlags & NetNode.Flags.Created) == 0)
                return "missing-node";
            if ((startFlags & NetNode.Flags.Collapsed) != 0 || (endFlags & NetNode.Flags.Collapsed) != 0)
                return "collapsed-node";
            if ((startFlags & NetNode.Flags.Untouchable) != 0 || (endFlags & NetNode.Flags.Untouchable) != 0)
                return "untouchable-node";
            if ((startFlags & NetNode.Flags.Outside) != 0 || (endFlags & NetNode.Flags.Outside) != 0)
                return "outside-node";

            return null;
        }

        private NetInfo FindVanillaBusLaneTarget(NetInfo currentInfo)
        {
            if (currentInfo == null)
                return null;

            RoadBaseAI currentRoadAi = currentInfo.m_netAI as RoadBaseAI;
            if (currentRoadAi == null)
                return null;

            RoadVariantKind currentKind = GetRoadVariantKind(currentInfo);
            if (currentKind != RoadVariantKind.Surface)
            {
                NetInfo variantTarget = FindVariantBusLaneTarget(currentInfo, currentRoadAi, currentKind);
                if (variantTarget != null)
                    return variantTarget;
            }

            return FindLoadedBusLaneTarget(currentInfo, currentRoadAi, currentKind);
        }

        private NetInfo FindVariantBusLaneTarget(NetInfo currentInfo, RoadBaseAI currentRoadAi, RoadVariantKind currentKind)
        {
            NetInfo surfaceParent = GetSurfaceParentRoad(currentInfo);
            if (surfaceParent == null || ReferenceEquals(surfaceParent, currentInfo))
                return null;

            RoadBaseAI surfaceRoadAi = surfaceParent.m_netAI as RoadBaseAI;
            if (surfaceRoadAi == null)
                return null;

            NetInfo surfaceTarget = FindLoadedBusLaneTarget(surfaceParent, surfaceRoadAi, RoadVariantKind.Surface);
            NetInfo variantTarget = GetRoadVariantFromParent(surfaceTarget, currentKind);
            if (variantTarget == null)
                return null;

            return IsEligibleTargetRoad(currentInfo, variantTarget, currentRoadAi, currentKind, true)
                ? variantTarget
                : null;
        }

        private NetInfo FindLoadedBusLaneTarget(NetInfo currentInfo, RoadBaseAI currentRoadAi, RoadVariantKind currentKind)
        {
            NetInfo best = null;
            float bestScore = float.MaxValue;
            int loaded = PrefabCollection<NetInfo>.LoadedCount();

            for (uint i = 0; i < loaded; i++)
            {
                NetInfo candidate = PrefabCollection<NetInfo>.GetPrefab(i);
                if (candidate == null || candidate == currentInfo)
                    continue;

                if (!IsEligibleTargetRoad(currentInfo, candidate, currentRoadAi, currentKind, false))
                    continue;

                float score = ScoreTargetRoad(currentInfo, candidate);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = candidate;
            }

            return best;
        }

        private bool IsEligibleTargetRoad(
            NetInfo currentInfo,
            NetInfo candidate,
            RoadBaseAI currentRoadAi,
            RoadVariantKind currentKind,
            bool parentWidthMatched)
        {
            if (candidate == null)
                return false;
            if (currentRoadAi == null)
                return false;
            if (candidate.m_isCustomContent)
                return false;

            RoadBaseAI candidateRoadAi = candidate.m_netAI as RoadBaseAI;
            if (candidateRoadAi == null || !IsSupportedRoadAi(candidate))
                return false;

            bool currentHighway = HasHighwayIdentity(currentInfo, currentRoadAi);
            bool candidateHighway = HasHighwayIdentity(candidate, candidateRoadAi);
            if (currentHighway != candidateHighway)
                return false;
            if (!currentHighway && candidateRoadAi.m_highwayRules != currentRoadAi.m_highwayRules)
                return false;

            if (GetRoadVariantKind(candidate) != currentKind)
                return false;

            if (!HasBusLane(candidate) || IsBusOnlyRoad(candidate))
                return false;

            if (currentInfo.m_class == null || candidate.m_class == null)
                return false;
            if (currentInfo.m_class.m_service != candidate.m_class.m_service)
                return false;

            bool currentOneWay = IsOneWay(currentInfo);
            bool candidateOneWay = IsOneWay(candidate);
            if (currentOneWay != candidateOneWay)
                return false;

            if (!parentWidthMatched && !IsCompatibleRoadWidth(currentInfo, candidate, currentRoadAi))
                return false;

            if (candidate.m_hasPedestrianLanes != currentInfo.m_hasPedestrianLanes)
                return false;

            return true;
        }

        private bool IsCompatibleRoadWidth(NetInfo currentInfo, NetInfo candidate, RoadBaseAI currentRoadAi)
        {
            float widthDelta = Mathf.Abs(candidate.m_halfWidth - currentInfo.m_halfWidth);
            if (widthDelta <= ExactWidthTolerance)
                return true;

            if (HasHighwayIdentity(currentInfo, currentRoadAi) && widthDelta <= HighwayWidthTolerance)
                return true;

            return false;
        }

        private bool HasHighwayIdentity(NetInfo info, RoadBaseAI roadAi)
        {
            if (roadAi != null && roadAi.m_highwayRules)
                return true;

            string name = NormalizeRoadName(GetNetInfoName(info));
            return name.Contains("highway");
        }

        private RoadVariantKind GetRoadVariantKind(NetInfo info)
        {
            if (info == null)
                return RoadVariantKind.Surface;

            RoadVariantKind kind;
            if (_roadVariantKindByInfo.TryGetValue(info, out kind))
                return kind;

            BuildRoadVariantKindCache();
            if (_roadVariantKindByInfo.TryGetValue(info, out kind))
                return kind;

            kind = GetRoadVariantKindFromInfo(info);
            _roadVariantKindByInfo[info] = kind;
            return kind;
        }

        private void BuildRoadVariantKindCache()
        {
            int loaded = PrefabCollection<NetInfo>.LoadedCount();
            for (uint i = 0; i < loaded; i++)
            {
                NetInfo info = PrefabCollection<NetInfo>.GetPrefab(i);
                if (info == null)
                    continue;

                MarkRoadVariantKind(info, GetRoadVariantKindFromInfo(info), false);
                RoadAI roadAi = info.m_netAI as RoadAI;
                if (roadAi == null)
                    continue;

                MarkRoadVariantKind(roadAi.m_elevatedInfo, RoadVariantKind.Elevated, true);
                MarkRoadVariantKind(roadAi.m_bridgeInfo, RoadVariantKind.Bridge, true);
                MarkRoadVariantKind(roadAi.m_slopeInfo, RoadVariantKind.Slope, true);
                MarkRoadVariantKind(roadAi.m_tunnelInfo, RoadVariantKind.Tunnel, true);
                MarkSurfaceParent(roadAi.m_elevatedInfo, info);
                MarkSurfaceParent(roadAi.m_bridgeInfo, info);
                MarkSurfaceParent(roadAi.m_slopeInfo, info);
                MarkSurfaceParent(roadAi.m_tunnelInfo, info);
            }
        }

        private void MarkRoadVariantKind(NetInfo info, RoadVariantKind kind, bool overwrite)
        {
            if (info == null)
                return;
            if (!IsRoadInfo(info))
                return;
            if (!overwrite && _roadVariantKindByInfo.ContainsKey(info))
                return;

            _roadVariantKindByInfo[info] = kind;
        }

        private void MarkSurfaceParent(NetInfo variantInfo, NetInfo surfaceInfo)
        {
            if (variantInfo == null || surfaceInfo == null)
                return;
            if (!IsRoadInfo(variantInfo) || !IsRoadInfo(surfaceInfo))
                return;

            _surfaceParentByVariantInfo[variantInfo] = surfaceInfo;
        }

        private NetInfo GetSurfaceParentRoad(NetInfo info)
        {
            if (info == null)
                return null;

            BuildRoadVariantKindCache();

            NetInfo parent;
            if (_surfaceParentByVariantInfo.TryGetValue(info, out parent))
                return parent;

            return FindSurfaceParentRoadByName(info);
        }

        private NetInfo FindSurfaceParentRoadByName(NetInfo variantInfo)
        {
            string variantName = NormalizeVariantParentName(GetNetInfoName(variantInfo));
            if (string.IsNullOrEmpty(variantName))
                return null;

            int loaded = PrefabCollection<NetInfo>.LoadedCount();
            for (uint i = 0; i < loaded; i++)
            {
                NetInfo candidate = PrefabCollection<NetInfo>.GetPrefab(i);
                if (candidate == null || candidate == variantInfo)
                    continue;
                if (!(candidate.m_netAI is RoadAI))
                    continue;
                if (GetRoadVariantKind(candidate) != RoadVariantKind.Surface)
                    continue;

                string candidateName = NormalizeRoadName(GetNetInfoName(candidate));
                if (candidateName == variantName)
                    return candidate;
            }

            return null;
        }

        private NetInfo GetRoadVariantFromParent(NetInfo parentInfo, RoadVariantKind kind)
        {
            if (parentInfo == null)
                return null;

            RoadAI roadAi = parentInfo.m_netAI as RoadAI;
            if (roadAi == null)
                return null;

            switch (kind)
            {
                case RoadVariantKind.Elevated:
                    return roadAi.m_elevatedInfo;
                case RoadVariantKind.Bridge:
                    return roadAi.m_bridgeInfo;
                case RoadVariantKind.Slope:
                    return roadAi.m_slopeInfo;
                case RoadVariantKind.Tunnel:
                    return roadAi.m_tunnelInfo;
                default:
                    return parentInfo;
            }
        }

        private RoadVariantKind GetRoadVariantKindFromInfo(NetInfo info)
        {
            if (info != null && info.m_netAI is RoadBridgeAI)
                return RoadVariantKind.Bridge;
            if (info != null && info.m_netAI is RoadTunnelAI)
                return RoadVariantKind.Tunnel;

            string compact = GetNetInfoName(info).ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);

            if (compact.Contains("tunnel"))
                return RoadVariantKind.Tunnel;
            if (compact.Contains("bridge"))
                return RoadVariantKind.Bridge;
            if (compact.Contains("elevated"))
                return RoadVariantKind.Elevated;
            if (compact.Contains("slope"))
                return RoadVariantKind.Slope;

            return RoadVariantKind.Surface;
        }

        private string NormalizeVariantParentName(string roadName)
        {
            string normalized = NormalizeRoadName(roadName);
            normalized = normalized.Replace("bridge", string.Empty);
            normalized = normalized.Replace("elevated", string.Empty);
            normalized = normalized.Replace("slope", string.Empty);
            normalized = normalized.Replace("tunnel", string.Empty);
            return normalized;
        }

        private string NormalizeRoadName(string roadName)
        {
            if (string.IsNullOrEmpty(roadName))
                return string.Empty;

            return roadName.ToLowerInvariant()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);
        }

        private bool IsOneWay(NetInfo info)
        {
            if (info == null)
                return false;

            bool forward = info.m_hasForwardVehicleLanes || info.m_forwardVehicleLaneCount > 0;
            bool backward = info.m_hasBackwardVehicleLanes || info.m_backwardVehicleLaneCount > 0;
            return forward != backward;
        }

        private float ScoreTargetRoad(NetInfo currentInfo, NetInfo candidate)
        {
            string current = GetNetInfoName(currentInfo).ToLowerInvariant();
            string target = GetNetInfoName(candidate).ToLowerInvariant();
            float score = 0f;

            score += DecorationMismatchPenalty(current, target, "tree");
            score += DecorationMismatchPenalty(current, target, "grass");
            score += DecorationMismatchPenalty(current, target, "bicycle");
            score += DecorationMismatchPenalty(current, target, "wide sidewalk");
            score += target.Length * 0.01f;
            return score;
        }

        private float DecorationMismatchPenalty(string current, string target, string token)
        {
            bool currentHas = current.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            bool targetHas = target.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
            return currentHas == targetHas ? 0f : 5f;
        }

        private List<ushort> RevalidateGroupSegments(NetManager nm, BusRouteRoadUpgradeGroup group, BusRouteRoadUpgradeResult result)
        {
            var liveSegments = new List<ushort>();
            for (int i = 0; i < group.SegmentIds.Count; i++)
            {
                ushort segmentId = group.SegmentIds[i];
                if (segmentId == 0 || segmentId >= nm.m_segments.m_size)
                {
                    result.StaleSkipped++;
                    continue;
                }

                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0 || !ReferenceEquals(segment.Info, group.CurrentInfo))
                {
                    result.StaleSkipped++;
                    continue;
                }

                if (GetUnsafeSegmentReason(nm, ref segment) != null)
                {
                    result.StaleSkipped++;
                    continue;
                }

                liveSegments.Add(segmentId);
            }

            return liveSegments;
        }

        private List<ushort> RevalidateGroupSegmentChunk(
            NetManager nm,
            BusRouteRoadUpgradeGroup group,
            BusRouteRoadUpgradeResult result,
            ref int segmentIndex,
            int maxSegments)
        {
            var liveSegments = new List<ushort>();
            if (nm == null || group == null || group.SegmentIds == null)
                return liveSegments;

            if (segmentIndex < 0)
                segmentIndex = 0;

            while (segmentIndex < group.SegmentIds.Count && liveSegments.Count < maxSegments)
            {
                ushort segmentId = group.SegmentIds[segmentIndex];
                segmentIndex++;

                if (segmentId == 0 || segmentId >= nm.m_segments.m_size)
                {
                    result.StaleSkipped++;
                    continue;
                }

                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.Created) == 0 || !ReferenceEquals(segment.Info, group.CurrentInfo))
                {
                    result.StaleSkipped++;
                    continue;
                }

                if (GetUnsafeSegmentReason(nm, ref segment) != null)
                {
                    result.StaleSkipped++;
                    continue;
                }

                liveSegments.Add(segmentId);
            }

            return liveSegments;
        }

        private bool TryGetPostUpgradeCollapseReason(NetManager nm, ushort segmentId, out string reason, out bool segmentStillExists)
        {
            reason = null;
            segmentStillExists = false;

            if (nm == null || segmentId == 0 || segmentId >= nm.m_segments.m_size)
                return false;

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return false;

            segmentStillExists = true;
            if ((segment.m_flags & NetSegment.Flags.Collapsed) != 0)
            {
                reason = "segment-collapsed";
                return true;
            }

            if (segment.m_startNode == 0 || segment.m_endNode == 0)
                return false;
            if (segment.m_startNode >= nm.m_nodes.m_size || segment.m_endNode >= nm.m_nodes.m_size)
                return false;

            NetNode.Flags startFlags = nm.m_nodes.m_buffer[segment.m_startNode].m_flags;
            NetNode.Flags endFlags = nm.m_nodes.m_buffer[segment.m_endNode].m_flags;
            if ((startFlags & NetNode.Flags.Collapsed) != 0 || (endFlags & NetNode.Flags.Collapsed) != 0)
            {
                reason = "node-collapsed";
                return true;
            }

            return false;
        }

        private bool TryRepairPostUpgradeCollapsedSegment(
            NetManager nm,
            BusRouteRoadUpgradeRepairCandidate candidate,
            out ushort resultingSegmentId,
            out string error)
        {
            resultingSegmentId = 0;
            error = null;

            if (nm == null)
            {
                error = "NetManager unavailable";
                return false;
            }

            if (candidate == null || candidate.SegmentId == 0 || candidate.SegmentId >= nm.m_segments.m_size)
            {
                error = "segment id unavailable";
                return false;
            }

            ref NetSegment segment = ref nm.m_segments.m_buffer[candidate.SegmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
            {
                error = "segment no longer exists";
                return false;
            }

            NetInfo repairInfo = segment.Info ?? candidate.TargetInfo ?? candidate.SourceInfo;
            if (!IsRoadInfo(repairInfo))
            {
                error = "repair road prefab unavailable";
                return false;
            }

            string blocker = GetPostUpgradeRepairBlocker(nm, ref segment);
            if (blocker != null)
            {
                error = blocker;
                return false;
            }

            ToolBase.ToolErrors toolErrors;
            try
            {
                toolErrors = RebuildSameRoadWithNetTool(nm, candidate.SegmentId, ref segment, repairInfo, out resultingSegmentId);
            }
            catch (Exception e)
            {
                error = "repair API threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }

            if (toolErrors != ToolBase.ToolErrors.None && toolErrors != ToolBase.ToolErrors.AlreadyExists)
            {
                error = "repair API returned " + toolErrors;
                return false;
            }

            ushort checkSegmentId = resultingSegmentId != 0 ? resultingSegmentId : candidate.SegmentId;
            return IsPostUpgradeRepairHealthy(nm, checkSegmentId, repairInfo, out error);
        }

        private string GetPostUpgradeRepairBlocker(NetManager nm, ref NetSegment segment)
        {
            if ((segment.m_flags & NetSegment.Flags.Untouchable) != 0)
                return "segment became untouchable";

            if (segment.m_startNode == 0 || segment.m_endNode == 0)
                return "missing node";
            if (segment.m_startNode >= nm.m_nodes.m_size || segment.m_endNode >= nm.m_nodes.m_size)
                return "missing node";

            NetNode.Flags startFlags = nm.m_nodes.m_buffer[segment.m_startNode].m_flags;
            NetNode.Flags endFlags = nm.m_nodes.m_buffer[segment.m_endNode].m_flags;
            if ((startFlags & NetNode.Flags.Created) == 0 || (endFlags & NetNode.Flags.Created) == 0)
                return "missing node";
            if ((startFlags & NetNode.Flags.Untouchable) != 0 || (endFlags & NetNode.Flags.Untouchable) != 0)
                return "untouchable node";
            if ((startFlags & NetNode.Flags.Outside) != 0 || (endFlags & NetNode.Flags.Outside) != 0)
                return "outside node";

            return null;
        }

        private ToolBase.ToolErrors RebuildSameRoadWithNetTool(
            NetManager nm,
            ushort segmentId,
            ref NetSegment segment,
            NetInfo repairInfo,
            out ushort resultingSegmentId)
        {
            NetTool.ControlPoint startPoint = new NetTool.ControlPoint();
            NetTool.ControlPoint middlePoint = new NetTool.ControlPoint();
            NetTool.ControlPoint endPoint = new NetTool.ControlPoint();
            resultingSegmentId = 0;

            Vector3 startPosition = nm.m_nodes.m_buffer[segment.m_startNode].m_position;
            Vector3 endPosition = nm.m_nodes.m_buffer[segment.m_endNode].m_position;
            Vector3 fallbackDirection = endPosition - startPosition;
            fallbackDirection.y = 0f;
            if (fallbackDirection.sqrMagnitude < 0.01f)
                fallbackDirection = Vector3.forward;
            else
                fallbackDirection.Normalize();

            startPoint.m_node = segment.m_startNode;
            startPoint.m_segment = segmentId;
            startPoint.m_position = startPosition;
            startPoint.m_direction = NormalizeDirectionOrFallback(segment.m_startDirection, fallbackDirection);
            startPoint.m_elevation = startPoint.m_position.y;

            middlePoint.m_node = 0;
            middlePoint.m_segment = segmentId;
            middlePoint.m_position = segment.m_middlePosition;
            middlePoint.m_direction = Vector3.zero;
            middlePoint.m_elevation = middlePoint.m_position.y;

            endPoint.m_node = segment.m_endNode;
            endPoint.m_segment = segmentId;
            endPoint.m_position = endPosition;
            endPoint.m_direction = NormalizeDirectionOrFallback(segment.m_endDirection, -fallbackDirection);
            endPoint.m_elevation = endPoint.m_position.y;

            ushort firstNode;
            ushort lastNode;
            ushort newSegment;
            int cost;
            int productionRate;
            bool invert = (segment.m_flags & NetSegment.Flags.Invert) != 0;

            ToolBase.ToolErrors errors = NetTool.CreateNode(
                repairInfo,
                startPoint,
                middlePoint,
                endPoint,
                new FastList<NetTool.NodePosition>(),
                2,
                false,
                false,
                false,
                true,
                false,
                invert,
                false,
                0,
                out firstNode,
                out lastNode,
                out newSegment,
                out cost,
                out productionRate);
            resultingSegmentId = newSegment;
            return errors;
        }

        private Vector3 NormalizeDirectionOrFallback(Vector3 direction, Vector3 fallback)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude >= 0.01f)
            {
                direction.Normalize();
                return direction;
            }

            fallback.y = 0f;
            if (fallback.sqrMagnitude >= 0.01f)
            {
                fallback.Normalize();
                return fallback;
            }

            return Vector3.forward;
        }

        private bool IsPostUpgradeRepairHealthy(NetManager nm, ushort segmentId, NetInfo expectedInfo, out string error)
        {
            error = null;

            if (segmentId == 0 || segmentId >= nm.m_segments.m_size)
            {
                error = "resulting segment id unavailable";
                return false;
            }

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
            {
                error = "resulting segment missing";
                return false;
            }

            if (!ReferenceEquals(segment.Info, expectedInfo))
            {
                error = "resulting road prefab mismatch: after=" + GetNetInfoName(segment.Info);
                return false;
            }

            bool exists;
            string collapseReason;
            if (TryGetPostUpgradeCollapseReason(nm, segmentId, out collapseReason, out exists))
            {
                error = "result still collapsed: " + collapseReason;
                return false;
            }

            return true;
        }

        private bool HasBusLane(NetInfo info)
        {
            if (info == null || info.m_lanes == null)
                return false;

            string name = GetNetInfoName(info);
            if (HasBusLaneRoadName(name))
                return true;

            bool busNamedRoad = HasBusRoadName(name);
            for (int i = 0; i < info.m_lanes.Length; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                bool carLane = (lane.m_vehicleType & VehicleInfo.VehicleType.Car) != 0;
                if (!carLane)
                    continue;

                if ((lane.m_laneType & NetInfo.LaneType.PublicTransport) != 0)
                    return true;

                bool transportVehicleLane = (lane.m_laneType & NetInfo.LaneType.TransportVehicle) != 0;
                if (transportVehicleLane && (busNamedRoad || HasBusPriorityCategory(lane.m_vehicleCategoryPart1)))
                    return true;

                if (HasBusPriorityCategory(lane.m_vehicleCategoryPart1))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsBusOnlyRoad(NetInfo info)
        {
            if (info == null)
                return false;

            int generalCarLanes = CountGeneralCarLanes(info);
            if (generalCarLanes > 0)
                return false;

            string name = GetNetInfoName(info).ToLowerInvariant();
            if (name.Contains("bus only") || name.Contains("bus-only") || name.Contains("busonly") || name.Contains("busway"))
                return true;

            return HasBusLane(info);
        }

        private bool HasBusPriorityCategory(VehicleInfo.VehicleCategoryPart1 category)
        {
            VehicleInfo.VehicleCategoryPart1 busAndTrolley =
                VehicleInfo.VehicleCategoryPart1.Bus | VehicleInfo.VehicleCategoryPart1.Trolleybus;

            return category == VehicleInfo.VehicleCategoryPart1.Bus ||
                   category == VehicleInfo.VehicleCategoryPart1.Trolleybus ||
                   category == busAndTrolley;
        }

        private int CountGeneralCarLanes(NetInfo info)
        {
            if (info == null || info.m_lanes == null)
                return 0;

            int count = 0;
            for (int i = 0; i < info.m_lanes.Length; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != 0 &&
                    (lane.m_laneType & NetInfo.LaneType.PublicTransport) == 0 &&
                    (lane.m_vehicleType & VehicleInfo.VehicleType.Car) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        private bool HasBusLaneRoadName(string roadName)
        {
            if (string.IsNullOrEmpty(roadName))
                return false;

            string lower = roadName.ToLowerInvariant();
            if (lower.Contains("bus depot"))
                return false;

            string compact = lower.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
            return lower.Contains("bus lane")
                || lower.Contains("bus lanes")
                || lower.Contains("bus only lane")
                || lower.Contains("bus only lanes")
                || compact.Contains("buslane")
                || compact.Contains("buslanes")
                || compact.Contains("busonlylane")
                || compact.Contains("busonlylanes");
        }

        private bool HasBusRoadName(string roadName)
        {
            if (string.IsNullOrEmpty(roadName))
                return false;

            string lower = roadName.ToLowerInvariant();
            return !lower.Contains("bus depot") && lower.Contains("bus");
        }

        internal static string GetNetInfoName(NetInfo info)
        {
            if (info == null)
                return "(unknown)";

            if (!string.IsNullOrEmpty(info.name))
                return info.name;

            return info.GetUncheckedLocalizedTitle();
        }

        private void LogPlanPreview(BusRouteRoadUpgradePlan plan)
        {
            TransitLogging.Log(
                "Route road-upgrade RON plan: groups=" + plan.Groups.Count +
                ", segments=" + plan.TotalSegmentCount +
                ", candidates=" + plan.Result.CandidateCount +
                ", alreadyBusLane=" + plan.Result.AlreadyBusLaneSkipped +
                ", unsafe=" + plan.Result.UnsafeSkipped +
                ", nonRoad=" + plan.Result.NonRoadSkipped +
                ", noTarget=" + plan.Result.NoTargetSkipped + ".");

            int previewCount = Mathf.Min(plan.Groups.Count, MaxPreviewLogItems);
            for (int i = 0; i < previewCount; i++)
            {
                BusRouteRoadUpgradeGroup group = plan.Groups[i];
                TransitLogging.Log(
                    "Route road-upgrade RON group " + (i + 1) +
                    ": " + group.CurrentName +
                    " -> " + group.TargetName +
                    ", segments=" + group.SegmentCount +
                    ", busLineUses=" + group.BusLineUseCount +
                    ", maxBusLines=" + group.MaxBusLineCount +
                    ", length=" + group.TotalLength.ToString("0.0", CultureInfo.InvariantCulture) + ".");
            }

            if (plan.Groups.Count > previewCount)
                TransitLogging.Log("Route road-upgrade RON group log truncated; remaining=" + (plan.Groups.Count - previewCount) + ".");

            plan.Result.LogNoTargetPreview();
        }

        private sealed class BusRouteRoadUpgradeCandidate
        {
            public ushort SegmentId;
            public NetInfo CurrentInfo;
            public NetInfo TargetInfo;
            public int BusLineCount;
            public float Length;
        }

        internal sealed class BusRouteRoadUpgradeRepairCandidate
        {
            public ushort SegmentId;
            public NetInfo SourceInfo;
            public NetInfo TargetInfo;
            public string SourceName;
            public string TargetName;
        }

        private enum RoadVariantKind
        {
            Surface,
            Elevated,
            Bridge,
            Slope,
            Tunnel
        }
    }

    internal sealed class BusRouteRoadUpgradePlan
    {
        private static int s_nextPlanId;

        public readonly int PlanId;
        public readonly BusRouteRoadUpgradeResult Result = new BusRouteRoadUpgradeResult();
        public readonly List<BusRouteRoadUpgradeGroup> Groups = new List<BusRouteRoadUpgradeGroup>();

        public BusRouteRoadUpgradePlan()
        {
            PlanId = ++s_nextPlanId;
        }

        public int TotalSegmentCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < Groups.Count; i++)
                    count += Groups[i].SegmentCount;
                return count;
            }
        }

        public string ToPreviewStatusText()
        {
            if (Result.LiveBusLineCount <= 0)
                return "Road upgrades: no complete bus lines";
            if (Groups.Count <= 0)
                return "Road upgrades: no safe RON batches";

            return "Road upgrades: review " + Groups.Count + " RON batches, " + TotalSegmentCount + " segments";
        }

        public string ToConfirmationMessage()
        {
            string message =
                "APT found " + TotalSegmentCount + " bus-route road segments that RON can upgrade.\n\n" +
                "RON will change these same-width, same-direction road types:\n";

            int previewCount = Mathf.Min(Groups.Count, 8);
            for (int i = 0; i < previewCount; i++)
            {
                BusRouteRoadUpgradeGroup group = Groups[i];
                message += "- " + group.CurrentName + " -> " + group.TargetName + ": " + group.SegmentCount + " segments\n";
            }

            if (Groups.Count > previewCount)
                message += "- " + (Groups.Count - previewCount) + " more road-type batches\n";

            message += "\nSkipped: " + Result.AlreadyBusLaneSkipped + " already had bus lanes, " +
                Result.UnsafeSkipped + " were unsafe/special, and " +
                Result.NoTargetSkipped + " had no same-width same-variant vanilla bus-lane match.\n\n" +
                "Back up the save first. Apply these RON batches now?";

            return message;
        }
    }

    internal sealed class BusRouteRoadUpgradeApplyState
    {
        public readonly BusRouteRoadUpgradePlan Plan;
        public readonly BusRouteRoadUpgradeResult Result;
        public readonly RonNetworkReplacer Ron;
        public int GroupIndex;
        public int SegmentIndex;
        public List<BusRouteRoadUpgradeService.BusRouteRoadUpgradeRepairCandidate> PendingPostUpgradeRepairCandidates;
        public bool Completed;
        public bool StopCacheCleared;

        public BusRouteRoadUpgradeApplyState(
            BusRouteRoadUpgradePlan plan,
            BusRouteRoadUpgradeResult result,
            RonNetworkReplacer ron)
        {
            Plan = plan;
            Result = result ?? new BusRouteRoadUpgradeResult();
            Ron = ron;
        }

        public string ToProgressStatusText()
        {
            int total = Plan != null ? Plan.TotalSegmentCount : 0;
            int processed = Result.Applied + Result.Failed + Result.StaleSkipped;
            if (PendingPostUpgradeRepairCandidates != null)
                return "Road upgrades: checking RON chunk " + processed + "/" + total;

            return "Road upgrades: applying RON " + processed + "/" + total;
        }
    }

    internal sealed class BusRouteRoadUpgradeGroup
    {
        public readonly NetInfo CurrentInfo;
        public readonly NetInfo TargetInfo;
        public readonly string CurrentName;
        public readonly string TargetName;
        public readonly List<ushort> SegmentIds = new List<ushort>();
        public int BusLineUseCount;
        public int MaxBusLineCount;
        public float TotalLength;

        public BusRouteRoadUpgradeGroup(NetInfo currentInfo, NetInfo targetInfo)
        {
            CurrentInfo = currentInfo;
            TargetInfo = targetInfo;
            CurrentName = BusRouteRoadUpgradeService.GetNetInfoName(currentInfo);
            TargetName = BusRouteRoadUpgradeService.GetNetInfoName(targetInfo);
        }

        public int SegmentCount
        {
            get { return SegmentIds.Count; }
        }
    }

    internal sealed class BusRouteRoadUpgradeResult
    {
        public int LiveBusLineCount;
        public int StopLinkCount;
        public int PathUnitCount;
        public int PathPositionCount;
        public int PathIssueCount;
        public int AlreadyBusLaneSkipped;
        public int UnsafeSkipped;
        public int NonRoadSkipped;
        public int NoTargetSkipped;
        public int StaleSkipped;
        public int CandidateCount;
        public int GroupCount;
        public int RonBatchCount;
        public int Applied;
        public int Failed;
        public int PostUpgradeAuditCount;
        public int PostUpgradeCollapsedDetected;
        public int PostUpgradeCollapsedRepaired;
        public int PostUpgradeRepairFailed;
        public int PostUpgradeRepairStale;
        private readonly Dictionary<string, int> _noTargetBySource = new Dictionary<string, int>();

        public void RecordNoTarget(NetInfo source)
        {
            string name = BusRouteRoadUpgradeService.GetNetInfoName(source);
            int count;
            _noTargetBySource.TryGetValue(name, out count);
            _noTargetBySource[name] = count + 1;
        }

        public void LogNoTargetPreview()
        {
            if (_noTargetBySource.Count == 0)
                return;

            var rows = new List<KeyValuePair<string, int>>(_noTargetBySource);
            rows.Sort((a, b) =>
            {
                int countCompare = b.Value.CompareTo(a.Value);
                if (countCompare != 0)
                    return countCompare;

                return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });

            int previewCount = Mathf.Min(rows.Count, BusRouteRoadUpgradeService.MaxNoTargetLogItems);
            for (int i = 0; i < previewCount; i++)
                TransitLogging.Log("Route road-upgrade no-target source " + (i + 1) + ": " + rows[i].Key + ", segments=" + rows[i].Value + ".");

            if (rows.Count > previewCount)
                TransitLogging.Log("Route road-upgrade no-target source log truncated; remaining=" + (rows.Count - previewCount) + ".");
        }

        public BusRouteRoadUpgradeResult CloneForApply()
        {
            return new BusRouteRoadUpgradeResult
            {
                LiveBusLineCount = LiveBusLineCount,
                StopLinkCount = StopLinkCount,
                PathUnitCount = PathUnitCount,
                PathPositionCount = PathPositionCount,
                PathIssueCount = PathIssueCount,
                AlreadyBusLaneSkipped = AlreadyBusLaneSkipped,
                UnsafeSkipped = UnsafeSkipped,
                NonRoadSkipped = NonRoadSkipped,
                NoTargetSkipped = NoTargetSkipped,
                CandidateCount = CandidateCount,
                GroupCount = GroupCount
            };
        }

        public string ToStatusText()
        {
            if (LiveBusLineCount <= 0)
                return "Road upgrades: no complete bus lines";

            if (Applied <= 0 && Failed <= 0)
                return "Road upgrades: no safe RON batches";

            string status = "Road upgrades: RON requested " + Applied + ", failed " + Failed +
                ", batches " + RonBatchCount;
            if (PostUpgradeCollapsedDetected > 0 || PostUpgradeRepairFailed > 0)
            {
                status += ", collapse repairs " + PostUpgradeCollapsedRepaired + "/" +
                    PostUpgradeCollapsedDetected;
            }

            return status;
        }
    }

    internal sealed class RonNetworkReplacer
    {
        private readonly MethodInfo _replaceNetsMethod;
        private readonly string _unavailableReason;

        public RonNetworkReplacer()
        {
            _replaceNetsMethod = FindReplaceNetsMethod(out _unavailableReason);
        }

        public bool IsAvailable
        {
            get { return _replaceNetsMethod != null; }
        }

        public string UnavailableReason
        {
            get { return _unavailableReason ?? "RON is ready"; }
        }

        public bool TryReplace(NetInfo target, NetInfo replacement, List<ushort> segmentIds, out string error)
        {
            error = null;

            if (_replaceNetsMethod == null)
            {
                error = UnavailableReason;
                return false;
            }

            if (target == null || replacement == null || segmentIds == null || segmentIds.Count == 0)
            {
                error = "invalid RON replacement arguments";
                return false;
            }

            try
            {
                _replaceNetsMethod.Invoke(null, new object[] { target, replacement, segmentIds, false });
                return true;
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                error = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static MethodInfo FindReplaceNetsMethod(out string unavailableReason)
        {
            unavailableReason = null;

            Assembly ronAssembly = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                AssemblyName name = assembly.GetName();
                if (name != null && string.Equals(name.Name, "RON", StringComparison.OrdinalIgnoreCase))
                {
                    ronAssembly = assembly;
                    break;
                }
            }

            if (ronAssembly == null)
            {
                unavailableReason = "RON assembly is not loaded; subscribe to and enable RON, the network replacer";
                return null;
            }

            Type replacerType = ronAssembly.GetType("RON.Replacer", false);
            if (replacerType == null)
            {
                unavailableReason = "RON.Replacer type was not found";
                return null;
            }

            MethodInfo method = replacerType.GetMethod(
                "ReplaceNets",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                unavailableReason = "RON.ReplaceNets method was not found";
                return null;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 4)
            {
                unavailableReason = "RON.ReplaceNets signature was not recognized";
                return null;
            }

            return method;
        }
    }
}
