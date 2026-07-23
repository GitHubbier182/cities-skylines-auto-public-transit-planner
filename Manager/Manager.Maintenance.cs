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
        public bool HasCompletePublicBusLines()
        {
            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return false;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0)
                    continue;

                if (!line.Complete)
                    continue;

                TransportInfo info = line.Info;
                if (info != null &&
                    info.m_transportType == TransportInfo.TransportType.Bus &&
                    !IsProtectedSchoolBusRoute(lineId, ref line))
                {
                    return true;
                }
            }

            return false;
        }

        private List<ExistingLineSnapshot> MaintainExistingBusNetwork(
            AutoPublicTransitConfig cfg,
            List<Vector3> transitHubs,
            List<Vector3> touristAnchors,
            BusStopLocator stopLocator,
            TransitScanSummary scanSummary)
        {
            var existingLines = new List<ExistingLineSnapshot>();
            TransportManager tm = TransportManager.instance;
            var stats = new MaintenanceStats();

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                    continue;

                bool protectedSchoolBusRoute = IsProtectedSchoolBusRoute(lineId, ref line);
                bool playerProtectedLine = IsPlayerProtectedLine(lineId, ref line);
                List<Vector3> stops = GetExistingLineStops(ref line);
                if (protectedSchoolBusRoute || playerProtectedLine)
                {
                    if (protectedSchoolBusRoute)
                        stats.ProtectedSchoolBusLines++;
                    if (playerProtectedLine)
                        stats.PlayerProtectedLines++;
                    if (stops.Count > 0)
                        existingLines.Add(BuildCoverageOnlyLineSnapshot(lineId, stops, protectedSchoolBusRoute, playerProtectedLine));

                    continue;
                }

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) == (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden))
                {
                    SafeReleaseLine(lineId);
                    stats.RemovedLines++;
                    stats.RemovedIncompleteFragments++;
                    TransitLogging.Log("Released stale hidden generated-line probe id=" + lineId + " during maintenance.");
                    continue;
                }

                string integrityFailure;
                if (TryGetExistingLineIntegrityFailure(ref line, lineId, stops, cfg, stopLocator, out integrityFailure))
                {
                    CountIntegrityFailure(stats, integrityFailure);
                    if (IsUnrecoverableLineFragment(ref line, stops, integrityFailure))
                    {
                        SafeReleaseLine(lineId);
                        stats.RemovedLines++;
                        stats.RemovedIncompleteFragments++;
                        TransitLogging.Log("Released unrecoverable bus-line fragment " + DescribeLineForMaintenance(lineId, stops != null ? stops.Count : 0) + "; reason=" + integrityFailure + ".");
                    }
                    else
                    {
                        RefreshExistingLinePaths(lineId, ref line, integrityFailure, stats);
                        int[] deferredStopScores = ScoreLineStops(stops, transitHubs, touristAnchors, cfg);
                        LineMaintenanceProfile deferredProfile = BuildLineMaintenanceProfile(ref line, lineId, stops, deferredStopScores, transitHubs, touristAnchors, cfg);
                        TryRetainFlaggedLineCoverage(existingLines, lineId, stops, deferredProfile, stats);
                    }

                    continue;
                }

                int[] stopScores = ScoreLineStops(stops, transitHubs, touristAnchors, cfg);
                LineMaintenanceProfile profile = BuildLineMaintenanceProfile(ref line, lineId, stops, stopScores, transitHubs, touristAnchors, cfg);

                if (ShouldReleaseVeryWeakLine(profile, cfg))
                    stats.VeryWeakLinesFlagged++;

                if (profile.DemandScore < cfg.DemandThreshold)
                {
                    if (HasUsefulRidership(profile, cfg))
                        stats.RidershipProtectedLines++;
                    else if (profile.StrategicStopCount > 0)
                        stats.StrategicProtectedLines++;
                }

                if (IsWeaklyLoadedForVehicleCount(profile))
                    stats.WeakOversuppliedLines++;

                existingLines.Add(BuildExistingLineSnapshot(lineId, stops, profile));
            }

            stats.WeakDuplicateLinesFlagged = CountWeakDuplicateLines(existingLines, cfg);
            if (scanSummary != null)
            {
                scanSummary.ExistingLinesRetained = existingLines.Count;
                scanSummary.LinesRemoved = stats.RemovedLines;
                scanSummary.VeryWeakLinesRemoved = 0;
                scanSummary.InvalidAnchorLines = stats.InvalidAnchorLines;
                scanSummary.BrokenPathLines = stats.BrokenPathLines;
                scanSummary.ComplexShapeLines = stats.ComplexShapeLines;
                scanSummary.PrunedStops = 0;
                scanSummary.WeakDuplicateLinesRetired = 0;
                scanSummary.RidershipProtectedLines = stats.RidershipProtectedLines;
                scanSummary.StrategicProtectedLines = stats.StrategicProtectedLines;
                scanSummary.WeakOversuppliedLines = stats.WeakOversuppliedLines;
                scanSummary.RidershipProtectedStops = stats.RidershipProtectedStops;
                scanSummary.MaintenanceDeferredLineReleases = 0;
                scanSummary.MaintenanceRetainedIssueLines = stats.RetainedIssueLines;
            }

            TransitLogging.Log("Existing complete bus lines retained after maintenance: " + existingLines.Count + ".");
            if (stats.RemovedLines > 0)
                TransitLogging.Log("Removed " + stats.RemovedLines + " incomplete generated probes/fragments; live service lines were retained.");
            if (stats.RetainedIssueLines > 0)
                TransitLogging.Log("Retained " + stats.RetainedIssueLines + " live bus lines with audit issues as coverage to avoid depot churn; pathRefreshRequests=" + stats.PathRefreshRequests + ".");
            if (stats.InvalidAnchorLines > 0)
                TransitLogging.Log("Invalid bus-stop anchors found on " + stats.InvalidAnchorLines + " retained-line candidates; live lines were not deleted by maintenance.");
            if (stats.BrokenPathLines > 0)
                TransitLogging.Log("Broken bus-line paths found on " + stats.BrokenPathLines + " retained-line candidates; live lines were refreshed and retained.");
            if (stats.ComplexShapeLines > 0)
                TransitLogging.Log("Overly complex bus-line shapes found on " + stats.ComplexShapeLines + " retained-line candidates; live lines were retained for advisory rebuild.");
            if (stats.ProtectedSchoolBusLines > 0)
                TransitLogging.Log("Protected " + stats.ProtectedSchoolBusLines + " school bus route(s) as coverage-only external service.");
            if (stats.PlayerProtectedLines > 0)
                TransitLogging.Log("Protected " + stats.PlayerProtectedLines + " DND-marked player bus line(s) as coverage-only service.");
            TransitLogging.Log(
                "Maintenance ridership decisions: keptByRidership=" + stats.RidershipProtectedLines +
                ", keptByStrategicValue=" + stats.StrategicProtectedLines +
                ", veryWeakFlagged=" + stats.VeryWeakLinesFlagged +
                ", weakDuplicatesFlagged=" + stats.WeakDuplicateLinesFlagged +
                ", weakOversupplied=" + stats.WeakOversuppliedLines +
                ", stopsProtectedByRidership=" + stats.RidershipProtectedStops + ".");

            return existingLines;
        }

        private bool TryGetExistingLineIntegrityFailure(
            ref TransportLine line,
            ushort lineId,
            List<Vector3> stops,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out string failureReason)
        {
            if (!line.Complete)
            {
                failureReason = "incomplete";
                return true;
            }

            if (stops == null || stops.Count < 2)
            {
                failureReason = "too-few-stops";
                return true;
            }

            if ((line.m_flags & TransportLine.Flags.Invalid) != 0)
            {
                failureReason = "game-invalid";
                return true;
            }

            if (TryGetLinePathFailure(ref line, lineId, stops.Count, out failureReason))
                return true;

            if (!AreLineStopsStillValid(stops, cfg, stopLocator))
            {
                failureReason = "invalid-stop-anchor";
                return true;
            }

            string shapeReason;
            if (RouteShapeAnalyzer.TryGetShapeProblem(stops, cfg, out shapeReason))
            {
                failureReason = "complex-route-" + shapeReason;
                return true;
            }

            failureReason = null;
            return false;
        }

        private bool TryGetLinePathFailure(ref TransportLine line, ushort lineId, int stopCount, out string failureReason)
        {
            if ((line.m_flags & TransportLine.Flags.Invalid) != 0)
            {
                failureReason = "game-invalid";
                return true;
            }

            int count = Mathf.Max(0, stopCount);
            for (int i = 0; i < count; i++)
            {
                bool failed;
                line.CheckPrevPath(i, out failed);
                if (failed)
                {
                    failureReason = "prev-path-failed line=" + lineId + " stopIndex=" + i;
                    return true;
                }

                line.CheckNextPath(i, out failed);
                if (failed)
                {
                    failureReason = "next-path-failed line=" + lineId + " stopIndex=" + i;
                    return true;
                }
            }

            failureReason = null;
            return false;
        }

        private void CountIntegrityFailure(MaintenanceStats stats, string failureReason)
        {
            if (failureReason != null && failureReason.StartsWith("complex-route-", StringComparison.Ordinal))
                stats.ComplexShapeLines++;
            else if (failureReason == "invalid-stop-anchor" || failureReason == "too-few-stops" || failureReason == "incomplete")
                stats.InvalidAnchorLines++;
            else
                stats.BrokenPathLines++;

            TransitLogging.Verbose("Existing bus line integrity audit failed: " + failureReason + ".");
        }

        private bool IsUnrecoverableLineFragment(ref TransportLine line, List<Vector3> stops, string failureReason)
        {
            if (stops == null || stops.Count < 2)
                return true;

            return failureReason == "incomplete" && line.m_stops == 0;
        }

        private void RefreshExistingLinePaths(ushort lineId, ref TransportLine line, string reason, MaintenanceStats stats)
        {
            if (reason == null || reason.IndexOf("path", StringComparison.Ordinal) < 0)
                return;

            try
            {
                line.UpdatePaths(lineId);
                TransportManager.instance.UpdateLine(lineId);
                stats.PathRefreshRequests++;
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to request path refresh for retained bus line " + lineId + ": " + e.Message);
            }
        }

        private bool TryRetainFlaggedLineCoverage(
            List<ExistingLineSnapshot> existingLines,
            ushort lineId,
            List<Vector3> stops,
            LineMaintenanceProfile profile,
            MaintenanceStats stats)
        {
            if (existingLines == null || stops == null || stops.Count < 2)
                return false;

            existingLines.Add(BuildExistingLineSnapshot(lineId, stops, profile));
            stats.RetainedIssueLines++;
            return true;
        }

        private ExistingLineSnapshot BuildExistingLineSnapshot(ushort lineId, List<Vector3> stops, LineMaintenanceProfile profile)
        {
            return new ExistingLineSnapshot
            {
                LineId = lineId,
                Stops = stops,
                TotalLength = profile.RouteLength,
                DemandScore = profile.DemandScore,
                AverageRidership = profile.AverageRidership,
                VehicleCount = profile.VehicleCount,
                StrategicStopCount = profile.StrategicStopCount
            };
        }

        private ExistingLineSnapshot BuildCoverageOnlyLineSnapshot(ushort lineId, List<Vector3> stops, bool protectedSchoolBusRoute, bool playerProtectedLine)
        {
            return new ExistingLineSnapshot
            {
                LineId = lineId,
                Stops = stops ?? new List<Vector3>(),
                TotalLength = stops != null && stops.Count >= 2 ? ComputeRouteLength(stops) : 0f,
                IsProtectedSchoolBusRoute = protectedSchoolBusRoute,
                IsPlayerProtectedLine = playerProtectedLine
            };
        }

        private string DescribeLineForMaintenance(ushort lineId, int stopCount)
        {
            try
            {
                TransportManager tm = TransportManager.instance;
                if (tm == null || lineId == 0 || lineId >= tm.m_lines.m_size)
                    return "id=" + lineId;

                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                string publicNumber = line.m_lineNumber > 0 ? ", public=#" + line.m_lineNumber : "";
                string stops = stopCount >= 0 ? ", stops=" + stopCount : "";
                string vehicles = ", vehicles=" + CountLineVehicles(ref line, lineId);
                return "id=" + lineId + publicNumber + stops + vehicles;
            }
            catch
            {
                return "id=" + lineId;
            }
        }

        private LineMaintenanceProfile BuildLineMaintenanceProfile(
            ref TransportLine line,
            ushort lineId,
            List<Vector3> stops,
            int[] stopScores,
            List<Vector3> transitHubs,
            List<Vector3> touristAnchors,
            AutoPublicTransitConfig cfg)
        {
            var profile = new LineMaintenanceProfile();
            profile.DemandScore = SumScores(stopScores);
            profile.AverageRidership = GetAverageRidership(ref line);
            profile.VehicleCount = CountLineVehicles(ref line, lineId);
            profile.RouteLength = stops != null && stops.Count >= 2 ? ComputeRouteLength(stops) : 0f;
            profile.StrategicStopCount = CountStrategicStops(stops, transitHubs, touristAnchors, cfg);
            return profile;
        }

        private int CountStrategicStops(List<Vector3> stops, List<Vector3> transitHubs, List<Vector3> touristAnchors, AutoPublicTransitConfig cfg)
        {
            if (stops == null || stops.Count == 0)
                return 0;

            int count = 0;
            for (int i = 0; i < stops.Count; i++)
            {
                if (IsProtectedMaintenanceStop(stops[i], transitHubs, touristAnchors, cfg))
                    count++;
            }

            return count;
        }

        private bool ShouldReleaseVeryWeakLine(LineMaintenanceProfile profile, AutoPublicTransitConfig cfg)
        {
            if (profile.DemandScore >= cfg.DemandThreshold)
                return false;

            if (HasUsefulRidership(profile, cfg))
                return false;

            if (profile.StrategicStopCount > 0 && profile.DemandScore >= Mathf.Max(1, cfg.DemandThreshold / 2))
                return false;

            return profile.AverageRidership <= GetWeakRidershipThreshold(cfg);
        }

        private bool HasUsefulRidership(LineMaintenanceProfile profile, AutoPublicTransitConfig cfg)
        {
            return profile.AverageRidership >= GetUsefulRidershipThreshold(cfg);
        }

        private int GetWeakRidershipThreshold(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(3, cfg.DemandThreshold);
        }

        private int GetUsefulRidershipThreshold(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(8, cfg.DemandThreshold * 2);
        }

        private bool IsWeaklyLoadedForVehicleCount(LineMaintenanceProfile profile)
        {
            if (profile.VehicleCount <= 0)
                return false;

            return (float)profile.AverageRidership / profile.VehicleCount < 3f;
        }

        private int CountLineVehicles(ref TransportLine line, ushort lineId)
        {
            try
            {
                return line.CountVehicles(lineId);
            }
            catch
            {
                return 0;
            }
        }

        private int CountWeakDuplicateLines(List<ExistingLineSnapshot> existingLines, AutoPublicTransitConfig cfg)
        {
            if (existingLines == null || existingLines.Count < 2)
                return 0;

            var duplicateLineIds = new HashSet<ushort>();
            for (int i = 0; i < existingLines.Count; i++)
            {
                ExistingLineSnapshot candidate = existingLines[i];
                if (!IsManagedExistingLine(candidate))
                    continue;

                if (!IsWeakDuplicateCandidate(candidate, cfg))
                    continue;

                for (int j = 0; j < existingLines.Count; j++)
                {
                    if (i == j)
                        continue;

                    ExistingLineSnapshot other = existingLines[j];
                    if (!IsManagedExistingLine(other))
                        continue;

                    if (!IsStrongerDuplicateLine(candidate, other, cfg))
                        continue;

                    duplicateLineIds.Add(candidate.LineId);
                    break;
                }
            }

            return duplicateLineIds.Count;
        }

        private bool IsWeakDuplicateCandidate(ExistingLineSnapshot candidate, AutoPublicTransitConfig cfg)
        {
            if (candidate == null)
                return false;

            if (candidate.AverageRidership >= GetUsefulRidershipThreshold(cfg))
                return false;

            if (candidate.StrategicStopCount > 0 && candidate.DemandScore >= cfg.DemandThreshold)
                return false;

            return candidate.DemandScore < cfg.DemandThreshold * Mathf.Max(2, cfg.MinStopsPerRoute);
        }

        private bool IsStrongerDuplicateLine(ExistingLineSnapshot candidate, ExistingLineSnapshot other, AutoPublicTransitConfig cfg)
        {
            if (candidate == null || other == null)
                return false;

            if (GetStopOverlapRatio(candidate.Stops, other.Stops, cfg) < 0.75f)
                return false;

            if (other.AverageRidership > candidate.AverageRidership)
                return true;

            if (other.AverageRidership == candidate.AverageRidership && other.DemandScore > candidate.DemandScore)
                return true;

            return other.DemandScore == candidate.DemandScore
                && other.StrategicStopCount > candidate.StrategicStopCount;
        }

        private bool AreLineStopsStillValid(List<Vector3> stops, AutoPublicTransitConfig cfg, BusStopLocator stopLocator)
        {
            if (stops == null || stops.Count < 2)
                return false;

            float maxDistance = Mathf.Max(180f, cfg.MaxWalkingDistance * 0.85f);
            for (int i = 0; i < stops.Count; i++)
            {
                if (!stopLocator.IsValidBusStopPosition(stops[i], maxDistance))
                    return false;
            }

            return true;
        }

        private int[] ScoreLineStops(List<Vector3> stops, List<Vector3> transitHubs, List<Vector3> touristAnchors, AutoPublicTransitConfig cfg)
        {
            int[] scores = new int[stops != null ? stops.Count : 0];
            if (stops == null || stops.Count == 0)
                return scores;

            BuildingManager bm = BuildingManager.instance;
            var buffer = bm.m_buildings.m_buffer;
            float scoreDistance = cfg.MaxWalkingDistance;

            for (ushort id = 1; id < buffer.Length; id++)
            {
                ref Building building = ref buffer[id];
                if (building.m_flags == 0 || !IsDemandService(ref building))
                    continue;

                int weight = ScoreBuilding(id, ref building);
                if (weight <= 0)
                    continue;

                int nearestStop = FindNearestStopIndex(building.m_position, stops, scoreDistance);
                if (nearestStop != -1)
                    scores[nearestStop] += weight;
            }

            AddAnchorScores(scores, stops, transitHubs, Mathf.Max(cfg.DemandThreshold + 10, 22), cfg);
            AddAnchorScores(scores, stops, touristAnchors, Mathf.Max(cfg.DemandThreshold + 8, 20), cfg);
            return scores;
        }

        private void AddAnchorScores(int[] scores, List<Vector3> stops, List<Vector3> anchors, int weight, AutoPublicTransitConfig cfg)
        {
            if (scores == null || stops == null || anchors == null || anchors.Count == 0)
                return;

            float anchorDistance = cfg.MaxWalkingDistance * 1.2f;
            for (int i = 0; i < anchors.Count; i++)
            {
                int nearestStop = FindNearestStopIndex(anchors[i], stops, anchorDistance);
                if (nearestStop != -1)
                    scores[nearestStop] += weight;
            }
        }

        private int FindNearestStopIndex(Vector3 pos, List<Vector3> stops, float maxDistance)
        {
            int bestIndex = -1;
            float bestSqr = maxDistance * maxDistance;

            for (int i = 0; i < stops.Count; i++)
            {
                float dx = stops[i].x - pos.x;
                float dz = stops[i].z - pos.z;
                float sqr = dx * dx + dz * dz;
                if (sqr >= bestSqr)
                    continue;

                bestSqr = sqr;
                bestIndex = i;
            }

            return bestIndex;
        }

        private int SumScores(int[] scores)
        {
            int total = 0;
            if (scores == null)
                return total;

            for (int i = 0; i < scores.Length; i++)
                total += scores[i];

            return total;
        }

        private int RemoveWeakStopsFromLine(
            ushort lineId,
            List<Vector3> stops,
            int[] stopScores,
            LineMaintenanceProfile profile,
            List<Vector3> transitHubs,
            List<Vector3> touristAnchors,
            AutoPublicTransitConfig cfg,
            MaintenanceStats stats)
        {
            if (stops == null || stopScores == null || stops.Count != stopScores.Length)
                return 0;

            if (stops.Count <= Mathf.Max(2, cfg.MinStopsPerRoute))
                return 0;

            TransportManager tm = TransportManager.instance;
            int removed = 0;
            int remainingStops = stops.Count;

            for (int i = stops.Count - 1; i >= 0; i--)
            {
                if (remainingStops <= Mathf.Max(2, cfg.MinStopsPerRoute))
                    break;

                if (stopScores[i] >= cfg.DemandThreshold)
                    continue;

                if (HasUsefulRidership(profile, cfg))
                {
                    stats.RidershipProtectedStops++;
                    continue;
                }

                if (IsProtectedMaintenanceStop(stops[i], transitHubs, touristAnchors, cfg))
                    continue;

                if (!CanRemoveStopWithoutLargeGap(stops, i, cfg))
                    continue;

                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if (!line.RemoveStop(lineId, i))
                    continue;

                stops.RemoveAt(i);
                removed++;
                remainingStops--;
            }

            return removed;
        }

        private bool IsProtectedMaintenanceStop(Vector3 stop, List<Vector3> transitHubs, List<Vector3> touristAnchors, AutoPublicTransitConfig cfg)
        {
            float protectedDistance = Mathf.Max(96f, cfg.MaxWalkingDistance * 0.65f);
            return IsNearAnyStop(stop, transitHubs, protectedDistance)
                || IsNearAnyStop(stop, touristAnchors, protectedDistance);
        }

        private bool CanRemoveStopWithoutLargeGap(List<Vector3> stops, int removeIndex, AutoPublicTransitConfig cfg)
        {
            if (stops == null || stops.Count <= 2)
                return false;

            int previous = (removeIndex - 1 + stops.Count) % stops.Count;
            int next = (removeIndex + 1) % stops.Count;
            float gap = Geometry.DistanceXZ(stops[previous], stops[next]);
            return gap <= cfg.MaxRoadDistance * 1.35f;
        }

        private List<Vector3> GetExistingLineStops(ref TransportLine line)
        {
            var stops = new List<Vector3>();
            ushort firstStop = line.m_stops;
            if (firstStop == 0)
                return stops;

            NetManager nm = NetManager.instance;
            ushort currentStop = firstStop;
            int safety = 0;

            do
            {
                if (currentStop == 0)
                    break;

                stops.Add(nm.m_nodes.m_buffer[currentStop].m_position);
                currentStop = TransportLine.GetNextStop(currentStop);
                safety++;
            }
            while (currentStop != 0 && currentStop != firstStop && safety < 512);

            return stops;
        }

        private int ApplyExistingCoverageDiscount(int weight, Vector3 buildingPos, List<Vector3> existingStops, AutoPublicTransitConfig cfg)
        {
            if (existingStops == null || existingStops.Count == 0)
                return weight;

            float fullServiceDistance = cfg.MaxWalkingDistance * 0.7f;
            float partialServiceDistance = cfg.MaxWalkingDistance * 1.2f;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < existingStops.Count; i++)
            {
                float dist = Geometry.DistanceXZ(buildingPos, existingStops[i]);
                if (dist < bestDistance)
                    bestDistance = dist;
            }

            if (bestDistance <= fullServiceDistance)
                return 0;

            if (bestDistance <= partialServiceDistance)
                return Mathf.Max(1, weight / 3);

            return weight;
        }

        private bool IsRouteDuplicate(List<Vector3> lineStops, List<ExistingLineSnapshot> existingLines, AutoPublicTransitConfig cfg)
        {
            if (existingLines == null || existingLines.Count == 0)
                return false;

            float overlapDistance = Mathf.Max(50f, cfg.MaxWalkingDistance * 0.45f);
            float requiredOverlap = 0.65f;

            for (int i = 0; i < existingLines.Count; i++)
            {
                if (!IsManagedExistingLine(existingLines[i]))
                    continue;

                List<Vector3> existingStops = existingLines[i].Stops;
                int overlappingStops = 0;

                for (int j = 0; j < lineStops.Count; j++)
                {
                    if (IsNearAnyStop(lineStops[j], existingStops, overlapDistance))
                        overlappingStops++;
                }

                float overlapRatio = (float)overlappingStops / lineStops.Count;
                if (overlapRatio >= requiredOverlap)
                    return true;
            }

            return false;
        }

        private bool IsNearAnyStop(Vector3 pos, List<Vector3> stops, float maxDistance)
        {
            for (int i = 0; i < stops.Count; i++)
            {
                if (Geometry.DistanceXZ(pos, stops[i]) <= maxDistance)
                    return true;
            }

            return false;
        }

        private bool IsRouteEfficient(List<Vector3> lineStops, AutoPublicTransitConfig cfg)
        {
            return IsRouteEfficient(lineStops, cfg, true);
        }

        private bool IsRouteEfficient(List<Vector3> lineStops, AutoPublicTransitConfig cfg, bool strictShape)
        {
            if (lineStops == null || lineStops.Count < 2)
                return false;

            string shapeReason;
            if (RouteShapeAnalyzer.TryGetShapeProblem(lineStops, cfg, out shapeReason))
            {
                if (strictShape)
                    return false;

                if (shapeReason == "too-few-stops" ||
                    shapeReason == "too-long" ||
                    shapeReason == "zero-span" ||
                    shapeReason == "self-crossing" ||
                    shapeReason == "repeated-stops")
                {
                    return false;
                }
            }

            float routeLength = ComputeRouteLength(lineStops);
            if (routeLength > cfg.MaxLineLengthKm * 1000f)
                return false;

            float span = ComputeRouteSpan(lineStops);
            if (span <= 0.1f)
                return false;

            float circuity = routeLength / span;
            float circuityLimit = strictShape ? 2.6f : 3.8f;
            if (circuity > circuityLimit)
                return false;

            int sharpReversals = 0;
            for (int i = 1; i < lineStops.Count - 1; i++)
            {
                Vector2 a = Geometry.ToXZ(lineStops[i] - lineStops[i - 1]);
                Vector2 b = Geometry.ToXZ(lineStops[i + 1] - lineStops[i]);
                if (a.sqrMagnitude < 1f || b.sqrMagnitude < 1f)
                    continue;

                float angle = Mathf.Abs(Geometry.SignedAngle(a.normalized, b.normalized));
                if (angle > 150f)
                    sharpReversals++;
            }

            if (HasExcessiveSelfOverlap(lineStops, cfg))
                return false;

            int reversalLimit = strictShape ? 2 : 4;
            return sharpReversals < reversalLimit;
        }

        private class MaintenanceStats
        {
            public int RemovedLines;
            public int RemovedIncompleteFragments;
            public int InvalidAnchorLines;
            public int BrokenPathLines;
            public int ComplexShapeLines;
            public int RetainedIssueLines;
            public int PathRefreshRequests;
            public int VeryWeakLinesFlagged;
            public int WeakDuplicateLinesFlagged;
            public int RidershipProtectedLines;
            public int StrategicProtectedLines;
            public int WeakOversuppliedLines;
            public int RidershipProtectedStops;
            public int ProtectedSchoolBusLines;
            public int PlayerProtectedLines;
        }

        private struct LineMaintenanceProfile
        {
            public int DemandScore;
            public int AverageRidership;
            public int VehicleCount;
            public int StrategicStopCount;
            public float RouteLength;
        }

        private bool HasExcessiveSelfOverlap(List<Vector3> lineStops, AutoPublicTransitConfig cfg)
        {
            float overlapDistance = Mathf.Max(60f, cfg.GridCellSize * 0.5f);
            int nearRepeats = 0;

            for (int i = 0; i < lineStops.Count; i++)
            {
                for (int j = i + 2; j < lineStops.Count; j++)
                {
                    if (i == 0 && j == lineStops.Count - 1)
                        continue;

                    if (Geometry.DistanceXZ(lineStops[i], lineStops[j]) <= overlapDistance)
                        nearRepeats++;
                }
            }

            return nearRepeats > Mathf.Max(1, lineStops.Count / 5);
        }

        private float ComputeRouteLength(List<Vector3> lineStops)
        {
            float total = 0f;
            for (int i = 1; i < lineStops.Count; i++)
            {
                total += Geometry.DistanceXZ(lineStops[i - 1], lineStops[i]);
            }

            total += Geometry.DistanceXZ(lineStops[lineStops.Count - 1], lineStops[0]);
            return total;
        }

        private float ComputeRouteSpan(List<Vector3> lineStops)
        {
            float span = 0f;
            for (int i = 0; i < lineStops.Count; i++)
            {
                for (int j = i + 1; j < lineStops.Count; j++)
                {
                    float dist = Geometry.DistanceXZ(lineStops[i], lineStops[j]);
                    if (dist > span)
                        span = dist;
                }
            }

            return span;
        }
    }
}
