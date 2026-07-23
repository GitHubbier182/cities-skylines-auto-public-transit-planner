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
        public static Manager Instance;

        private bool _initialized;
        private bool _hotkeyLatch;
        private bool _scanRunning;
        private bool _roadUpgradeRunning;
        private BusRouteRoadUpgradePlan _pendingRouteRoadUpgradePlan;
        private BusRouteRoadUpgradeApplyState _activeRouteRoadUpgradeApplyState;
        private bool _routeRoadUpgradeApplyScheduled;
        private uint _nextActiveDepotDispatchCheckFrame;
        private readonly TransitRouteBuilder _routeBuilder = new TransitRouteBuilder();

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            if (_initialized)
                return;

            _initialized = true;
            ConfigManager.Load();
            TransitLogging.Log("Manager initialized.");
            StartCoroutine(RepairPublishedBusStopNodesDeferred("startup"));
        }

        private void Update()
        {
            bool ctrlDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool togglePressed = ctrlDown && Input.GetKey(KeyCode.T);

            if (togglePressed && !_hotkeyLatch)
            {
                AutoPublicTransitUI.Toggle();
            }

            _hotkeyLatch = togglePressed;
            UpdateActiveDepotDispatchMonitor();
            UpdateDepotReturnFastDespawn();
        }

        public void RunScan()
        {
            if (_scanRunning || _metroScanRunning || _trainScanRunning)
            {
                TransitLogging.Warn("A transit scan is already running.");
                AutoPublicTransitUI.UpdateScanStatus("Last scan: already running");
                return;
            }

            _scanRunning = true;
            AutoPublicTransitUI.UpdateScanStatus("Last scan: running");
            TransitLogging.Log("Starting transit demand scan...");
            var scanSummary = new TransitScanSummary();
            List<GeneratedLineProbe> generatedLineProbes = null;
            State.LastScanSummary = scanSummary;

            try
            {
                ConfigManager.ApplyLockedBusPlanningProfile();
                var cfg = ConfigManager.Config;
                DateTime startedAt = DateTime.UtcNow;
                DateTime stageStartedAt = startedAt;
                var transitHubs = CollectTransitHubs(cfg);
                scanSummary.TransitHubCount = transitHubs.Count;
                LogScanStage("collect transit hubs", ref stageStartedAt);
                var touristAnchors = CollectTouristAnchors();
                var touristAnchorPositions = GetTouristAnchorPositions(touristAnchors);
                scanSummary.TouristAnchorCount = touristAnchors.Count;
                LogScanStage("collect tourist anchors", ref stageStartedAt);
                var stopLocator = new BusStopLocator(cfg.AvoidHighways, cfg.GridCellSize);
                LogScanStage("index bus-stop roads", ref stageStartedAt);
                TransitLogging.Log(
                    "Indexed bus-stop road segments: " + stopLocator.IndexedSegments +
                    "; rejected underground/tunnel=" + stopLocator.RejectedUndergroundSegments +
                    ", collapsed=" + stopLocator.RejectedCollapsedSegments +
                    ", missingNode=" + stopLocator.RejectedMissingNodeSegments + ".");
                scanSummary.RejectedUndergroundStopSegments = stopLocator.RejectedUndergroundSegments;
                scanSummary.RejectedCollapsedStopSegments = stopLocator.RejectedCollapsedSegments;
                scanSummary.RejectedMissingNodeStopSegments = stopLocator.RejectedMissingNodeSegments;
                RepairPublishedBusStopNodes("pre-scan");
                var existingLines = MaintainExistingBusNetwork(cfg, transitHubs, touristAnchorPositions, stopLocator, scanSummary);
                LogScanStage("maintain existing bus network", ref stageStartedAt);
                var demandGrid = new DemandGrid(cfg.GridCellSize);
                var demandMap = new Dictionary<ushort, int>();
                int candidateStops = 0;
                int cacheHits = 0;
                int cacheMisses = 0;
                int cacheInvalidations = 0;
                int eligibleBuildings = 0;
                int scannedBuildings = 0;
                int sampledBuildings = 0;
                int stopSearchFailures = 0;
                int zeroWeightBuildings = 0;
                int coverageDiscountedBuildings = 0;
                List<Vector3> existingStops = FlattenStops(existingLines);

                BuildingManager bm = BuildingManager.instance;
                if (bm == null)
                    throw new InvalidOperationException("Building data is not ready yet. Wait for the city to finish loading, then scan again.");

                var buffer = bm.m_buildings.m_buffer;

                for (ushort id = 1; id < buffer.Length; id++)
                {
                    ref Building b = ref buffer[id];
                    if (b.m_flags == 0)
                        continue;

                    if (!IsDemandService(ref b))
                        continue;

                    eligibleBuildings++;
                    scannedBuildings++;

                    Vector3 pos = b.m_position;

                    CachedStopMatch stopMatch;
                    bool usedCache;
                    bool hadCache = State.StopCache.ContainsKey(id);
                    if (!TryGetNearestBusStopPositionCached(id, pos, cfg, stopLocator, out stopMatch, out usedCache))
                    {
                        if (hadCache)
                            cacheInvalidations++;

                        stopSearchFailures++;
                        continue;
                    }

                    if (usedCache)
                    {
                        cacheHits++;
                    }
                    else if (hadCache)
                    {
                        cacheInvalidations++;
                    }
                    else
                    {
                        cacheMisses++;
                    }

                    int weight = ScoreBuilding(id, ref b);
                    if (weight <= 0)
                    {
                        zeroWeightBuildings++;
                        continue;
                    }

                    weight = ApplyExistingCoverageDiscount(weight, pos, existingStops, cfg);
                    if (weight <= 0)
                    {
                        coverageDiscountedBuildings++;
                        continue;
                    }

                    demandGrid.AddSample(pos, stopMatch, weight, GetDemandNodePurpose(ref b));
                    demandMap[id] = weight;
                    candidateStops++;
                    sampledBuildings++;
                }
                LogScanStage("sample demand buildings", ref stageStartedAt);

                InjectTransitHubDemand(demandGrid, transitHubs, existingStops, cfg, stopLocator);
                LogScanStage("inject transit hub demand", ref stageStartedAt);
                InjectTouristDemand(demandGrid, touristAnchors, existingStops, cfg, stopLocator);
                LogScanStage("inject tourist demand", ref stageStartedAt);
                InjectZonedRciDemand(demandGrid, existingStops, cfg, stopLocator);
                LogScanStage("inject zoned RCI demand", ref stageStartedAt);

                TransitLogging.Verbose("Demand-scored buildings: " + demandMap.Count);
                TransitLogging.Log("Valid stop candidates: " + candidateStops);
                TransitLogging.Log("Full city demand pass considered " + scannedBuildings + " of " + eligibleBuildings + " eligible buildings.");
                TransitLogging.Log(
                    "Demand filters: accepted=" + sampledBuildings +
                    ", zeroWeight=" + zeroWeightBuildings +
                    ", alreadyCovered=" + coverageDiscountedBuildings +
                    ", noStop=" + stopSearchFailures + ".");
                TransitLogging.Log(
                    "Stop cache: hits=" + cacheHits +
                    ", misses=" + cacheMisses +
                    ", invalidated=" + cacheInvalidations +
                    ", size=" + State.StopCache.Count + ".");
                if (cfg.LinkToOtherTransit)
                    TransitLogging.Log("Transit hubs considered: " + transitHubs.Count);
                TransitLogging.Log("Tourist anchors considered: " + touristAnchors.Count);
                scanSummary.EligibleBuildings = eligibleBuildings;
                scanSummary.ScannedBuildings = scannedBuildings;
                scanSummary.ValidStopCandidates = candidateStops;
                scanSummary.AcceptedDemandBuildings = sampledBuildings;
                scanSummary.ZeroWeightBuildings = zeroWeightBuildings;
                scanSummary.AlreadyCoveredBuildings = coverageDiscountedBuildings;
                scanSummary.NoStopBuildings = stopSearchFailures;
                scanSummary.StopCacheHits = cacheHits;
                scanSummary.StopCacheMisses = cacheMisses;
                scanSummary.StopCacheInvalidations = cacheInvalidations;
                scanSummary.StopCacheSize = State.StopCache.Count;

                List<DemandNode> nodes = demandGrid.ToNodes(cfg.DemandThreshold);
                scanSummary.DemandNodeCount = nodes.Count;
                LogScanStage("aggregate demand nodes", ref stageStartedAt);
                TransitLogging.Log("Demand nodes: " + nodes.Count);
                LogDemandNodePurposeSummary(nodes, scanSummary);

                List<List<Vector3>> routes = _routeBuilder.BuildRoutes(nodes, cfg, stopLocator);
                scanSummary.BuiltRouteCount = routes.Count;
                LogScanStage("build route candidates", ref stageStartedAt);
                TransitLogging.Log("Built routes: " + routes.Count);
                TransitLogging.Log(
                    "Planner link diagnostics: locationFallbacks=" + _routeBuilder.LocationFallbackLinkCount +
                    ", roadGraphRejected=" + _routeBuilder.RoadGraphRejectedLinkCount +
                    ", touristCoverageRoutes=" + _routeBuilder.TouristCoverageRouteCount +
                    ", mandatoryHubStops=" + _routeBuilder.MandatoryTransitHubInsertionCount +
                    ", mandatoryHubMisses=" + _routeBuilder.MandatoryTransitHubMissCount +
                    ", secondPassConnectorStops=" + _routeBuilder.RouteConnectorInsertionCount +
                    ", secondPassConnectedRoutes=" + _routeBuilder.RouteConnectorConnectedRouteCount +
                    ", unconnectedRoutesRejected=" + _routeBuilder.RouteConnectorRejectedRouteCount + ".");
                LogGeneratedRoutePurposeSummary(routes, nodes, scanSummary);

                State.LastDemandMap = demandMap;
                State.LastRoutes = routes;
                State.HasScanRun = true;

                EnsureBusDepotCoverage(nodes);
                LogScanStage("ensure depot coverage", ref stageStartedAt);
                generatedLineProbes = BuildGeneratedLineProbes(routes, existingLines, cfg, stopLocator, scanSummary);
                LogScanStage("apply generated bus lines", ref stageStartedAt);
                StartCoroutine(FinalizeScanAfterGeneratedLineSettlement(generatedLineProbes, existingLines, nodes, transitHubs, cfg, stopLocator, scanSummary, startedAt, stageStartedAt));
                generatedLineProbes = null;
            }
            catch (Exception e)
            {
                ReleasePendingGeneratedLineProbes(generatedLineProbes, "the bus scan failed before asynchronous settlement took ownership");
                TransitLogging.Error("Transit scan failed: " + e);
                scanSummary.FailureMessage = e.Message;
                State.LastScanSummary = scanSummary;
                AutoPublicTransitUI.UpdateScanSummary(scanSummary);
                _scanRunning = false;
            }
        }

        private System.Collections.IEnumerator FinalizeScanAfterGeneratedLineSettlement(
            List<GeneratedLineProbe> generatedLineProbes,
            List<ExistingLineSnapshot> existingLines,
            List<DemandNode> nodes,
            List<Vector3> transitHubs,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransitScanSummary scanSummary,
            DateTime startedAt,
            DateTime stageStartedAt)
        {
            yield return StartCoroutine(SettleGeneratedLineProbes(generatedLineProbes, existingLines, cfg, stopLocator, scanSummary, "settling generated paths", 5, 3));
            if (!_scanRunning)
                yield break;

            const int maxCoverageBackfillRounds = 3;
            for (int backfillRound = 1; backfillRound <= maxCoverageBackfillRounds; backfillRound++)
            {
                LogPublishedCoverageAudit("pre-backfill round " + backfillRound, nodes, existingLines, transitHubs, cfg);
                List<List<Vector3>> coverageBackfillRoutes = _routeBuilder.BuildCoverageBackfillRoutes(nodes, existingLines, transitHubs, cfg, stopLocator);
                if (coverageBackfillRoutes.Count > 0)
                {
                    TransitLogging.Log(
                        "Coverage backfill round " + backfillRound +
                        " planned " + coverageBackfillRoutes.Count +
                        " route(s) for " + _routeBuilder.CoverageBackfillUncoveredNodeCount +
                        " uncovered demand node(s) after published-line settlement using " +
                        _routeBuilder.CoverageBackfillConnectorCount + " connector node(s), connectedLines=" +
                        _routeBuilder.CoverageBackfillConnectedLineCount +
                        ", isolatedLines=" + _routeBuilder.CoverageBackfillUnconnectedLineCount +
                        ", strategicFallbackRoutes=" + _routeBuilder.CoverageBackfillStrategicFallbackRouteCount + ".");

                    int createdBeforeBackfill = scanSummary != null ? scanSummary.CreatedLines : 0;
                    List<GeneratedLineProbe> coverageBackfillProbes = BuildGeneratedLineProbes(coverageBackfillRoutes, existingLines, cfg, stopLocator, scanSummary, false);
                    yield return StartCoroutine(SettleGeneratedLineProbes(coverageBackfillProbes, existingLines, cfg, stopLocator, scanSummary, "settling coverage backfill round " + backfillRound, 4, 2));
                    if (!_scanRunning)
                        yield break;

                    LogScanStage("coverage backfill round " + backfillRound, ref stageStartedAt);
                    LogPublishedCoverageAudit("post-backfill round " + backfillRound, nodes, existingLines, transitHubs, cfg);

                    int createdThisBackfill = (scanSummary != null ? scanSummary.CreatedLines : 0) - createdBeforeBackfill;
                    if (createdThisBackfill <= 0)
                        break;

                    continue;
                }

                if (_routeBuilder.CoverageBackfillUncoveredNodeCount > 0)
                {
                    TransitLogging.Log(
                        "Coverage backfill round " + backfillRound +
                        " found " + _routeBuilder.CoverageBackfillUncoveredNodeCount +
                        " uncovered demand node(s), but no connected local route candidates survived planning using " +
                        _routeBuilder.CoverageBackfillConnectorCount + " connector node(s), connectedLines=" +
                        _routeBuilder.CoverageBackfillConnectedLineCount +
                        ", isolatedLines=" + _routeBuilder.CoverageBackfillUnconnectedLineCount +
                        ", strategicFallbackRoutes=" + _routeBuilder.CoverageBackfillStrategicFallbackRouteCount + ".");
                }
                else
                {
                    TransitLogging.Log("Coverage backfill found no uncovered demand nodes after published-line settlement.");
                }

                break;
            }
            LogPublishedCoverageAudit("final post-backfill", nodes, existingLines, transitHubs, cfg);

            List<List<Vector3>> regionalConnectorRoutes = _routeBuilder.BuildRegionalConnectorRoutes(existingLines, transitHubs, cfg, stopLocator);
            TransitLogging.Log(
                "Regional connector pass planned " + regionalConnectorRoutes.Count +
                " route(s) from " + _routeBuilder.RegionalConnectorAnchorCount +
                " settled line/hub anchor(s) in " + _routeBuilder.RegionalConnectorClusterCount +
                " network pocket(s), transitHubAnchors=" + _routeBuilder.RegionalConnectorTransitHubAnchorCount +
                ", rejected=" + _routeBuilder.RegionalConnectorRejectedRouteCount + ".");

            if (regionalConnectorRoutes.Count > 0)
            {
                int createdBeforeRegionalConnector = scanSummary != null ? scanSummary.CreatedLines : 0;
                List<GeneratedLineProbe> regionalConnectorProbes = BuildGeneratedLineProbes(regionalConnectorRoutes, existingLines, cfg, stopLocator, scanSummary, false);
                yield return StartCoroutine(SettleGeneratedLineProbes(regionalConnectorProbes, existingLines, cfg, stopLocator, scanSummary, "settling regional connector paths", 5, 3));
                if (!_scanRunning)
                    yield break;

                LogScanStage("regional connector pass", ref stageStartedAt);
                int createdRegionalConnectors = (scanSummary != null ? scanSummary.CreatedLines : 0) - createdBeforeRegionalConnector;
                TransitLogging.Log("Regional connector pass published " + createdRegionalConnectors + " route(s) after hidden-line settlement.");
                LogPublishedCoverageAudit("post-regional connector", nodes, existingLines, transitHubs, cfg);
            }

            bool finalizeFailed = false;
            try
            {
                LogScanStage("settle generated bus lines", ref stageStartedAt);
                LogBusLineVisibilityAudit();
                RepairPublishedBusStopNodes("post-settlement");
                LogBusStopPublicationAudit();
                RefreshPublicTransportOverviewPanels("post-publish immediate", true);
                StartCoroutine(RefreshPublicTransportOverviewPanelsDeferred());
                BusEconomicsSummary busEconomicsSummary = new BusEconomicsAdvisor().BuildSummary(existingLines);
                scanSummary.BusEconomicsSummary = busEconomicsSummary;
                State.LastBusEconomicsSummary = busEconomicsSummary;
                LogBusEconomicsSummary(busEconomicsSummary);
                LogScanStage("build bus economics summary", ref stageStartedAt);
                List<DepotPlacementRecommendation> depotPlacementRecommendations = BuildDepotPlacementRecommendations(existingLines, cfg, stopLocator, busEconomicsSummary);
                scanSummary.DepotPlacementRecommendationCount = depotPlacementRecommendations.Count;
                State.LastDepotPlacementRecommendations = depotPlacementRecommendations;
                LogDepotPlacementRecommendations(depotPlacementRecommendations, busEconomicsSummary);
                LogScanStage("build depot placement advisory", ref stageStartedAt);
                List<BusLaneUpgradeRecommendation> busLaneRecommendations;
                if (AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled)
                {
                    busLaneRecommendations = BuildBusLaneUpgradeRecommendations(existingLines, cfg);
                }
                else
                {
                    busLaneRecommendations = new List<BusLaneUpgradeRecommendation>();
                    TransitLogging.Log("Bus-lane road upgrades are in development and disabled for this release.");
                }

                scanSummary.BusLaneRecommendationCount = busLaneRecommendations.Count;
                LogScanStage(
                    AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled ? "build bus-lane recommendations" : "skip bus-lane recommendations",
                    ref stageStartedAt);
                State.LastBusLaneRecommendations = busLaneRecommendations;
                AutoPublicTransitUI.UpdateBusLaneSummary(busLaneRecommendations.Count);

                TimeSpan elapsed = DateTime.UtcNow - startedAt;
                scanSummary.DurationSeconds = (float)elapsed.TotalSeconds;
                scanSummary.Completed = true;
                State.LastScanSummary = scanSummary;
                TransitLogging.Log("Transit scan took " + elapsed.TotalSeconds.ToString("0.0") + "s.");
                if (scanSummary.GeneratedUnsafeVehicleModelsDetected > 0 ||
                    scanSummary.GeneratedVehicleModelsRepaired > 0 ||
                    scanSummary.GeneratedLinesWithoutSafeCityBusVehicle > 0)
                {
                    TransitLogging.Log(
                        "Generated bus vehicle model audit: unsafeOrMissingDetected=" + scanSummary.GeneratedUnsafeVehicleModelsDetected +
                        ", repaired=" + scanSummary.GeneratedVehicleModelsRepaired +
                        ", withoutSafeCityBus=" + scanSummary.GeneratedLinesWithoutSafeCityBusVehicle + ".");
                }
                TransitLogging.Log("Transit scan completed.");
                AutoPublicTransitUI.UpdateScanSummary(scanSummary);
                AutoPublicTransitUI.ShowDepotShortageDialog(busEconomicsSummary);
                if (scanSummary.CreatedLineIds != null && scanSummary.CreatedLineIds.Count > 0)
                {
                    State.LastBusSpawnHealthSummary = null;
                    StartCoroutine(CheckGeneratedBusSpawnHealthDeferred(scanSummary));
                }
            }
            catch (Exception e)
            {
                FailScanAfterAsyncError(scanSummary, e);
                finalizeFailed = true;
            }

            if (finalizeFailed)
                yield break;

            _scanRunning = false;
        }

        private System.Collections.IEnumerator SettleGeneratedLineProbes(
            List<GeneratedLineProbe> generatedLineProbes,
            List<ExistingLineSnapshot> existingLines,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransitScanSummary scanSummary,
            string statusText,
            int maxPasses,
            int requireSettledPathsPass)
        {
            if (generatedLineProbes == null || generatedLineProbes.Count == 0)
                yield break;

            AutoPublicTransitUI.UpdateScanStatus("Last scan: " + statusText);

            for (int pass = 1; pass <= maxPasses; pass++)
            {
                if (!_scanRunning)
                {
                    ReleasePendingGeneratedLineProbes(generatedLineProbes, "scan cancelled before " + statusText);
                    yield break;
                }

                for (int frame = 0; frame < 45; frame++)
                {
                    if (!_scanRunning)
                    {
                        ReleasePendingGeneratedLineProbes(generatedLineProbes, "scan cancelled during " + statusText);
                        yield break;
                    }

                    yield return null;
                }

                bool requireSettledPaths = pass >= requireSettledPathsPass;
                bool allowRetry = pass < maxPasses;
                int changed;
                try
                {
                    changed = ReconcileGeneratedLineProbes(generatedLineProbes, existingLines, cfg, stopLocator, pass, requireSettledPaths, !allowRetry, scanSummary);
                }
                catch (Exception e)
                {
                    ReleasePendingGeneratedLineProbes(generatedLineProbes, "probe reconciliation failed during " + statusText);
                    FailScanAfterAsyncError(scanSummary, e);
                    yield break;
                }

                if (changed > 0)
                {
                    TransitLogging.Log("Generated bus-line probe sweep (" + statusText + ") pass " + pass + " reconciled " + changed + " candidate routes after path settling.");
                    AutoPublicTransitUI.UpdateScanSummary(scanSummary);
                }

                if (generatedLineProbes.Count == 0)
                    break;
            }
        }

        private void ReleasePendingGeneratedLineProbes(List<GeneratedLineProbe> generatedLineProbes, string reason)
        {
            if (generatedLineProbes == null || generatedLineProbes.Count == 0)
                return;

            int released = 0;
            for (int i = 0; i < generatedLineProbes.Count; i++)
            {
                GeneratedLineProbe probe = generatedLineProbes[i];
                if (probe == null || probe.ProbeLineId == 0)
                    continue;

                SafeReleaseLine(probe.ProbeLineId);
                released++;
            }

            generatedLineProbes.Clear();
            TransitLogging.Log("Released " + released + " pending generated bus-line probe(s) because " + reason + ".");
        }

        private void LogPublishedCoverageAudit(
            string reason,
            List<DemandNode> nodes,
            List<ExistingLineSnapshot> existingLines,
            List<Vector3> transitHubs,
            AutoPublicTransitConfig cfg)
        {
            int totalNodes = nodes != null ? nodes.Count : 0;
            int coveredNodes = 0;
            int transitHubNodes = 0;
            int coveredTransitHubNodes = 0;
            int touristNodes = 0;
            int coveredTouristNodes = 0;
            int residentialNodes = 0;
            int coveredResidentialNodes = 0;
            int workOrShoppingNodes = 0;
            int coveredWorkOrShoppingNodes = 0;
            var missedStrategicSamples = new List<string>();
            var missedLocalSamples = new List<string>();
            bool[] connectedPublishedLines = BuildPublishedCoverageAuditConnectionMask(existingLines, transitHubs, cfg);

            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    bool covered = IsDemandNodeCoveredByPublishedLines(nodes[i], existingLines, connectedPublishedLines, cfg);
                    bool transitHub = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TransitHub);
                    bool tourist = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TouristAnchor);
                    bool residential = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.Residential);
                    bool workOrShopping = DemandNodePurpose.HasAnyPurpose(nodes[i], DemandNodePurpose.WorkOrShoppingMask);

                    if (covered)
                        coveredNodes++;

                    if (transitHub)
                    {
                        transitHubNodes++;
                        if (covered)
                            coveredTransitHubNodes++;
                    }

                    if (tourist)
                    {
                        touristNodes++;
                        if (covered)
                            coveredTouristNodes++;
                    }

                    if (residential)
                    {
                        residentialNodes++;
                        if (covered)
                            coveredResidentialNodes++;
                    }

                    if (workOrShopping)
                    {
                        workOrShoppingNodes++;
                        if (covered)
                            coveredWorkOrShoppingNodes++;
                    }

                    if (!covered && (transitHub || tourist) && missedStrategicSamples.Count < 8)
                    {
                        missedStrategicSamples.Add(
                            GetStrategicSampleLabel(transitHub, tourist) +
                            "@(" + nodes[i].StopPosition.x.ToString("0", CultureInfo.InvariantCulture) +
                            "," + nodes[i].StopPosition.z.ToString("0", CultureInfo.InvariantCulture) +
                            ") demand=" + nodes[i].Demand);
                    }

                    if (!covered && !transitHub && !tourist && (residential || workOrShopping) && missedLocalSamples.Count < 8)
                    {
                        missedLocalSamples.Add(
                            GetLocalSampleLabel(residential, workOrShopping) +
                            "@(" + nodes[i].StopPosition.x.ToString("0", CultureInfo.InvariantCulture) +
                            "," + nodes[i].StopPosition.z.ToString("0", CultureInfo.InvariantCulture) +
                            ") demand=" + nodes[i].Demand);
                    }
                }
            }

            int lineCount = existingLines != null ? existingLines.Count : 0;
            int hubLinkedLines = 0;
            int busTransferLinkedLines = 0;
            int connectedLines = 0;
            int isolatedLines = 0;
            for (int i = 0; i < lineCount; i++)
            {
                bool hubLinked = IsPublishedLineNearAnyPosition(existingLines[i], transitHubs, GetPublishedCoverageAuditHubTransferDistance(cfg));
                bool busLinked = IsPublishedLineNearAnotherLine(i, existingLines, GetPublishedCoverageAuditBusTransferDistance(cfg));
                if (hubLinked)
                    hubLinkedLines++;
                if (busLinked)
                    busTransferLinkedLines++;

                if (connectedPublishedLines != null && i < connectedPublishedLines.Length && connectedPublishedLines[i])
                    connectedLines++;
                else
                    isolatedLines++;
            }

            TransitLogging.Log(
                "Post-publish mandatory hub coverage audit (" + reason + "): demandNodes=" + totalNodes +
                ", covered=" + coveredNodes +
                ", missed=" + Mathf.Max(0, totalNodes - coveredNodes) +
                ", hubNodes=" + transitHubNodes +
                ", hubCovered=" + coveredTransitHubNodes +
                ", hubMissed=" + Mathf.Max(0, transitHubNodes - coveredTransitHubNodes) +
                ", touristNodes=" + touristNodes +
                ", touristCovered=" + coveredTouristNodes +
                ", touristMissed=" + Mathf.Max(0, touristNodes - coveredTouristNodes) +
                ", residentialNodes=" + residentialNodes +
                ", residentialCovered=" + coveredResidentialNodes +
                ", residentialMissed=" + Mathf.Max(0, residentialNodes - coveredResidentialNodes) +
                ", workOrShoppingNodes=" + workOrShoppingNodes +
                ", workOrShoppingCovered=" + coveredWorkOrShoppingNodes +
                ", workOrShoppingMissed=" + Mathf.Max(0, workOrShoppingNodes - coveredWorkOrShoppingNodes) +
                ", publishedBusLines=" + lineCount +
                ", connectedLines=" + connectedLines +
                ", isolatedLines=" + isolatedLines +
                ", hubLinkedLines=" + hubLinkedLines +
                ", busTransferLinkedLines=" + busTransferLinkedLines + ".");

            if (missedStrategicSamples.Count > 0)
                TransitLogging.Log("Post-publish missed strategic coverage samples (" + reason + "): " + string.Join("; ", missedStrategicSamples.ToArray()) + ".");

            if (missedLocalSamples.Count > 0)
                TransitLogging.Log("Post-publish missed local coverage samples (" + reason + "): " + string.Join("; ", missedLocalSamples.ToArray()) + ".");
        }

        private bool[] BuildPublishedCoverageAuditConnectionMask(List<ExistingLineSnapshot> existingLines, List<Vector3> transitHubs, AutoPublicTransitConfig cfg)
        {
            int lineCount = existingLines != null ? existingLines.Count : 0;
            var connectedLines = new bool[lineCount];
            bool hasTransitHubs = transitHubs != null && transitHubs.Count > 0;
            bool allowSingleIslandLine = lineCount <= 1 && !hasTransitHubs;

            for (int i = 0; i < lineCount; i++)
            {
                connectedLines[i] =
                    allowSingleIslandLine ||
                    IsPublishedLineNearAnyPosition(existingLines[i], transitHubs, GetPublishedCoverageAuditHubTransferDistance(cfg)) ||
                    IsPublishedLineNearAnotherLine(i, existingLines, GetPublishedCoverageAuditBusTransferDistance(cfg));
            }

            return connectedLines;
        }

        private string GetStrategicSampleLabel(bool transitHub, bool tourist)
        {
            if (transitHub && tourist)
                return "hub+tourist";

            if (transitHub)
                return "hub";

            return "tourist";
        }

        private string GetLocalSampleLabel(bool residential, bool workOrShopping)
        {
            if (residential && workOrShopping)
                return "residential+workOrShopping";

            if (residential)
                return "residential";

            return "workOrShopping";
        }

        private bool IsDemandNodeCoveredByPublishedLines(DemandNode node, List<ExistingLineSnapshot> existingLines, bool[] connectedPublishedLines, AutoPublicTransitConfig cfg)
        {
            if (existingLines == null)
                return false;

            float coverageDistance = GetPublishedCoverageAuditDistance(node, cfg);
            for (int i = 0; i < existingLines.Count; i++)
            {
                if (connectedPublishedLines != null && i < connectedPublishedLines.Length && !connectedPublishedLines[i])
                    continue;

                ExistingLineSnapshot line = existingLines[i];
                if (line == null || line.Stops == null)
                    continue;

                if (IsAnyPositionNear(line.Stops, node.StopPosition, coverageDistance))
                    return true;
            }

            return false;
        }

        private float GetPublishedCoverageAuditDistance(DemandNode node, AutoPublicTransitConfig cfg)
        {
            if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TouristAnchor))
                return Mathf.Max(95f, cfg.MaxWalkingDistance * 0.65f);

            if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TransitHub))
                return Mathf.Max(110f, cfg.MaxWalkingDistance * 0.75f);

            return Mathf.Max(130f, cfg.MaxWalkingDistance * 0.9f);
        }

        private bool IsPublishedLineNearAnyPosition(ExistingLineSnapshot line, List<Vector3> positions, float maxDistance)
        {
            if (line == null || line.Stops == null || positions == null)
                return false;

            for (int i = 0; i < positions.Count; i++)
            {
                if (IsAnyPositionNear(line.Stops, positions[i], maxDistance))
                    return true;
            }

            return false;
        }

        private bool IsPublishedLineNearAnotherLine(int lineIndex, List<ExistingLineSnapshot> existingLines, float maxDistance)
        {
            if (existingLines == null || lineIndex < 0 || lineIndex >= existingLines.Count)
                return false;

            ExistingLineSnapshot line = existingLines[lineIndex];
            if (line == null || line.Stops == null)
                return false;

            for (int otherIndex = 0; otherIndex < existingLines.Count; otherIndex++)
            {
                if (otherIndex == lineIndex)
                    continue;

                ExistingLineSnapshot other = existingLines[otherIndex];
                if (other == null || other.Stops == null)
                    continue;

                for (int i = 0; i < other.Stops.Count; i++)
                {
                    if (IsAnyPositionNear(line.Stops, other.Stops[i], maxDistance))
                        return true;
                }
            }

            return false;
        }

        private bool IsAnyPositionNear(List<Vector3> positions, Vector3 target, float maxDistance)
        {
            if (positions == null)
                return false;

            float maxSqr = maxDistance * maxDistance;
            for (int i = 0; i < positions.Count; i++)
            {
                float dx = positions[i].x - target.x;
                float dz = positions[i].z - target.z;
                if (dx * dx + dz * dz <= maxSqr)
                    return true;
            }

            return false;
        }

        private float GetPublishedCoverageAuditHubTransferDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(130f, cfg.MaxWalkingDistance * 0.75f);
        }

        private float GetPublishedCoverageAuditBusTransferDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(70f, Mathf.Min(cfg.MaxWalkingDistance * 0.45f, cfg.GridCellSize * 0.55f));
        }

        private void FailScanAfterAsyncError(TransitScanSummary scanSummary, Exception e)
        {
            TransitLogging.Error("Transit scan failed: " + e);
            if (scanSummary != null)
            {
                scanSummary.FailureMessage = e.Message;
                State.LastScanSummary = scanSummary;
                AutoPublicTransitUI.UpdateScanSummary(scanSummary);
            }

            _scanRunning = false;
        }

        private void LogScanStage(string stageName, ref DateTime stageStartedAt)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan elapsed = now - stageStartedAt;
            TransitLogging.Log("Scan stage - " + stageName + ": " + elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.");
            stageStartedAt = now;
        }

        private void LogBusEconomicsSummary(BusEconomicsSummary summary)
        {
            if (summary == null)
            {
                TransitLogging.Warn("Bus economics summary was not available.");
                return;
            }

            string breakEven = float.IsInfinity(summary.EstimatedBreakEvenLineCount)
                ? "n/a"
                : summary.EstimatedBreakEvenLineCount.ToString("0.0", CultureInfo.InvariantCulture);

            TransitLogging.Log(
                "Bus economics advisory: depots=" + summary.DepotCount +
                ", completeLines=" + summary.CompleteLineCount +
                ", usefulLines=" + summary.UsefulLineCount +
                ", positiveLines=" + summary.PositiveLineCount +
                ", fareScoredLines=" + summary.FareScoredLineCount +
                ", fareIgnoredByPolicy=" + summary.FareRevenueIgnoredLineCount +
                ", avgRiders=" + summary.AveragePassengersPerLine.ToString("0.0", CultureInfo.InvariantCulture) +
                ", vehicles=" + summary.VehicleCount +
                ", avgVehicles=" + summary.AverageVehiclesPerLine.ToString("0.0", CultureInfo.InvariantCulture) +
                ", avgLengthKm=" + summary.AverageRouteLengthKm.ToString("0.0", CultureInfo.InvariantCulture) +
                ", usefulLinesPerDepot=" + summary.UsefulLinesPerDepot.ToString("0.0", CultureInfo.InvariantCulture) +
                ", depotPlanningLines=" + summary.DepotPlanningLineCount +
                ", recommendedDepots=" + summary.RecommendedDepotCount +
                ", depotDelta=" + summary.DepotCountDifference +
                ", depotUsefulLineCapacity=" + summary.EstimatedUsefulLineCapacity +
                ", usefulLinesBeforeDepotPressure=" + summary.AdditionalUsefulLinesBeforeDepotPressure +
                ", breakEvenLines=" + breakEven +
                ", estimatedNetworkContribution=" + summary.EstimatedNetworkContribution.ToString("0", CultureInfo.InvariantCulture) +
                ".");

            if (!string.IsNullOrEmpty(summary.DepotSufficiencyNote))
                TransitLogging.Log("Bus depot sufficiency: " + summary.DepotSufficiencyNote);

            if (!string.IsNullOrEmpty(summary.Recommendation))
                TransitLogging.Log("Bus economics recommendation: " + summary.Recommendation);
        }

        private void LogDemandNodePurposeSummary(List<DemandNode> nodes, TransitScanSummary summary)
        {
            int hubNodes = 0;
            int touristNodes = 0;
            int residentialNodes = 0;
            int workOrShoppingNodes = 0;
            int strategicNodes = 0;
            int dualStrategicNodes = 0;

            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    bool hub = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TransitHub);
                    bool tourist = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TouristAnchor);
                    bool residential = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.Residential);
                    bool workOrShopping = DemandNodePurpose.HasAnyPurpose(nodes[i], DemandNodePurpose.WorkOrShoppingMask);
                    if (hub)
                        hubNodes++;
                    if (tourist)
                        touristNodes++;
                    if (residential)
                        residentialNodes++;
                    if (workOrShopping)
                        workOrShoppingNodes++;
                    if (hub || tourist)
                        strategicNodes++;
                    if (hub && tourist)
                        dualStrategicNodes++;
                }
            }

            if (summary != null)
            {
                summary.DemandTransitHubNodeCount = hubNodes;
                summary.DemandTouristAnchorNodeCount = touristNodes;
                summary.DemandStrategicNodeCount = strategicNodes;
                summary.DemandDualStrategicNodeCount = dualStrategicNodes;
            }

            TransitLogging.Log(
                "Demand node mix: strategic=" + strategicNodes +
                ", transitHub=" + hubNodes +
                ", tourist=" + touristNodes +
                ", residential=" + residentialNodes +
                ", workOrShopping=" + workOrShoppingNodes +
                ", both=" + dualStrategicNodes + ".");
        }

        private void LogGeneratedRoutePurposeSummary(List<List<Vector3>> routes, List<DemandNode> nodes, TransitScanSummary summary)
        {
            int strategicRoutes = 0;
            int hubRoutes = 0;
            int touristRoutes = 0;
            int residentialRoutes = 0;
            int workOrShoppingRoutes = 0;
            int totalRoutes = routes != null ? routes.Count : 0;

            if (routes != null && nodes != null)
            {
                for (int i = 0; i < routes.Count; i++)
                {
                    bool routeHasHub = false;
                    bool routeHasTourist = false;
                    bool routeHasResidential = false;
                    bool routeHasWorkOrShopping = false;
                    List<Vector3> route = routes[i];

                    for (int j = 0; j < route.Count; j++)
                    {
                        int nodeIndex = FindNearestDemandNodeIndex(route[j], nodes, 18f);
                        if (nodeIndex < 0)
                            continue;

                        DemandNode node = nodes[nodeIndex];
                        if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TransitHub))
                            routeHasHub = true;
                        if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.TouristAnchor))
                            routeHasTourist = true;
                        if (DemandNodePurpose.HasPurpose(node, DemandNodePurpose.Residential))
                            routeHasResidential = true;
                        if (DemandNodePurpose.HasAnyPurpose(node, DemandNodePurpose.WorkOrShoppingMask))
                            routeHasWorkOrShopping = true;
                    }

                    if (routeHasHub)
                        hubRoutes++;
                    if (routeHasTourist)
                        touristRoutes++;
                    if (routeHasResidential)
                        residentialRoutes++;
                    if (routeHasWorkOrShopping)
                        workOrShoppingRoutes++;
                    if (routeHasHub || routeHasTourist)
                        strategicRoutes++;
                }
            }

            if (summary != null)
            {
                summary.StrategicRouteCandidateCount = strategicRoutes;
                summary.TransitHubRouteCandidateCount = hubRoutes;
                summary.TouristAnchorRouteCandidateCount = touristRoutes;
            }

            TransitLogging.Log(
                "Generated route mix: strategic=" + strategicRoutes +
                ", transitHub=" + hubRoutes +
                ", tourist=" + touristRoutes +
                ", residential=" + residentialRoutes +
                ", workOrShopping=" + workOrShoppingRoutes +
                ", normalOnly=" + Mathf.Max(0, totalRoutes - strategicRoutes) +
                " of " + totalRoutes + " candidates.");
        }

        private int FindNearestDemandNodeIndex(Vector3 stop, List<DemandNode> nodes, float maxDistance)
        {
            int bestIndex = -1;
            float bestSqr = maxDistance * maxDistance;

            if (nodes == null)
                return bestIndex;

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

        private int ScoreBuilding(ushort id, ref Building b)
        {
            ItemClass.Service svc = b.Info != null && b.Info.m_class != null
                ? b.Info.m_class.m_service
                : ItemClass.Service.None;

            CitizenManager cm = CitizenManager.instance;
            int count = 0;

            uint unit = b.m_citizenUnits;
            int safety = 0;

            while (unit != 0 && safety < 20)
            {
                ref CitizenUnit cu = ref cm.m_units.m_buffer[unit];
                for (int i = 0; i < 5; i++)
                {
                    uint citizen = cu.GetCitizen(i);
                    if (citizen != 0)
                        count++;
                }

                unit = cu.m_nextUnit;
                safety++;
            }

            switch (svc)
            {
                case ItemClass.Service.Residential:
                    return Mathf.Clamp(count / 2, 0, 100);
                case ItemClass.Service.Commercial:
                case ItemClass.Service.Office:
                case ItemClass.Service.Industrial:
                    return Mathf.Clamp(count / 3, 0, 100);
                default:
                    return 0;
            }
        }
    }
}
