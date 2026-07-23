using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace AutoPublicTransit
{
    public partial class Manager : MonoBehaviour
    {
        private static readonly FieldInfo TransportManagerLineNumberField =
            typeof(TransportManager).GetField("m_lineNumber", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PublicTransportDetailPanelBusCountField =
            typeof(PublicTransportDetailPanel).GetField("m_BusCount", BindingFlags.Instance | BindingFlags.NonPublic);
        private const int MaxGeneratedProbeRepairDepth = 2;
        private const int MaxGeneratedVehicleAuditDeferralPasses = 2;
        private const float GeneratedStopReuseDistance = 200f;

        private List<GeneratedLineProbe> BuildGeneratedLineProbes(
            List<List<Vector3>> routes,
            List<ExistingLineSnapshot> existingLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransitScanSummary scanSummary,
            bool resetScanSummary = true)
        {
            var generatedLineProbes = new List<GeneratedLineProbe>();
            TransportManager tm = TransportManager.instance;
            TransportInfo busInfo = GetOrdinaryCityBusTransportInfo(tm, "generated-line-build");
            if (busInfo == null)
            {
                TransitLogging.Error("Bus transport info was not available, so no lines could be created.");
                return generatedLineProbes;
            }

            if (routes == null || routes.Count == 0)
            {
                TransitLogging.Warn("No routes were generated, so no bus lines were created.");
                return generatedLineProbes;
            }

            if (existingLines == null)
                existingLines = new List<ExistingLineSnapshot>();

            var plannedLineShapes = new List<ExistingLineSnapshot>(existingLines);
            int preparedProbes = 0;
            int skippedRoutes = 0;
            int tooShortRoutes = 0;
            int duplicateRoutes = 0;
            int circuitousRoutes = 0;
            int integrityFailedRoutes = 0;
            var randomizer = new Randomizer((uint)DateTime.UtcNow.Ticks);

            foreach (var route in routes)
            {
                List<PreparedRouteCandidate> routeCandidates = PrepareGeneratedRouteCandidates(GetLineStops(route), cfg);
                if (routeCandidates.Count == 0)
                {
                    skippedRoutes++;
                    circuitousRoutes++;
                    TransitLogging.Log("Skipped a generated route because strict shape repair could not produce a buildable candidate.");
                    continue;
                }

                if (routeCandidates.Count > 1)
                    TransitLogging.Log("Split/repaired one generated route into " + routeCandidates.Count + " buildable candidates.");

                for (int candidateIndex = 0; candidateIndex < routeCandidates.Count; candidateIndex++)
                {
                    PreparedRouteCandidate candidate = routeCandidates[candidateIndex];
                    List<Vector3> lineStops = candidate.Stops;
                    bool candidateSimplified = candidate.Adjusted;

                    if (lineStops.Count < Mathf.Max(2, cfg.MinStopsPerRoute))
                    {
                        skippedRoutes++;
                        tooShortRoutes++;
                        TransitLogging.Warn("Skipped a generated route because it did not contain enough distinct stops.");
                        continue;
                    }

                    if (IsRouteDuplicate(lineStops, plannedLineShapes, cfg))
                    {
                        skippedRoutes++;
                        duplicateRoutes++;
                        TransitLogging.Log("Skipped a generated route because it overlaps an existing line too heavily.");
                        continue;
                    }

                    string strictShapeFailure;
                    if (TryGetStrictGeneratedRouteShapeFailure(lineStops, cfg, out strictShapeFailure))
                    {
                        skippedRoutes++;
                        circuitousRoutes++;
                        TransitLogging.Log("Skipped a generated route because it still failed strict shape validation after repair: " + strictShapeFailure + ".");
                        continue;
                    }

                    string hiddenFailure;
                    if (TryAddGeneratedLineProbeFromStops(
                        lineStops,
                        candidateSimplified,
                        candidate.RepairReason,
                        generatedLineProbes,
                        plannedLineShapes,
                        cfg,
                        stopLocator,
                        busInfo,
                        ref randomizer,
                        ref preparedProbes,
                        0,
                        out hiddenFailure))
                    {
                        continue;
                    }

                    int repairedProbes = TryPreparePathRepairSubrouteProbes(
                        lineStops,
                        hiddenFailure,
                        generatedLineProbes,
                        plannedLineShapes,
                        cfg,
                        stopLocator,
                        busInfo,
                        ref randomizer,
                        ref preparedProbes,
                        0);

                    if (repairedProbes > 0)
                    {
                        TransitLogging.Log("Repaired one generated route into " + repairedProbes + " hidden subroute probe(s) after hidden probe failed: " + hiddenFailure + ".");
                        continue;
                    }

                    skippedRoutes++;
                    integrityFailedRoutes++;
                    TransitLogging.Warn("Skipped generated route because a hidden path probe could not be built: " + hiddenFailure + ".");
                }
            }

            if (scanSummary != null && resetScanSummary)
            {
                scanSummary.CreatedLines = 0;
                scanSummary.RepairedGeneratedLines = 0;
                scanSummary.GeneratedRoutesSkipped = skippedRoutes;
                scanSummary.GeneratedRoutesTooShort = tooShortRoutes;
                scanSummary.GeneratedRoutesDuplicate = duplicateRoutes;
                scanSummary.GeneratedRoutesCircuitous = circuitousRoutes;
                scanSummary.GeneratedRoutesIntegrityFailed = integrityFailedRoutes;
                scanSummary.GeneratedStopsSkipped = 0;
                scanSummary.ClosureBackoffs = 0;
                if (scanSummary.CreatedLineIds == null)
                    scanSummary.CreatedLineIds = new List<ushort>();
                else
                    scanSummary.CreatedLineIds.Clear();
            }
            else if (scanSummary != null)
            {
                scanSummary.GeneratedRoutesSkipped += skippedRoutes;
                scanSummary.GeneratedRoutesTooShort += tooShortRoutes;
                scanSummary.GeneratedRoutesDuplicate += duplicateRoutes;
                scanSummary.GeneratedRoutesCircuitous += circuitousRoutes;
                scanSummary.GeneratedRoutesIntegrityFailed += integrityFailedRoutes;
                if (scanSummary.CreatedLineIds == null)
                    scanSummary.CreatedLineIds = new List<ushort>();
            }

            if (preparedProbes == 0)
            {
                TransitLogging.Warn("No hidden bus-line probes were prepared from the generated routes.");
                return generatedLineProbes;
            }

            TransitLogging.Log("Prepared " + preparedProbes + " hidden generated-line probes; real bus lines will be created only after path validation passes.");

            return generatedLineProbes;
        }

        private bool TryGetStrictGeneratedRouteShapeFailure(
            List<Vector3> stops,
            AutoPublicTransitConfig cfg,
            out string failureReason)
        {
            string shapeReason;
            if (RouteShapeAnalyzer.TryGetShapeProblem(stops, cfg, out shapeReason))
            {
                if (!IsHardGeneratedRouteShapeFailure(shapeReason))
                {
                    failureReason = null;
                    return false;
                }

                failureReason = "complex-route-" + shapeReason;
                return true;
            }

            failureReason = null;
            return false;
        }

        private bool IsHardGeneratedRouteShapeFailure(string shapeReason)
        {
            return shapeReason == "too-few-stops" ||
                   shapeReason == "too-long" ||
                   shapeReason == "zero-span" ||
                   shapeReason == "self-crossing" ||
                   shapeReason == "repeated-stops";
        }

        private List<PreparedRouteCandidate> PrepareGeneratedRouteCandidates(List<Vector3> routeStops, AutoPublicTransitConfig cfg)
        {
            var candidates = new List<PreparedRouteCandidate>();
            if (routeStops == null)
                return candidates;

            int originalStopCount = routeStops.Count;
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            if (originalStopCount < minimumStops)
                return candidates;

            int minimumRetainedStops = Mathf.Max(minimumStops, Mathf.CeilToInt(originalStopCount * 0.7f));
            AddLoopOrderRepairCandidates(candidates, routeStops, cfg, "smooth loop", false);

            List<Vector3> preserved = RouteShapeAnalyzer.SimplifyForBuildPreservingOrder(routeStops, cfg, minimumRetainedStops);
            AddPreparedRouteCandidate(candidates, preserved, originalStopCount != preserved.Count, "preserve-order simplify", cfg);

            if (candidates.Count == 0)
            {
                List<Vector3> deeper = RouteShapeAnalyzer.SimplifyForBuildPreservingOrder(routeStops, cfg, minimumStops);
                AddPreparedRouteCandidate(candidates, deeper, originalStopCount != deeper.Count, "deep simplify", cfg);
            }

            if (candidates.Count == 0)
            {
                List<Vector3> optimized = RouteShapeAnalyzer.SimplifyForBuild(routeStops, cfg);
                AddPreparedRouteCandidate(candidates, optimized, true, "optimized simplify", cfg);
            }

            if (candidates.Count == 0)
                AddSplitRouteCandidates(candidates, routeStops, cfg);

            return candidates;
        }

        private void AddSplitRouteCandidates(List<PreparedRouteCandidate> candidates, List<Vector3> routeStops, AutoPublicTransitConfig cfg)
        {
            if (routeStops == null)
                return;

            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            if (routeStops.Count <= minimumStops)
                return;

            int windowSize = Mathf.Min(routeStops.Count, Mathf.Max(minimumStops, 4));
            int stride = Mathf.Max(1, windowSize - 2);
            int maxCandidates = 4;

            for (int start = 0; start < routeStops.Count && candidates.Count < maxCandidates; start += stride)
            {
                if (start + minimumStops > routeStops.Count)
                    break;

                int count = Mathf.Min(windowSize, routeStops.Count - start);
                if (count < minimumStops)
                    break;

                var window = new List<Vector3>();
                for (int i = 0; i < count; i++)
                    window.Add(routeStops[start + i]);

                List<Vector3> simplified = RouteShapeAnalyzer.SimplifyForBuildPreservingOrder(window, cfg, minimumStops);
                AddPreparedRouteCandidate(candidates, simplified, true, "split simplify", cfg);

                if (candidates.Count >= maxCandidates)
                    break;

                List<Vector3> optimized = RouteShapeAnalyzer.SimplifyForBuild(window, cfg);
                AddPreparedRouteCandidate(candidates, optimized, true, "split optimized simplify", cfg);
            }

            if (candidates.Count < maxCandidates && routeStops.Count > windowSize)
            {
                var tail = new List<Vector3>();
                int tailStart = Mathf.Max(0, routeStops.Count - windowSize);
                for (int i = tailStart; i < routeStops.Count; i++)
                    tail.Add(routeStops[i]);

                List<Vector3> tailSimplified = RouteShapeAnalyzer.SimplifyForBuildPreservingOrder(tail, cfg, minimumStops);
                AddPreparedRouteCandidate(candidates, tailSimplified, true, "tail split simplify", cfg);
            }
        }

        private void AddPreparedRouteCandidate(
            List<PreparedRouteCandidate> candidates,
            List<Vector3> stops,
            bool adjusted,
            string repairReason,
            AutoPublicTransitConfig cfg)
        {
            if (candidates == null || stops == null || stops.Count < Mathf.Max(2, cfg.MinStopsPerRoute))
                return;

            string failureReason;
            if (TryGetStrictGeneratedRouteShapeFailure(stops, cfg, out failureReason))
                return;

            if (IsPreparedRouteCandidateDuplicate(candidates, stops, cfg))
                return;

            candidates.Add(new PreparedRouteCandidate
            {
                Stops = new List<Vector3>(stops),
                Adjusted = adjusted,
                RepairReason = repairReason
            });
        }

        private bool TryAddGeneratedLineProbeFromStops(
            List<Vector3> lineStops,
            bool candidateSimplified,
            string repairReason,
            List<GeneratedLineProbe> generatedLineProbes,
            List<ExistingLineSnapshot> plannedLineShapes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransportInfo busInfo,
            ref Randomizer randomizer,
            ref int preparedProbes,
            int repairDepth,
            out string failureReason)
        {
            LineBuildResult result = TryCreateHiddenProbeFromRoute(lineStops, plannedLineShapes, cfg, stopLocator, busInfo, ref randomizer);
            if (!result.Success)
            {
                failureReason = result.FailureReason;
                return false;
            }

            preparedProbes++;
            bool adjusted = result.SkippedStops > 0 || result.ClosureBackoffs > 0 || result.ReusedStops > 0 || candidateSimplified;
            generatedLineProbes.Add(new GeneratedLineProbe
            {
                ProbeLineId = result.LineId,
                Stops = new List<Vector3>(result.Stops),
                Adjusted = adjusted,
                SkippedStops = result.SkippedStops,
                ClosureBackoffs = result.ClosureBackoffs,
                RepairDepth = repairDepth
            });
            plannedLineShapes.Add(new ExistingLineSnapshot
            {
                LineId = result.LineId,
                Stops = new List<Vector3>(result.Stops),
                TotalLength = ComputeRouteLength(result.Stops)
            });

            TransitLogging.Log(
                "Prepared hidden generated-line probe " + result.LineId +
                " with " + result.Stops.Count +
                " stops (" + repairReason +
                ", reusedNearbyStops=" + result.ReusedStops + ").");
            failureReason = null;
            return true;
        }

        private int TryPreparePathRepairSubrouteProbes(
            List<Vector3> lineStops,
            string originalFailure,
            List<GeneratedLineProbe> generatedLineProbes,
            List<ExistingLineSnapshot> plannedLineShapes,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransportInfo busInfo,
            ref Randomizer randomizer,
            ref int preparedProbes,
            int repairDepth)
        {
            if (!IsRepairableHiddenProbeFailure(originalFailure))
                return 0;

            List<PreparedRouteCandidate> repairCandidates = BuildPathRepairSubrouteCandidates(lineStops, cfg);
            int prepared = 0;
            int attempts = 0;
            int maxPrepared = repairDepth > 0 ? 4 : 3;
            int maxAttempts = repairDepth > 0 ? 24 : 16;

            for (int i = 0; i < repairCandidates.Count && attempts < maxAttempts && prepared < maxPrepared; i++)
            {
                PreparedRouteCandidate candidate = repairCandidates[i];
                if (candidate.Stops.Count < Mathf.Max(2, cfg.MinStopsPerRoute))
                    continue;

                if (IsRouteDuplicate(candidate.Stops, plannedLineShapes, cfg))
                    continue;

                string strictShapeFailure;
                if (TryGetStrictGeneratedRouteShapeFailure(candidate.Stops, cfg, out strictShapeFailure))
                    continue;

                attempts++;
                string repairFailure;
                if (!TryAddGeneratedLineProbeFromStops(
                    candidate.Stops,
                    true,
                    candidate.RepairReason,
                    generatedLineProbes,
                    plannedLineShapes,
                    cfg,
                    stopLocator,
                    busInfo,
                    ref randomizer,
                    ref preparedProbes,
                    repairDepth,
                    out repairFailure))
                {
                    TransitLogging.Verbose("Rejected path-repair subroute after hidden probe failed: " + repairFailure + ".");
                    continue;
                }

                prepared++;
            }

            return prepared;
        }

        private bool IsRepairableHiddenProbeFailure(string failureReason)
        {
            if (string.IsNullOrEmpty(failureReason))
                return false;

            return failureReason.IndexOf("looping-path", StringComparison.Ordinal) >= 0
                   || failureReason.IndexOf("avoidable-loop", StringComparison.Ordinal) >= 0
                   || failureReason.IndexOf("invalid-stop-anchor", StringComparison.Ordinal) >= 0
                   || failureReason.IndexOf("strict shape validation", StringComparison.Ordinal) >= 0
                   || failureReason.IndexOf("path-failed", StringComparison.Ordinal) >= 0
                   || failureReason.IndexOf("incomplete", StringComparison.Ordinal) >= 0;
        }

        private List<PreparedRouteCandidate> BuildPathRepairSubrouteCandidates(List<Vector3> routeStops, AutoPublicTransitConfig cfg)
        {
            var candidates = new List<PreparedRouteCandidate>();
            if (routeStops == null)
                return candidates;

            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            if (routeStops.Count < minimumStops)
                return candidates;

            AddLoopOrderRepairCandidates(candidates, routeStops, cfg, "path repair smooth loop", true);

            if (routeStops.Count > minimumStops)
            {
                for (int removeIndex = 0; removeIndex < routeStops.Count && candidates.Count < 12; removeIndex++)
                {
                    var pruned = new List<Vector3>();
                    for (int i = 0; i < routeStops.Count; i++)
                    {
                        if (i != removeIndex)
                            pruned.Add(routeStops[i]);
                    }

                    List<Vector3> simplified = RouteShapeAnalyzer.SimplifyForBuildPreservingOrder(pruned, cfg, minimumStops);
                    AddPreparedRouteCandidate(candidates, simplified, true, "path repair prune", cfg);
                }
            }

            int maximumWindow = Mathf.Min(routeStops.Count, minimumStops + 2);
            int candidateLimit = 24;
            for (int windowSize = minimumStops; windowSize <= maximumWindow; windowSize++)
            {
                for (int start = 0; start + windowSize <= routeStops.Count; start++)
                {
                    var window = new List<Vector3>();
                    for (int i = 0; i < windowSize; i++)
                        window.Add(routeStops[start + i]);

                    AddPreparedRouteCandidate(candidates, window, true, "path repair window", cfg);
                    AddPreparedRouteCandidate(candidates, RouteShapeAnalyzer.SimplifyForBuild(window, cfg), true, "path repair optimized window", cfg);
                    if (candidates.Count >= candidateLimit)
                        return candidates;
                }

                if (routeStops.Count > windowSize)
                {
                    for (int start = routeStops.Count - windowSize + 1; start < routeStops.Count; start++)
                    {
                        var wrappedWindow = new List<Vector3>();
                        for (int offset = 0; offset < windowSize; offset++)
                            wrappedWindow.Add(routeStops[(start + offset) % routeStops.Count]);

                        AddPreparedRouteCandidate(candidates, wrappedWindow, true, "path repair wrapped window", cfg);
                        AddPreparedRouteCandidate(candidates, RouteShapeAnalyzer.SimplifyForBuild(wrappedWindow, cfg), true, "path repair optimized wrapped window", cfg);
                        if (candidates.Count >= candidateLimit)
                            return candidates;
                    }
                }
            }

            return candidates;
        }

        private void AddLoopOrderRepairCandidates(
            List<PreparedRouteCandidate> candidates,
            List<Vector3> routeStops,
            AutoPublicTransitConfig cfg,
            string reasonPrefix,
            bool skipOriginalOrder)
        {
            if (candidates == null || routeStops == null)
                return;

            List<Vector3> smoothed = RouteShapeAnalyzer.OptimizeClosedLoop(routeStops, cfg);
            AddLoopOrderRepairCandidate(candidates, smoothed, routeStops, true, reasonPrefix, cfg, skipOriginalOrder);
            AddLoopOrderRepairCandidate(candidates, ReverseRouteStops(smoothed), routeStops, true, reasonPrefix + " reverse", cfg, skipOriginalOrder);

            List<Vector3> simplified = RouteShapeAnalyzer.SimplifyForBuild(routeStops, cfg);
            AddLoopOrderRepairCandidate(candidates, simplified, routeStops, true, reasonPrefix + " simplify", cfg, skipOriginalOrder);
            AddLoopOrderRepairCandidate(candidates, ReverseRouteStops(simplified), routeStops, true, reasonPrefix + " simplify reverse", cfg, skipOriginalOrder);
        }

        private void AddLoopOrderRepairCandidate(
            List<PreparedRouteCandidate> candidates,
            List<Vector3> stops,
            List<Vector3> originalStops,
            bool adjusted,
            string repairReason,
            AutoPublicTransitConfig cfg,
            bool skipOriginalOrder)
        {
            if (skipOriginalOrder)
            {
                float orderDistance = Mathf.Max(32f, cfg.MaxWalkingDistance * 0.25f);
                if (ArePreparedRouteOrdersEquivalent(stops, originalStops, orderDistance))
                    return;
            }

            AddPreparedRouteCandidate(candidates, stops, adjusted, repairReason, cfg);
        }

        private List<Vector3> ReverseRouteStops(List<Vector3> stops)
        {
            if (stops == null)
                return new List<Vector3>();

            var reversed = new List<Vector3>(stops);
            reversed.Reverse();
            return reversed;
        }

        private bool IsPreparedRouteCandidateDuplicate(List<PreparedRouteCandidate> candidates, List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            if (candidates == null || stops == null || stops.Count == 0)
                return false;

            float overlapDistance = Mathf.Max(32f, cfg.MaxWalkingDistance * 0.25f);
            for (int i = 0; i < candidates.Count; i++)
            {
                List<Vector3> existing = candidates[i].Stops;
                if (ArePreparedRouteOrdersEquivalent(stops, existing, overlapDistance))
                    return true;
            }

            return false;
        }

        private bool ArePreparedRouteOrdersEquivalent(List<Vector3> a, List<Vector3> b, float maxDistance)
        {
            if (a == null || b == null || a.Count != b.Count || a.Count == 0)
                return false;

            int bestMatches = 0;
            for (int offset = 0; offset < b.Count; offset++)
            {
                int matches = 0;
                for (int i = 0; i < a.Count; i++)
                {
                    if (Geometry.DistanceXZ(a[i], b[(i + offset) % b.Count]) <= maxDistance)
                        matches++;
                }

                if (matches > bestMatches)
                    bestMatches = matches;
            }

            return (float)bestMatches / a.Count >= 0.85f;
        }

        private class GeneratedLineProbe
        {
            public ushort ProbeLineId;
            public List<Vector3> Stops = new List<Vector3>();
            public bool Adjusted;
            public int SkippedStops;
            public int ClosureBackoffs;
            public int RepairDepth;
            public int VehicleAuditDeferrals;
        }

        private class PreparedRouteCandidate
        {
            public List<Vector3> Stops = new List<Vector3>();
            public bool Adjusted;
            public string RepairReason;
        }

        private class LineBuildResult
        {
            public bool Success;
            public ushort LineId;
            public List<Vector3> Stops = new List<Vector3>();
            public int SkippedStops;
            public int ClosureBackoffs;
            public int ReusedStops;
            public bool VehicleModelRepaired;
            public bool UnsafeVehicleModelDetected;
            public bool NoSafeCityBusVehicle;
            public bool RetryVehicleAuditAfterSettlement;
            public string VehicleModelAuditSummary;
            public string FailureReason;
        }

        private class StopPublicationStats
        {
            public int Lines;
            public int VisibleLines;
            public int Stops;
            public int TemporaryStops;
            public int TemporaryStopsCleared;
            public int WrongTransportLine;
            public int TransportLineOwnersFixed;
            public int MissingCreatedStops;
            public int BrokenStopChains;
            public int FixedStops;
            public int EditLockedStops;
            public int EditLocksCleared;
            public int MoveableFlagsApplied;
            public int UpdatedNodes;
            public int NodeUpdateFailures;
            public int LegacyEditableLinesMigrated;
            public int LegacyEditableLinesSkippedLive;
            public int LegacyEditableStopsMigrated;
            public int LegacyEditableMigrationFailures;

            public void Add(StopPublicationStats other)
            {
                if (other == null)
                    return;

                Lines += other.Lines;
                VisibleLines += other.VisibleLines;
                Stops += other.Stops;
                TemporaryStops += other.TemporaryStops;
                TemporaryStopsCleared += other.TemporaryStopsCleared;
                WrongTransportLine += other.WrongTransportLine;
                TransportLineOwnersFixed += other.TransportLineOwnersFixed;
                MissingCreatedStops += other.MissingCreatedStops;
                BrokenStopChains += other.BrokenStopChains;
                FixedStops += other.FixedStops;
                EditLockedStops += other.EditLockedStops;
                EditLocksCleared += other.EditLocksCleared;
                MoveableFlagsApplied += other.MoveableFlagsApplied;
                UpdatedNodes += other.UpdatedNodes;
                NodeUpdateFailures += other.NodeUpdateFailures;
                LegacyEditableLinesMigrated += other.LegacyEditableLinesMigrated;
                LegacyEditableLinesSkippedLive += other.LegacyEditableLinesSkippedLive;
                LegacyEditableStopsMigrated += other.LegacyEditableStopsMigrated;
                LegacyEditableMigrationFailures += other.LegacyEditableMigrationFailures;
            }
        }

        private class StopSnapshot
        {
            public Vector3 Position;
            public bool FixedPlatform;
        }

        private enum GeneratedProbeStatus
        {
            Pending,
            Valid,
            Failed
        }

        private LineBuildResult TryCreateHiddenProbeFromRoute(
            List<Vector3> requestedStops,
            List<ExistingLineSnapshot> existingLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransportInfo busInfo,
            ref Randomizer randomizer)
        {
            var result = new LineBuildResult();
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            TransportManager tm = TransportManager.instance;

            ushort lineId;
            if (!tm.CreateLine(out lineId, ref randomizer, busInfo, false))
            {
                result.FailureReason = "TransportManager.CreateLine returned false";
                return result;
            }

            result.LineId = lineId;
            ref TransportLine probeLine = ref tm.m_lines.m_buffer[lineId];
            probeLine.m_flags |= TransportLine.Flags.Temporary | TransportLine.Flags.Hidden;

            try
            {
                var acceptedStops = new List<Vector3>();

                for (int i = 0; i < requestedStops.Count; i++)
                {
                    if (acceptedStops.Count >= cfg.MaxStopsPerRoute)
                        break;

                    Vector3 candidate = requestedStops[i];
                    Vector3 reusableStop;
                    if (TryFindNearbyStop(candidate, acceptedStops, GeneratedStopReuseDistance, out reusableStop))
                    {
                        result.ReusedStops++;
                        TransitLogging.Verbose(
                            "Reused current generated stop instead of creating stop " + i +
                            " for line " + lineId +
                            " within " + GeneratedStopReuseDistance.ToString("0", CultureInfo.InvariantCulture) + "f.");
                        continue;
                    }

                    if (TryFindReusableGeneratedStop(candidate, existingLines, GeneratedStopReuseDistance, out reusableStop))
                    {
                        candidate = reusableStop;
                        result.ReusedStops++;
                    }

                    if (IsNearAnyStop(candidate, acceptedStops, 1f))
                        continue;

                    string stopFailure;
                    if (TryAddStopToLine(lineId, acceptedStops.Count, candidate, out stopFailure))
                    {
                        acceptedStops.Add(candidate);
                        continue;
                    }

                    result.SkippedStops++;
                    TransitLogging.Verbose("Skipped generated stop " + i + " for line " + lineId + ": " + stopFailure + ".");
                }

                if (acceptedStops.Count < minimumStops)
                {
                    result.FailureReason = "only " + acceptedStops.Count + " stops were accepted";
                    return result;
                }

                while (acceptedStops.Count >= minimumStops)
                {
                    string closeFailure;
                    if (TryCloseLine(lineId, acceptedStops[0], out closeFailure))
                        break;

                    int removeIndex = acceptedStops.Count - 1;
                    string removeFailure;
                    if (!TryRemoveStopFromLine(lineId, removeIndex, out removeFailure))
                    {
                        result.FailureReason = "could not back off unclosable stop " + removeIndex + ": " + removeFailure;
                        return result;
                    }

                    result.ClosureBackoffs++;
                    acceptedStops.RemoveAt(removeIndex);
                    TransitLogging.Verbose("Backed off generated stop " + removeIndex + " for line " + lineId + " after close failed: " + closeFailure + ".");
                }

                if (acceptedStops.Count < minimumStops)
                {
                    result.FailureReason = "route could not close with at least " + minimumStops + " stops";
                    return result;
                }

                ref TransportLine builtProbe = ref tm.m_lines.m_buffer[lineId];
                List<Vector3> actualStops = GetExistingLineStops(ref builtProbe);
                if (!builtProbe.Complete || actualStops.Count < minimumStops)
                {
                    result.FailureReason = "hidden probe was not complete after close";
                    return result;
                }

                try
                {
                    builtProbe.UpdatePaths(lineId);
                }
                catch
                {
                }

                if ((builtProbe.m_flags & TransportLine.Flags.Invalid) != 0)
                {
                    result.FailureReason = "hidden probe was marked invalid by the game";
                    return result;
                }

                if (IsRouteDuplicate(actualStops, existingLines, cfg))
                {
                    result.FailureReason = "hidden probe route overlaps an existing line too heavily";
                    return result;
                }

                if (stopLocator != null && !AreLineStopsStillValid(actualStops, cfg, stopLocator))
                {
                    result.FailureReason = "hidden probe route failed scanner stop-anchor validation: invalid-stop-anchor";
                    return result;
                }

                string strictShapeFailure;
                if (TryGetStrictGeneratedRouteShapeFailure(actualStops, cfg, out strictShapeFailure))
                {
                    result.FailureReason = "hidden probe route failed strict shape validation: " + strictShapeFailure;
                    return result;
                }

                result.Success = true;
                result.Stops = actualStops;
                return result;
            }
            catch (Exception e)
            {
                result.FailureReason = e.ToString();
                return result;
            }
            finally
            {
                if (!result.Success)
                    SafeReleaseLine(lineId);
            }
        }

        private int ReconcileGeneratedLineProbes(
            List<GeneratedLineProbe> probes,
            List<ExistingLineSnapshot> existingLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            int pass,
            bool requireSettledPaths,
            bool finalPass,
            TransitScanSummary scanSummary)
        {
            if (probes == null || probes.Count == 0)
                return 0;

            int changed = 0;
            TransportManager tm = TransportManager.instance;
            TransportInfo busInfo = GetOrdinaryCityBusTransportInfo(tm, "generated-line-reconcile");
            if (busInfo == null)
            {
                TransitLogging.Error("Bus transport info was not available while reconciling generated-line probes.");
                return 0;
            }
            var randomizer = new Randomizer((uint)(DateTime.UtcNow.Ticks + pass));

            for (int i = probes.Count - 1; i >= 0; i--)
            {
                GeneratedLineProbe probe = probes[i];
                ushort probeLineId = probe.ProbeLineId;
                if (probeLineId == 0 || probeLineId >= tm.m_lines.m_size)
                {
                    probes.RemoveAt(i);
                    CountGeneratedRouteIntegrityFailure(scanSummary);
                    TransitLogging.Warn("Skipped generated route because hidden probe line id was out of range.");
                    changed++;
                    continue;
                }

                ref TransportLine probeLine = ref tm.m_lines.m_buffer[probeLineId];
                if ((probeLine.m_flags & TransportLine.Flags.Created) == 0)
                {
                    probes.RemoveAt(i);
                    CountGeneratedRouteIntegrityFailure(scanSummary);
                    TransitLogging.Warn("Skipped generated route because hidden probe line " + probeLineId + " was released before settlement.");
                    changed++;
                    continue;
                }

                string failureReason;
                GeneratedProbeStatus status = GetGeneratedLineProbeStatus(ref probeLine, probeLineId, cfg, stopLocator, requireSettledPaths, finalPass, out failureReason);
                if (status == GeneratedProbeStatus.Pending)
                    continue;

                if (status == GeneratedProbeStatus.Failed)
                {
                    if (probe.RepairDepth < MaxGeneratedProbeRepairDepth && IsRepairableHiddenProbeFailure(failureReason))
                    {
                        int preparedRepairProbes = 0;
                        var repairLineShapes = new List<ExistingLineSnapshot>(existingLines);
                        int repairedProbes = TryPreparePathRepairSubrouteProbes(
                            probe.Stops,
                            failureReason,
                            probes,
                            repairLineShapes,
                            cfg,
                            stopLocator,
                            busInfo,
                            ref randomizer,
                            ref preparedRepairProbes,
                            probe.RepairDepth + 1);

                        if (repairedProbes > 0)
                        {
                            SafeReleaseLine(probeLineId);
                            probes.RemoveAt(i);
                            TransitLogging.Log(
                                "Requeued " + repairedProbes +
                                " smaller generated-line probe(s) after settled hidden probe " + probeLineId +
                                " failed: " + failureReason + ".");
                            changed++;
                            continue;
                        }
                    }

                    SafeReleaseLine(probeLineId);
                    probes.RemoveAt(i);
                    CountGeneratedRouteIntegrityFailure(scanSummary);
                    TransitLogging.Warn("Skipped generated route after hidden path probe failed on line " + probeLineId + ": " + failureReason + ".");
                    changed++;
                    continue;
                }

                LineBuildResult promoteResult = TryPromoteSettledGeneratedProbe(probe, existingLines, cfg, stopLocator, busInfo);

                if (!promoteResult.Success)
                {
                    if (promoteResult.RetryVehicleAuditAfterSettlement &&
                        probe.VehicleAuditDeferrals < MaxGeneratedVehicleAuditDeferralPasses)
                    {
                        probe.VehicleAuditDeferrals++;
                        TransitLogging.Warn(
                            "Deferring generated bus line " + probeLineId +
                            " vehicle audit after no ordinary city bus model was available; deferral=" +
                            probe.VehicleAuditDeferrals + "/" + MaxGeneratedVehicleAuditDeferralPasses +
                            ", reason=" + promoteResult.FailureReason + ".");
                        changed++;
                        continue;
                    }

                    probes.RemoveAt(i);
                    if (scanSummary != null)
                    {
                        if (promoteResult.UnsafeVehicleModelDetected)
                            scanSummary.GeneratedUnsafeVehicleModelsDetected++;
                        if (promoteResult.NoSafeCityBusVehicle)
                            scanSummary.GeneratedLinesWithoutSafeCityBusVehicle++;
                    }

                    SafeReleaseLine(probeLineId);
                    CountGeneratedRouteIntegrityFailure(scanSummary);
                    TransitLogging.Warn("Skipped generated route because promotion from hidden probe " + probeLineId + " failed: " + promoteResult.FailureReason + ".");
                    changed++;
                    continue;
                }

                probes.RemoveAt(i);

                if (scanSummary != null)
                {
                    scanSummary.CreatedLines++;
                    scanSummary.GeneratedStopsSkipped += probe.SkippedStops;
                    scanSummary.ClosureBackoffs += probe.ClosureBackoffs;
                    if (promoteResult.VehicleModelRepaired)
                        scanSummary.GeneratedVehicleModelsRepaired++;
                    if (promoteResult.UnsafeVehicleModelDetected)
                        scanSummary.GeneratedUnsafeVehicleModelsDetected++;
                    if (promoteResult.NoSafeCityBusVehicle)
                        scanSummary.GeneratedLinesWithoutSafeCityBusVehicle++;
                    if (scanSummary.CreatedLineIds == null)
                        scanSummary.CreatedLineIds = new List<ushort>();
                    scanSummary.CreatedLineIds.Add(promoteResult.LineId);
                    if (probe.Adjusted)
                        scanSummary.RepairedGeneratedLines++;
                }

                existingLines.Add(new ExistingLineSnapshot
                {
                    LineId = promoteResult.LineId,
                    Stops = new List<Vector3>(promoteResult.Stops),
                    TotalLength = ComputeRouteLength(promoteResult.Stops)
                });

                TransitLogging.Log("Promoted generated bus line " + promoteResult.LineId + " from settled hidden probe " + probeLineId + " with " + promoteResult.Stops.Count + " stops.");
                changed++;
            }

            return changed;
        }

        private GeneratedProbeStatus GetGeneratedLineProbeStatus(
            ref TransportLine line,
            ushort lineId,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            bool requireSettledPaths,
            bool finalPass,
            out string failureReason)
        {
            if (!line.Complete)
            {
                failureReason = "incomplete";
                return GeneratedProbeStatus.Failed;
            }

            List<Vector3> stops = GetExistingLineStops(ref line);
            if (stops == null || stops.Count < 2)
            {
                failureReason = "too-few-stops";
                return GeneratedProbeStatus.Failed;
            }

            if ((line.m_flags & TransportLine.Flags.Invalid) != 0)
            {
                failureReason = "game-invalid";
                return GeneratedProbeStatus.Failed;
            }

            if (!requireSettledPaths)
            {
                failureReason = null;
                return GeneratedProbeStatus.Pending;
            }

            GeneratedProbeStatus pathStatus = GetStrictLinePathStatus(ref line, lineId, stops.Count, finalPass, out failureReason);
            if (pathStatus != GeneratedProbeStatus.Valid)
                return pathStatus;

            if (TryGetGeneratedLinePathLoopFailure(ref line, lineId, stops, cfg, out failureReason))
                return GeneratedProbeStatus.Failed;

            if (stopLocator != null && TryGetExistingLineIntegrityFailure(ref line, lineId, stops, cfg, stopLocator, out failureReason))
            {
                return GeneratedProbeStatus.Failed;
            }

            if (stopLocator == null)
            {
                string shapeFailure;
                if (TryGetStrictGeneratedRouteShapeFailure(stops, cfg, out shapeFailure))
                {
                    failureReason = shapeFailure;
                    return GeneratedProbeStatus.Failed;
                }
            }

            failureReason = null;
            return GeneratedProbeStatus.Valid;
        }

        private bool TryGetGeneratedLinePathLoopFailure(
            ref TransportLine line,
            ushort lineId,
            List<Vector3> stops,
            AutoPublicTransitConfig cfg,
            out string failureReason)
        {
            failureReason = null;
            if (stops == null || stops.Count < 2)
            {
                failureReason = "too-few-stops";
                return true;
            }

            float pathLength;
            float maxLegRatio;
            float maxLegExtra;
            string pathFailure;
            if (!TryComputeTransportLinePathMetrics(ref line, lineId, stops.Count, out pathLength, out maxLegRatio, out maxLegExtra, out pathFailure))
            {
                failureReason = pathFailure;
                return true;
            }

            float straightLength = Mathf.Max(1f, ComputeRouteLength(stops));
            float extraLength = pathLength - straightLength;
            float pathToStraight = pathLength / straightLength;
            float maxAllowedLength = Mathf.Max(cfg.MaxLineLengthKm * 1000f, cfg.MaxLineLengthKm * 1500f);

            if (pathLength > maxAllowedLength)
            {
                failureReason =
                    "looping-path-too-long line=" + lineId +
                    " path=" + FormatKilometers(pathLength) +
                    " limit=" + FormatKilometers(maxAllowedLength);
                return true;
            }

            float legExtraLimit = Mathf.Max(3200f, cfg.MaxRoadDistance * 5.0f);
            if (stops.Count >= 4 && maxLegRatio > 5.5f && maxLegExtra > legExtraLimit)
            {
                failureReason =
                    "avoidable-loop-leg line=" + lineId +
                    " path=" + FormatKilometers(pathLength) +
                    " straight=" + FormatKilometers(straightLength) +
                    " maxLegRatio=" + FormatRatio(maxLegRatio) +
                    " maxLegExtra=" + FormatKilometers(maxLegExtra);
                return true;
            }

            float routeExtraLimit = Mathf.Max(6500f, cfg.MaxRoadDistance * 8.0f);
            if (stops.Count >= 4 && pathToStraight > 3.8f && extraLength > routeExtraLimit)
            {
                failureReason =
                    "avoidable-loop line=" + lineId +
                    " path=" + FormatKilometers(pathLength) +
                    " straight=" + FormatKilometers(straightLength) +
                    " ratio=" + FormatRatio(pathToStraight) +
                    " extra=" + FormatKilometers(extraLength);
                return true;
            }

            failureReason = null;
            return false;
        }

        private bool TryComputeTransportLinePathMetrics(
            ref TransportLine line,
            ushort lineId,
            int stopCount,
            out float pathLength,
            out float maxLegRatio,
            out float maxLegExtra,
            out string failureReason)
        {
            pathLength = 0f;
            maxLegRatio = 0f;
            maxLegExtra = 0f;
            failureReason = null;

            ushort firstStop = line.m_stops;
            if (firstStop == 0)
            {
                failureReason = "no-stops";
                return false;
            }

            NetManager nm = NetManager.instance;
            ushort currentStop = firstStop;
            int checkedStops = 0;

            while (currentStop != 0 && checkedStops < stopCount && checkedStops < 512)
            {
                ushort nextStop = TransportLine.GetNextStop(currentStop);
                if (nextStop == 0)
                {
                    failureReason = "stop-chain-ended line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                if (currentStop >= nm.m_nodes.m_buffer.Length || nextStop >= nm.m_nodes.m_buffer.Length)
                {
                    failureReason = "stop-node-out-of-range line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                ushort segmentId = TransportLine.GetNextSegment(currentStop);
                if (segmentId == 0 || segmentId >= nm.m_segments.m_buffer.Length)
                {
                    failureReason = "missing-next-segment line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.PathFailed) != 0)
                {
                    failureReason = "path-failed line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                if ((segment.m_flags & NetSegment.Flags.WaitingPath) != 0)
                {
                    failureReason = "path-waiting line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                if (segment.m_path == 0)
                {
                    failureReason = "path-missing line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                if ((segment.m_flags & NetSegment.Flags.PathLength) == 0)
                {
                    failureReason = "path-length-pending line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                float segmentLength = segment.m_averageLength;
                if (segmentLength <= 0.1f)
                {
                    failureReason = "path-length-zero line=" + lineId + " stopIndex=" + checkedStops;
                    return false;
                }

                pathLength += segmentLength;

                Vector3 currentPosition = nm.m_nodes.m_buffer[currentStop].m_position;
                Vector3 nextPosition = nm.m_nodes.m_buffer[nextStop].m_position;
                float directLength = Geometry.DistanceXZ(currentPosition, nextPosition);
                float legExtra = segmentLength - directLength;
                float legRatio = segmentLength / Mathf.Max(1f, directLength);
                if (legRatio > maxLegRatio)
                    maxLegRatio = legRatio;
                if (legExtra > maxLegExtra)
                    maxLegExtra = legExtra;

                checkedStops++;
                currentStop = nextStop;
                if (currentStop == firstStop)
                    break;
            }

            if (checkedStops < stopCount)
            {
                failureReason = "stop-chain-ended line=" + lineId + " checked=" + checkedStops + " expected=" + stopCount;
                return false;
            }

            failureReason = null;
            return true;
        }

        private string FormatKilometers(float meters)
        {
            return (meters / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + "km";
        }

        private string FormatRatio(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private string GetTransportLinePathMetricsSummary(ref TransportLine line, ushort lineId, List<Vector3> stops)
        {
            if (stops == null || stops.Count < 2)
                return "pathKm=n/a";

            float pathLength;
            float maxLegRatio;
            float maxLegExtra;
            string failureReason;
            if (!TryComputeTransportLinePathMetrics(ref line, lineId, stops.Count, out pathLength, out maxLegRatio, out maxLegExtra, out failureReason))
                return "pathKm=n/a pathReason=" + failureReason;

            float straightLength = Mathf.Max(1f, ComputeRouteLength(stops));
            return "pathKm=" + FormatKilometers(pathLength) +
                   ", straightKm=" + FormatKilometers(straightLength) +
                   ", pathRatio=" + FormatRatio(pathLength / straightLength) +
                   ", maxLegRatio=" + FormatRatio(maxLegRatio) +
                   ", maxLegExtra=" + FormatKilometers(maxLegExtra);
        }

        private GeneratedProbeStatus GetStrictLinePathStatus(
            ref TransportLine line,
            ushort lineId,
            int stopCount,
            bool finalPass,
            out string failureReason)
        {
            failureReason = null;
            ushort firstStop = line.m_stops;
            if (firstStop == 0)
            {
                failureReason = "no-stops";
                return GeneratedProbeStatus.Failed;
            }

            NetManager nm = NetManager.instance;
            ushort currentStop = firstStop;
            int checkedStops = 0;

            while (currentStop != 0 && checkedStops < stopCount && checkedStops < 512)
            {
                ushort segmentId = TransportLine.GetNextSegment(currentStop);
                if (segmentId == 0)
                {
                    failureReason = "missing-next-segment line=" + lineId + " stopIndex=" + checkedStops;
                    return GeneratedProbeStatus.Failed;
                }

                ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
                if ((segment.m_flags & NetSegment.Flags.PathFailed) != 0)
                {
                    failureReason = "path-failed line=" + lineId + " stopIndex=" + checkedStops;
                    return GeneratedProbeStatus.Failed;
                }

                if ((segment.m_flags & NetSegment.Flags.WaitingPath) != 0)
                {
                    failureReason = "path-waiting line=" + lineId + " stopIndex=" + checkedStops;
                    return finalPass ? GeneratedProbeStatus.Failed : GeneratedProbeStatus.Pending;
                }

                if (segment.m_path == 0)
                {
                    failureReason = "path-missing line=" + lineId + " stopIndex=" + checkedStops;
                    return GeneratedProbeStatus.Failed;
                }

                if ((segment.m_flags & NetSegment.Flags.PathLength) == 0)
                {
                    failureReason = "path-length-pending line=" + lineId + " stopIndex=" + checkedStops;
                    return finalPass ? GeneratedProbeStatus.Failed : GeneratedProbeStatus.Pending;
                }

                checkedStops++;
                currentStop = TransportLine.GetNextStop(currentStop);
                if (currentStop == firstStop)
                    break;
            }

            if (checkedStops < stopCount)
            {
                failureReason = "stop-chain-ended line=" + lineId + " checked=" + checkedStops + " expected=" + stopCount;
                return GeneratedProbeStatus.Failed;
            }

            failureReason = null;
            return GeneratedProbeStatus.Valid;
        }

        private LineBuildResult TryPromoteSettledGeneratedProbe(
            GeneratedLineProbe probe,
            List<ExistingLineSnapshot> existingLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransportInfo busInfo)
        {
            var result = new LineBuildResult();
            if (busInfo == null)
            {
                result.FailureReason = "bus transport info unavailable";
                return result;
            }

            TransportManager tm = TransportManager.instance;
            ushort promotedLineId = probe.ProbeLineId;
            result.LineId = promotedLineId;

            try
            {
                ref TransportLine promotedLine = ref tm.m_lines.m_buffer[promotedLineId];
                if ((promotedLine.m_flags & TransportLine.Flags.Created) == 0)
                {
                    result.FailureReason = "settled probe was no longer created";
                    return result;
                }

                if (promotedLine.Info == null || promotedLine.Info.m_transportType != TransportInfo.TransportType.Bus)
                {
                    result.FailureReason = "settled probe was not a bus transport line";
                    return result;
                }

                List<Vector3> actualStops = GetExistingLineStops(ref promotedLine);
                if (!promotedLine.Complete || actualStops.Count < Mathf.Max(2, cfg.MinStopsPerRoute))
                {
                    result.FailureReason = "settled probe was not complete before publication";
                    return result;
                }

                string failureReason;
                GeneratedProbeStatus status = GetGeneratedLineProbeStatus(ref promotedLine, promotedLineId, cfg, stopLocator, true, true, out failureReason);
                if (status != GeneratedProbeStatus.Valid)
                {
                    result.FailureReason = failureReason;
                    return result;
                }

                if (IsRouteDuplicate(actualStops, existingLines, cfg))
                {
                    result.FailureReason = "promoted route overlaps an existing line too heavily";
                    return result;
                }

                if (TryGetStrictGeneratedRouteShapeFailure(actualStops, cfg, out failureReason))
                {
                    result.FailureReason = "promoted route failed strict shape validation: " + failureReason;
                    return result;
                }

                ushort publicLineNumber;
                string numberFailure;
                if (!TryAssignGeneratedPublicLineNumber(tm, ref promotedLine, out publicLineNumber, out numberFailure))
                {
                    result.FailureReason = numberFailure;
                    return result;
                }

                string pathMetricsSummary = GetTransportLinePathMetricsSummary(ref promotedLine, promotedLineId, actualStops);
                PublishSettledGeneratedLine(tm, promotedLineId, ref promotedLine);
                string vehicleAuditPhase = probe.VehicleAuditDeferrals == 0
                    ? "generated-publication-immediate"
                    : "generated-publication-deferred-" + probe.VehicleAuditDeferrals;
                BusLineVehicleAudit vehicleAudit = EnsureGeneratedBusLineUsesSafeCityBusVehicle(promotedLineId, ref promotedLine, vehicleAuditPhase);
                if (vehicleAudit != null)
                {
                    result.VehicleModelRepaired = vehicleAudit.ReplacementApplied;
                    result.UnsafeVehicleModelDetected = vehicleAudit.SelectedVehicleUnsafe ||
                        vehicleAudit.SelectedVehicleMissing ||
                        vehicleAudit.ReplacementApplied ||
                        vehicleAudit.NoSafeCityBusVehicle;
                    result.NoSafeCityBusVehicle = vehicleAudit.NoSafeCityBusVehicle;
                    result.VehicleModelAuditSummary = vehicleAudit.GetLogSummary();
                    if (vehicleAudit.NoSafeCityBusVehicle)
                    {
                        result.RetryVehicleAuditAfterSettlement = true;
                        result.FailureReason = "no compatible ordinary city bus model was available: " + result.VehicleModelAuditSummary;
                        TransitLogging.Warn(
                            "Generated bus line " + promotedLineId +
                            " vehicle audit found no compatible ordinary city bus model. Selected vehicle=" +
                            (string.IsNullOrEmpty(vehicleAudit.SelectedVehicleName) ? "none" : vehicleAudit.SelectedVehicleName) +
                            ". Disable coach/intercity-only bus assets or enable at least one ordinary city bus model.");
                        return result;
                    }
                }

                actualStops = GetExistingLineStops(ref promotedLine);
                if (!promotedLine.Complete || actualStops.Count < Mathf.Max(2, cfg.MinStopsPerRoute))
                {
                    result.FailureReason = "published line was not complete";
                    return result;
                }

                if (stopLocator != null && TryGetExistingLineIntegrityFailure(ref promotedLine, promotedLineId, actualStops, cfg, stopLocator, out failureReason))
                {
                    result.FailureReason = "published line failed scanner integrity validation: " + failureReason;
                    return result;
                }

                result.Success = true;
                result.Stops = actualStops;
                TransitLogging.Log(
                    "Published generated bus line id=" + promotedLineId +
                    ", publicNumber=" + publicLineNumber +
                    ", stops=" + actualStops.Count +
                    ", " + pathMetricsSummary +
                    ", vehicleAudit=" + (string.IsNullOrEmpty(result.VehicleModelAuditSummary) ? "unavailable" : result.VehicleModelAuditSummary) +
                    ", flags=" + promotedLine.m_flags +
                    ", overviewVisible=" + IsVanillaOverviewVisibleBusLine(ref promotedLine) + ".");
                return result;
            }
            catch (Exception e)
            {
                result.FailureReason = e.ToString();
                return result;
            }
        }

        private bool TryAssignGeneratedPublicLineNumber(
            TransportManager tm,
            ref TransportLine line,
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

            ushort[] lineNumbers = TransportManagerLineNumberField.GetValue(tm) as ushort[];
            int busIndex = (int)TransportInfo.TransportType.Bus;
            if (lineNumbers == null || busIndex < 0 || busIndex >= lineNumbers.Length)
            {
                failureReason = "TransportManager line-number counter was unavailable";
                return false;
            }

            uint next = (uint)lineNumbers[busIndex] + 1u;
            if (next > ushort.MaxValue)
            {
                failureReason = "bus line-number counter overflowed";
                return false;
            }

            publicLineNumber = (ushort)next;
            lineNumbers[busIndex] = publicLineNumber;
            line.m_lineNumber = publicLineNumber;
            failureReason = null;
            return true;
        }

        private void PublishSettledGeneratedLine(
            TransportManager tm,
            ushort lineId,
            ref TransportLine line)
        {
            line.m_flags &= ~(TransportLine.Flags.Temporary |
                              TransportLine.Flags.Hidden |
                              TransportLine.Flags.Invalid |
                              TransportLine.Flags.Selected |
                              TransportLine.Flags.Highlighted);
            line.m_flags |= TransportLine.Flags.Created |
                            TransportLine.Flags.Complete |
                            TransportLine.Flags.CompleteSet;

            StopPublicationStats stopStats = PublishGeneratedLineStops(lineId, ref line);

            try
            {
                line.CheckCompletionMilestone();
                line.m_flags |= TransportLine.Flags.CompleteSet;
            }
            catch
            {
            }

            try
            {
                line.UpdatePaths(lineId);
            }
            catch
            {
            }

            try
            {
                line.UpdateMeshData(lineId);
            }
            catch
            {
            }

            try
            {
                tm.UpdateLine(lineId);
                tm.UpdateLinesNow();
            }
            catch
            {
            }

            if (stopStats.TemporaryStopsCleared > 0 ||
                stopStats.TransportLineOwnersFixed > 0 ||
                stopStats.EditLocksCleared > 0 ||
                stopStats.MoveableFlagsApplied > 0 ||
                stopStats.NodeUpdateFailures > 0)
            {
                TransitLogging.Log(
                    "Published physical bus stops for line " + lineId +
                    ": stops=" + stopStats.Stops +
                    ", temporaryCleared=" + stopStats.TemporaryStopsCleared +
                    ", ownersFixed=" + stopStats.TransportLineOwnersFixed +
                    ", fixedStops=" + stopStats.FixedStops +
                    ", editLocksCleared=" + stopStats.EditLocksCleared +
                    ", moveableApplied=" + stopStats.MoveableFlagsApplied +
                    ", nodeRefreshes=" + stopStats.UpdatedNodes +
                    ", nodeRefreshFailures=" + stopStats.NodeUpdateFailures + ".");
            }
        }

        private StopPublicationStats PublishGeneratedLineStops(ushort lineId, ref TransportLine line)
        {
            return VisitLineStops(lineId, ref line, true, true);
        }

        private System.Collections.IEnumerator RepairPublishedBusStopNodesDeferred(string reason)
        {
            for (int frame = 0; frame < 60; frame++)
                yield return null;

            RepairPublishedBusStopNodes(reason);
        }

        private StopPublicationStats RepairPublishedBusStopNodes(string reason)
        {
            TransportManager tm = TransportManager.instance;
            var stats = new StopPublicationStats();

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0)
                    continue;

                if ((line.m_flags & TransportLine.Flags.Complete) == 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                    continue;

                if (IsProtectedFromAptManagement(lineId, ref line))
                    continue;

                StopPublicationStats lineStats = VisitLineStops(lineId, ref line, true, false);
                if (ShouldMigrateLegacyFixedBusLineForEditability(reason, lineId, ref line, lineStats))
                    TryMigrateLegacyFixedBusLineForEditability(lineId, ref line, reason, lineStats);

                stats.Add(lineStats);
            }

            if (stats.Lines > 0)
            {
                TransitLogging.Log(
                    "Public bus stop node repair (" + reason + "): lines=" + stats.Lines +
                    ", stops=" + stats.Stops +
                    ", temporaryCleared=" + stats.TemporaryStopsCleared +
                    ", ownersFixed=" + stats.TransportLineOwnersFixed +
                    ", fixedStops=" + stats.FixedStops +
                    ", editLocksCleared=" + stats.EditLocksCleared +
                    ", moveableApplied=" + stats.MoveableFlagsApplied +
                    ", legacyEditableLinesMigrated=" + stats.LegacyEditableLinesMigrated +
                    ", legacyEditableLinesSkippedLive=" + stats.LegacyEditableLinesSkippedLive +
                    ", legacyEditableStopsMigrated=" + stats.LegacyEditableStopsMigrated +
                    ", legacyEditableMigrationFailures=" + stats.LegacyEditableMigrationFailures +
                    ", missingCreated=" + stats.MissingCreatedStops +
                    ", brokenChains=" + stats.BrokenStopChains +
                    ", nodeRefreshes=" + stats.UpdatedNodes +
                    ", nodeRefreshFailures=" + stats.NodeUpdateFailures + ".");
            }

            return stats;
        }

        private bool IsVanillaOverviewVisibleBusLine(ref TransportLine line)
        {
            if ((line.m_flags & (TransportLine.Flags.Created | TransportLine.Flags.Temporary)) != TransportLine.Flags.Created)
                return false;
            if ((line.m_flags & TransportLine.Flags.Hidden) != 0)
                return false;

            TransportInfo info = line.Info;
            return info != null && info.m_transportType == TransportInfo.TransportType.Bus;
        }

        private void LogBusLineVisibilityAudit()
        {
            TransportManager tm = TransportManager.instance;
            int visibleBusLines = 0;
            int temporaryBusLines = 0;
            int hiddenBusLines = 0;
            int completeBusLines = 0;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                    continue;

                if ((line.m_flags & TransportLine.Flags.Complete) != 0)
                    completeBusLines++;
                if ((line.m_flags & TransportLine.Flags.Temporary) != 0)
                    temporaryBusLines++;
                if ((line.m_flags & TransportLine.Flags.Hidden) != 0)
                    hiddenBusLines++;
                if (IsVanillaOverviewVisibleBusLine(ref line))
                    visibleBusLines++;
            }

            TransitLogging.Log(
                "Bus line visibility audit: vanillaOverviewVisible=" + visibleBusLines +
                ", complete=" + completeBusLines +
                ", temporary=" + temporaryBusLines +
                ", hidden=" + hiddenBusLines + ".");
        }

        private void LogBusStopPublicationAudit()
        {
            TransportManager tm = TransportManager.instance;
            var stats = new StopPublicationStats();

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0)
                    continue;

                if ((line.m_flags & TransportLine.Flags.Complete) == 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                    continue;

                stats.Add(VisitLineStops(lineId, ref line, false, false));
            }

            TransitLogging.Log(
                "Bus stop publication audit: lines=" + stats.Lines +
                ", vanillaOverviewVisible=" + stats.VisibleLines +
                ", stops=" + stats.Stops +
                ", temporaryStops=" + stats.TemporaryStops +
                ", wrongOwners=" + stats.WrongTransportLine +
                ", fixedStops=" + stats.FixedStops +
                ", editLocked=" + stats.EditLockedStops +
                ", missingCreated=" + stats.MissingCreatedStops +
                ", brokenChains=" + stats.BrokenStopChains + ".");
        }

        private StopPublicationStats VisitLineStops(ushort lineId, ref TransportLine line, bool promote, bool applyMoveable)
        {
            var stats = new StopPublicationStats();
            stats.Lines = 1;
            if (IsVanillaOverviewVisibleBusLine(ref line))
                stats.VisibleLines = 1;

            ushort firstStop = line.m_stops;
            if (firstStop == 0)
            {
                stats.BrokenStopChains++;
                return stats;
            }

            NetManager nm = NetManager.instance;
            SimulationManager sm = SimulationManager.instance;
            ushort currentStop = firstStop;
            int safety = 0;

            while (currentStop != 0 && safety < 512)
            {
                stats.Stops++;
                bool changed = false;

                if (currentStop >= nm.m_nodes.m_buffer.Length)
                {
                    stats.MissingCreatedStops++;
                    stats.BrokenStopChains++;
                    break;
                }

                ref NetNode node = ref nm.m_nodes.m_buffer[currentStop];
                if ((node.m_flags & NetNode.Flags.Created) == 0)
                    stats.MissingCreatedStops++;

                if ((node.m_flags & NetNode.Flags.Temporary) != 0)
                {
                    if (promote)
                    {
                        node.m_flags &= ~NetNode.Flags.Temporary;
                        stats.TemporaryStopsCleared++;
                        changed = true;
                    }
                    else
                    {
                        stats.TemporaryStops++;
                    }
                }

                if (node.m_transportLine != lineId)
                {
                    if (promote)
                    {
                        node.m_transportLine = lineId;
                        stats.TransportLineOwnersFixed++;
                        changed = true;
                    }
                    else
                    {
                        stats.WrongTransportLine++;
                    }
                }

                if ((node.m_flags & NetNode.Flags.Fixed) != 0)
                    stats.FixedStops++;

                NetNode.Flags editBlockers = NetNode.Flags.Untouchable;
                if ((node.m_flags & editBlockers) != 0)
                {
                    stats.EditLockedStops++;
                    if (promote)
                    {
                        node.m_flags &= ~editBlockers;
                        stats.EditLocksCleared++;
                        changed = true;
                    }
                }

                if ((node.m_flags & NetNode.Flags.Moveable) == 0)
                {
                    if (promote && applyMoveable)
                    {
                        node.m_flags |= NetNode.Flags.Moveable;
                        stats.MoveableFlagsApplied++;
                        changed = true;
                    }
                }

                if (promote)
                {
                    if (changed)
                    {
                        node.m_buildIndex = sm.m_currentBuildIndex;
                        sm.m_currentBuildIndex++;

                        try
                        {
                            nm.UpdateNode(currentStop);
                            stats.UpdatedNodes++;
                        }
                        catch
                        {
                            stats.NodeUpdateFailures++;
                        }
                    }
                }

                safety++;
                currentStop = TransportLine.GetNextStop(currentStop);
                if (currentStop == firstStop)
                    break;
            }

            if (currentStop == 0 || safety >= 512)
                stats.BrokenStopChains++;

            return stats;
        }

        private bool ShouldMigrateLegacyFixedBusLineForEditability(string reason, ushort lineId, ref TransportLine line, StopPublicationStats stats)
        {
            if (stats == null)
                return false;

            if (!IsLegacyEditMigrationReason(reason))
                return false;

            if (stats.Stops < 2 || stats.FixedStops != stats.Stops)
                return false;

            if (stats.TemporaryStops > 0 || stats.BrokenStopChains > 0 || stats.MissingCreatedStops > 0)
                return false;

            if (!IsVanillaOverviewVisibleBusLine(ref line))
                return false;

            TransportInfo info = line.Info;
            if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                return false;

            if (line.m_vehicles != 0 || CountLineVehicles(ref line, lineId) > 0)
            {
                stats.LegacyEditableLinesSkippedLive++;
                return false;
            }

            return true;
        }

        private bool IsLegacyEditMigrationReason(string reason)
        {
            return string.Equals(reason, "startup", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(reason, "pre-scan", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryMigrateLegacyFixedBusLineForEditability(ushort lineId, ref TransportLine line, string reason, StopPublicationStats stats)
        {
            List<StopSnapshot> stops = GetLineStopSnapshots(ref line);
            if (stops.Count < 2)
            {
                stats.LegacyEditableMigrationFailures++;
                TransitLogging.Warn("Skipped legacy editable migration for bus line " + lineId + " during " + reason + ": fewer than 2 readable stops.");
                return false;
            }

            TransportInfo info = line.Info;
            string validationFailure;
            if (!TryValidateEditableStopGraph(stops, info, lineId, out validationFailure))
            {
                stats.LegacyEditableMigrationFailures++;
                TransitLogging.Warn("Skipped legacy editable migration for bus line " + lineId + " during " + reason + ": validation failed: " + validationFailure + ".");
                return false;
            }

            TransportLine.Flags preservedFlags = line.m_flags & (
                TransportLine.Flags.CustomColor |
                TransportLine.Flags.CustomName |
                TransportLine.Flags.DisabledDay |
                TransportLine.Flags.DisabledNight);
            Color32 color = line.m_color;
            ushort lineNumber = line.m_lineNumber;
            ushort budget = line.m_budget;
            ushort ticketPrice = line.m_ticketPrice;
            byte averageInterval = line.m_averageInterval;

            string rebuildFailure;
            if (!TryRebuildLineStopGraph(lineId, ref line, stops, false, out rebuildFailure))
            {
                string restoreFailure;
                TryRebuildLineStopGraph(lineId, ref line, stops, true, out restoreFailure);
                stats.LegacyEditableMigrationFailures++;
                TransitLogging.Warn(
                    "Failed legacy editable migration for bus line " + lineId +
                    " during " + reason +
                    ": rebuild failed: " + rebuildFailure +
                    (string.IsNullOrEmpty(restoreFailure) ? "." : "; restore result: " + restoreFailure + "."));
                return false;
            }

            line.m_color = color;
            line.m_lineNumber = lineNumber;
            line.m_budget = budget;
            line.m_ticketPrice = ticketPrice;
            line.m_averageInterval = averageInterval;
            line.m_flags &= ~(TransportLine.Flags.Temporary | TransportLine.Flags.Hidden | TransportLine.Flags.Selected | TransportLine.Flags.Highlighted | TransportLine.Flags.Invalid);
            line.m_flags |= TransportLine.Flags.Created | TransportLine.Flags.Complete | TransportLine.Flags.CompleteSet | preservedFlags;

            try
            {
                line.CheckCompletionMilestone();
            }
            catch
            {
            }

            try
            {
                line.UpdatePaths(lineId);
            }
            catch
            {
            }

            try
            {
                line.UpdateMeshData(lineId);
            }
            catch
            {
            }

            try
            {
                TransportManager.instance.UpdateLine(lineId);
                TransportManager.instance.UpdateLinesNow();
            }
            catch
            {
            }

            StopPublicationStats publishStats = PublishGeneratedLineStops(lineId, ref line);
            stats.LegacyEditableLinesMigrated++;
            stats.LegacyEditableStopsMigrated += stops.Count;
            stats.MoveableFlagsApplied += publishStats.MoveableFlagsApplied;
            stats.EditLocksCleared += publishStats.EditLocksCleared;
            stats.TemporaryStopsCleared += publishStats.TemporaryStopsCleared;
            stats.TransportLineOwnersFixed += publishStats.TransportLineOwnersFixed;
            stats.UpdatedNodes += publishStats.UpdatedNodes;
            stats.NodeUpdateFailures += publishStats.NodeUpdateFailures;

            TransitLogging.Log(
                "Migrated legacy fixed bus line " + lineId +
                " to editable stop nodes during " + reason +
                ": stops=" + stops.Count + ".");
            return true;
        }

        private List<StopSnapshot> GetLineStopSnapshots(ref TransportLine line)
        {
            var stops = new List<StopSnapshot>();
            ushort firstStop = line.m_stops;
            if (firstStop == 0)
                return stops;

            NetManager nm = NetManager.instance;
            ushort currentStop = firstStop;
            int safety = 0;

            do
            {
                if (currentStop == 0 || currentStop >= nm.m_nodes.m_buffer.Length)
                    break;

                ref NetNode node = ref nm.m_nodes.m_buffer[currentStop];
                stops.Add(new StopSnapshot
                {
                    Position = node.m_position,
                    FixedPlatform = (node.m_flags & NetNode.Flags.Fixed) != 0
                });

                currentStop = TransportLine.GetNextStop(currentStop);
                safety++;
            }
            while (currentStop != 0 && currentStop != firstStop && safety < 512);

            return stops;
        }

        private bool TryValidateEditableStopGraph(List<StopSnapshot> stops, TransportInfo info, ushort sourceLineId, out string failureReason)
        {
            failureReason = null;
            if (stops == null || stops.Count < 2)
            {
                failureReason = "fewer than 2 stops";
                return false;
            }

            if (info == null)
            {
                failureReason = "missing transport info";
                return false;
            }

            TransportManager tm = TransportManager.instance;
            Randomizer randomizer = SimulationManager.instance.m_randomizer;
            ushort probeLineId;
            if (!tm.CreateLine(out probeLineId, ref randomizer, info, false))
            {
                failureReason = "could not create validation probe";
                return false;
            }

            try
            {
                ref TransportLine probeLine = ref tm.m_lines.m_buffer[probeLineId];
                probeLine.m_flags |= TransportLine.Flags.Temporary | TransportLine.Flags.Hidden;
                string rebuildFailure;
                if (!TryRebuildLineStopGraph(probeLineId, ref probeLine, stops, false, out rebuildFailure))
                {
                    failureReason = rebuildFailure;
                    return false;
                }

                if (!probeLine.Complete)
                {
                    failureReason = "validation probe did not complete";
                    return false;
                }

                return true;
            }
            finally
            {
                SafeReleaseLine(probeLineId);
            }
        }

        private bool TryRebuildLineStopGraph(ushort lineId, ref TransportLine line, List<StopSnapshot> stops, bool preserveFixedPlatforms, out string failureReason)
        {
            failureReason = null;
            if (stops == null || stops.Count < 2)
            {
                failureReason = "fewer than 2 stops";
                return false;
            }

            try
            {
                line.ClearStops(lineId);
            }
            catch (Exception e)
            {
                failureReason = "ClearStops failed: " + e.Message;
                return false;
            }

            for (int i = 0; i < stops.Count; i++)
            {
                bool fixedPlatform = preserveFixedPlatforms && stops[i].FixedPlatform;
                if (!TryAddStopToLine(lineId, i, stops[i].Position, fixedPlatform, out failureReason))
                    return false;
            }

            if (!TryCloseLine(lineId, stops[0].Position, out failureReason))
                return false;

            ref TransportLine rebuiltLine = ref TransportManager.instance.m_lines.m_buffer[lineId];
            if (!rebuiltLine.Complete)
            {
                failureReason = "rebuilt line did not become complete";
                return false;
            }

            return true;
        }

        private System.Collections.IEnumerator RefreshPublicTransportOverviewPanelsDeferred()
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                for (int frame = 0; frame < 10; frame++)
                    yield return null;

                RefreshPublicTransportOverviewPanels("post-publish deferred " + attempt, false);
            }
        }

        private void RefreshPublicTransportOverviewPanels(string reason, bool suppressLineDetails)
        {
            try
            {
                List<PublicTransportDetailPanel> panels = GetLoadedPublicTransportDetailPanels();
                int refreshedPanels = 0;
                int maxPanelBusRows = -1;

                for (int i = 0; i < panels.Count; i++)
                {
                    PublicTransportDetailPanel panel = panels[i];
                    if (panel == null)
                        continue;

                    panel.RefreshLines();
                    refreshedPanels++;

                    int panelBusRows = GetPublicTransportPanelBusRows(panel);
                    if (panelBusRows > maxPanelBusRows)
                        maxPanelBusRows = panelBusRows;
                }

                string panelRows = maxPanelBusRows >= 0
                    ? maxPanelBusRows.ToString(CultureInfo.InvariantCulture)
                    : "n/a";
                int suppressedDetailPanels = suppressLineDetails ? SuppressVanillaTransportLineDetailPanels(reason) : 0;

                TransitLogging.Log(
                    "Refreshed public transport line overview panels (" + reason +
                    "): panels=" + refreshedPanels +
                    ", panelBusRows=" + panelRows +
                    ", vanillaOverviewVisible=" + CountVanillaOverviewVisibleBusLines() +
                    ", suppressedLineDetails=" + suppressedDetailPanels + ".");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to refresh public transport line overview panels: " + e.Message);
            }
        }

        private int SuppressVanillaTransportLineDetailPanels(string reason)
        {
            int hidden = 0;

            try
            {
                var candidates = new List<UIComponent>();
                UnityEngine.Object[] panels = UnityEngine.Object.FindObjectsOfType(typeof(UIPanel));
                for (int i = 0; i < panels.Length; i++)
                {
                    UIComponent root = GetVanillaTransportLineDetailPanelRoot(panels[i] as UIComponent);
                    if (root == null || candidates.Contains(root))
                        continue;

                    candidates.Add(root);
                }

                for (int i = 0; i < candidates.Count; i++)
                {
                    UIComponent candidate = candidates[i];
                    if (candidate == null || !candidate.isVisible)
                        continue;

                    bool coveredByParent = false;
                    for (int j = 0; j < candidates.Count; j++)
                    {
                        if (i == j)
                            continue;

                        UIComponent other = candidates[j];
                        if (other != null && IsAncestorOf(other, candidate))
                        {
                            coveredByParent = true;
                            break;
                        }
                    }

                    if (coveredByParent)
                        continue;

                    candidate.Hide();
                    hidden++;
                }

                if (hidden > 0)
                    TransitLogging.Log("Suppressed " + hidden + " vanilla transport line detail panel(s) after " + reason + ".");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to suppress vanilla transport line detail panels: " + e.Message);
            }

            return hidden;
        }

        private UIComponent GetVanillaTransportLineDetailPanelRoot(UIComponent component)
        {
            if (!IsVanillaTransportLineDetailPanel(component))
                return null;

            UIComponent root = component;
            UIComponent parent = component.parent;
            while (IsVanillaTransportLineDetailPanel(parent))
            {
                root = parent;
                parent = parent.parent;
            }

            return root;
        }

        private bool IsVanillaTransportLineDetailPanel(UIComponent component)
        {
            if (component == null)
                return false;

            if (!component.isVisible)
                return false;

            if (!IsSuppressibleDialogSizedPanel(component))
                return false;

            if (IsAutoPublicTransitComponent(component))
                return false;

            string componentName = component.name == null ? "" : component.name.ToLowerInvariant();
            string typeName = component.GetType().Name.ToLowerInvariant();
            string fullName = component.GetType().FullName == null ? "" : component.GetType().FullName.ToLowerInvariant();
            string combined = componentName + " " + typeName + " " + fullName;

            if (combined.Contains("publictransportdetailpanel"))
                return false;

            if (combined.Contains("publictransport") &&
                (combined.Contains("lineinfo") || combined.Contains("worldinfo") || combined.Contains("infopanel")))
            {
                return true;
            }

            return GetTransportLineDetailMarkerScore(component) >= 2;
        }

        private bool IsSuppressibleDialogSizedPanel(UIComponent component)
        {
            if (component == null)
                return false;

            if (component.width < 240f || component.height < 140f)
                return false;

            if (component.width > 950f || component.height > 900f)
                return false;

            return true;
        }

        private bool IsAutoPublicTransitComponent(UIComponent component)
        {
            while (component != null)
            {
                string componentName = component.name == null ? "" : component.name.ToLowerInvariant();
                string typeName = component.GetType().Name.ToLowerInvariant();
                string fullName = component.GetType().FullName == null ? "" : component.GetType().FullName.ToLowerInvariant();

                if (componentName.Contains("autopublictransit") ||
                    typeName.Contains("autopublictransit") ||
                    fullName.Contains("autopublictransit"))
                {
                    return true;
                }

                component = component.parent;
            }

            return false;
        }

        private int GetTransportLineDetailMarkerScore(UIComponent root)
        {
            if (root == null)
                return 0;

            var markers = new HashSet<string>();
            UIComponent[] children = root.GetComponentsInChildren<UIComponent>(true);
            for (int i = 0; i < children.Length; i++)
            {
                AddTransportLineDetailTextMarkers(children[i] as UILabel, markers);
                AddTransportLineDetailTextMarkers(children[i] as UIButton, markers);
            }

            return markers.Count;
        }

        private void AddTransportLineDetailTextMarkers(UILabel label, HashSet<string> markers)
        {
            if (label == null)
                return;

            AddTransportLineDetailTextMarkers(label.text, markers);
        }

        private void AddTransportLineDetailTextMarkers(UIButton button, HashSet<string> markers)
        {
            if (button == null)
                return;

            AddTransportLineDetailTextMarkers(button.text, markers);
        }

        private void AddTransportLineDetailTextMarkers(string text, HashSet<string> markers)
        {
            if (string.IsNullOrEmpty(text) || markers == null)
                return;

            string value = text.ToLowerInvariant();
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

            if (value.Contains("delete line"))
                markers.Add("delete-line");
            if (value.Contains("add vehicle"))
                markers.Add("add-vehicle");
            if (value.Contains("remove vehicle"))
                markers.Add("remove-vehicle");
            if (value.Contains("budget control"))
                markers.Add("budget-control");
            if (value.Contains("unbunching"))
                markers.Add("unbunching");
            if (value.Contains("line activity"))
                markers.Add("line-activity");
            if (value.Contains("passengers last 10 min"))
                markers.Add("recent-passengers");
            if (value.Trim() == "depot:" || value.Trim() == "depot")
                markers.Add("depot");
            if (value.Contains("refresh") && value.Contains("name") && value.Contains("color"))
                markers.Add("refresh-name-color");
        }

        private bool IsAncestorOf(UIComponent ancestor, UIComponent component)
        {
            if (ancestor == null || component == null)
                return false;

            UIComponent current = component.parent;
            while (current != null)
            {
                if (current == ancestor)
                    return true;

                current = current.parent;
            }

            return false;
        }

        private List<PublicTransportDetailPanel> GetLoadedPublicTransportDetailPanels()
        {
            var panels = new List<PublicTransportDetailPanel>();

            try
            {
                if (UIView.library != null)
                {
                    PublicTransportDetailPanel libraryPanel = UIView.library.Get<PublicTransportDetailPanel>("PublicTransportDetailPanel");
                    if (libraryPanel != null)
                        panels.Add(libraryPanel);
                }
            }
            catch
            {
            }

            UnityEngine.Object[] foundPanels = UnityEngine.Object.FindObjectsOfType(typeof(PublicTransportDetailPanel));
            for (int i = 0; i < foundPanels.Length; i++)
            {
                PublicTransportDetailPanel panel = foundPanels[i] as PublicTransportDetailPanel;
                if (panel != null && !panels.Contains(panel))
                    panels.Add(panel);
            }

            return panels;
        }

        private int GetPublicTransportPanelBusRows(PublicTransportDetailPanel panel)
        {
            if (panel == null || PublicTransportDetailPanelBusCountField == null)
                return -1;

            object value = PublicTransportDetailPanelBusCountField.GetValue(panel);
            if (value is int)
                return (int)value;

            return -1;
        }

        private int CountVanillaOverviewVisibleBusLines()
        {
            TransportManager tm = TransportManager.instance;
            int visibleBusLines = 0;

            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if (IsVanillaOverviewVisibleBusLine(ref line))
                    visibleBusLines++;
            }

            return visibleBusLines;
        }

        private void SafeReleaseLine(ushort lineId)
        {
            TransportManager tm = TransportManager.instance;
            if (tm == null || lineId == 0 || lineId >= tm.m_lines.m_size)
                return;

            ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
                return;

            try
            {
                tm.ReleaseLine(lineId);
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to release generated-line probe " + lineId + ": " + e.Message);
            }
        }

        private void CountGeneratedRouteIntegrityFailure(TransitScanSummary scanSummary)
        {
            if (scanSummary == null)
                return;

            scanSummary.GeneratedRoutesSkipped++;
            scanSummary.GeneratedRoutesIntegrityFailed++;
        }

        private bool TryAddStopToLine(ushort lineId, int index, Vector3 position, out string failureReason)
        {
            return TryAddStopToLine(lineId, index, position, false, out failureReason);
        }

        private bool TryAddStopToLine(ushort lineId, int index, Vector3 position, bool fixedPlatform, out string failureReason)
        {
            TransportManager tm = TransportManager.instance;
            ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
            {
                failureReason = "line was no longer created";
                return false;
            }

            if (!line.CanAddStop(lineId, index, position))
            {
                failureReason = "CanAddStop rejected index " + index;
                return false;
            }

            if (!line.AddStop(lineId, index, position, fixedPlatform))
            {
                failureReason = "AddStop returned false at index " + index;
                return false;
            }

            failureReason = null;
            return true;
        }

        private bool TryFindReusableGeneratedStop(
            Vector3 proposedStop,
            List<ExistingLineSnapshot> existingLines,
            float maxDistance,
            out Vector3 reusableStop)
        {
            reusableStop = Vector3.zero;
            if (existingLines == null || existingLines.Count == 0)
                return false;

            float bestDistance = maxDistance;
            bool found = false;
            for (int i = 0; i < existingLines.Count; i++)
            {
                ExistingLineSnapshot line = existingLines[i];
                if (line == null || line.Stops == null)
                    continue;

                Vector3 candidate;
                float distance;
                if (!TryFindNearbyStop(proposedStop, line.Stops, bestDistance, out candidate, out distance))
                    continue;

                reusableStop = candidate;
                bestDistance = distance;
                found = true;
            }

            return found;
        }

        private bool TryFindNearbyStop(Vector3 proposedStop, List<Vector3> stops, float maxDistance, out Vector3 reusableStop)
        {
            float distance;
            return TryFindNearbyStop(proposedStop, stops, maxDistance, out reusableStop, out distance);
        }

        private bool TryFindNearbyStop(Vector3 proposedStop, List<Vector3> stops, float maxDistance, out Vector3 reusableStop, out float distance)
        {
            reusableStop = Vector3.zero;
            distance = maxDistance;
            if (stops == null || stops.Count == 0)
                return false;

            bool found = false;
            for (int i = 0; i < stops.Count; i++)
            {
                float candidateDistance = Geometry.DistanceXZ(proposedStop, stops[i]);
                if (candidateDistance > distance)
                    continue;

                reusableStop = stops[i];
                distance = candidateDistance;
                found = true;
            }

            return found;
        }

        private bool TryCloseLine(ushort lineId, Vector3 firstStop, out string failureReason)
        {
            TransportManager tm = TransportManager.instance;
            ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
            {
                failureReason = "line was no longer created";
                return false;
            }

            if (!line.CanAddStop(lineId, -1, firstStop))
            {
                failureReason = "CanAddStop rejected closing stop";
                return false;
            }

            if (!line.AddStop(lineId, -1, firstStop, false))
            {
                failureReason = "AddStop returned false for closing stop";
                return false;
            }

            failureReason = null;
            return true;
        }

        private bool TryRemoveStopFromLine(ushort lineId, int index, out string failureReason)
        {
            TransportManager tm = TransportManager.instance;
            ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
            if ((line.m_flags & TransportLine.Flags.Created) == 0)
            {
                failureReason = "line was no longer created";
                return false;
            }

            if (!line.RemoveStop(lineId, index))
            {
                failureReason = "RemoveStop returned false at index " + index;
                return false;
            }

            failureReason = null;
            return true;
        }

        private List<ExistingLineSnapshot> AuditExistingBusNetwork(AutoPublicTransitConfig cfg)
        {
            var existingLines = new List<ExistingLineSnapshot>();
            TransportManager tm = TransportManager.instance;
            int removedLines = 0;
            int retiredWeakLines = 0;
            int releasedStaleProbes = 0;

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
                if (protectedSchoolBusRoute || playerProtectedLine)
                {
                    List<Vector3> protectedStops = GetExistingLineStops(ref line);
                    if (protectedStops.Count > 0)
                        existingLines.Add(BuildCoverageOnlyLineSnapshot(lineId, protectedStops, protectedSchoolBusRoute, playerProtectedLine));

                    continue;
                }

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) == (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden))
                {
                    tm.ReleaseLine(lineId);
                    releasedStaleProbes++;
                    continue;
                }

                List<Vector3> stops = GetExistingLineStops(ref line);
                if (!line.Complete || stops.Count < 2)
                {
                    tm.ReleaseLine(lineId);
                    removedLines++;
                    continue;
                }

                existingLines.Add(new ExistingLineSnapshot
                {
                    LineId = lineId,
                    Stops = stops,
                    TotalLength = ComputeRouteLength(stops)
                });
            }

            var retireLineIds = FindWeakDuplicateLines(existingLines, cfg);
            for (int i = 0; i < retireLineIds.Count; i++)
            {
                ushort lineId = retireLineIds[i];
                tm.ReleaseLine(lineId);
                existingLines.RemoveAll(line => line.LineId == lineId);
                retiredWeakLines++;
            }

            TransitLogging.Log("Existing complete bus lines retained: " + existingLines.Count + ".");
            if (releasedStaleProbes > 0)
                TransitLogging.Log("Released " + releasedStaleProbes + " stale hidden generated-line probes.");
            if (removedLines > 0)
                TransitLogging.Log("Removed " + removedLines + " incomplete or dead-end bus lines.");
            if (retiredWeakLines > 0)
                TransitLogging.Log("Retired " + retiredWeakLines + " weak duplicate bus lines.");

            return existingLines;
        }
    }
}
