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
        }

        public void RunScan()
        {
            if (_scanRunning)
            {
                TransitLogging.Warn("Transit demand scan is already running.");
                AutoPublicTransitUI.UpdateScanStatus("Last scan: already running");
                return;
            }

            _scanRunning = true;
            AutoPublicTransitUI.UpdateScanStatus("Last scan: running");
            TransitLogging.Log("Starting transit demand scan...");
            var scanSummary = new TransitScanSummary();
            State.LastScanSummary = scanSummary;

            try
            {
                var cfg = ConfigManager.Config;
                scanSummary.QuickScanMode = cfg.QuickScanMode;
                DateTime startedAt = DateTime.UtcNow;
                DateTime stageStartedAt = startedAt;
                var transitHubs = CollectTransitHubs(cfg);
                scanSummary.TransitHubCount = transitHubs.Count;
                LogScanStage("collect transit hubs", ref stageStartedAt);
                var touristAnchors = CollectTouristAnchors();
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
                var existingLines = MaintainExistingBusNetwork(cfg, transitHubs, touristAnchors, stopLocator, scanSummary);
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
                int quickSkippedBuildings = 0;
                int stopSearchFailures = 0;
                int zeroWeightBuildings = 0;
                int coverageDiscountedBuildings = 0;
                int quickScanStride = GetQuickScanStride(cfg);
                scanSummary.QuickScanStride = quickScanStride;
                LogScanStage("compute demand sampling stride", ref stageStartedAt);
                List<Vector3> existingStops = FlattenStops(existingLines);

                BuildingManager bm = BuildingManager.instance;
                var buffer = bm.m_buildings.m_buffer;

                for (ushort id = 1; id < buffer.Length; id++)
                {
                    ref Building b = ref buffer[id];
                    if (b.m_flags == 0)
                        continue;

                    if (!IsDemandService(ref b))
                        continue;

                    eligibleBuildings++;

                    if (quickScanStride > 1 && (id % quickScanStride) != 0)
                    {
                        quickSkippedBuildings++;
                        continue;
                    }

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

                    demandGrid.AddSample(pos, stopMatch, weight);
                    demandMap[id] = weight;
                    candidateStops++;
                    sampledBuildings++;
                }
                LogScanStage("sample demand buildings", ref stageStartedAt);

                InjectTransitHubDemand(demandGrid, transitHubs, existingStops, cfg, stopLocator);
                LogScanStage("inject transit hub demand", ref stageStartedAt);
                InjectTouristDemand(demandGrid, touristAnchors, existingStops, cfg, stopLocator);
                LogScanStage("inject tourist demand", ref stageStartedAt);

                TransitLogging.Verbose("Demand-scored buildings: " + demandMap.Count);
                TransitLogging.Log("Valid stop candidates: " + candidateStops);
                if (cfg.QuickScanMode)
                    TransitLogging.Log("Sampled demand pass considered " + scannedBuildings + " of " + eligibleBuildings + " eligible buildings at stride " + quickScanStride + "; skipped " + quickSkippedBuildings + " by stride.");
                else
                    TransitLogging.Log("Full demand pass considered " + scannedBuildings + " eligible buildings.");
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
                scanSummary.QuickSkippedBuildings = quickSkippedBuildings;
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
                    ", roadGraphRejected=" + _routeBuilder.RoadGraphRejectedLinkCount + ".");
                LogGeneratedRoutePurposeSummary(routes, nodes, scanSummary);

                State.LastDemandMap = demandMap;
                State.LastRoutes = routes;
                State.HasScanRun = true;

                EnsureBusDepotCoverage(nodes);
                LogScanStage("ensure depot coverage", ref stageStartedAt);
                List<GeneratedLineProbe> generatedLineProbes = BuildGeneratedLineProbes(routes, existingLines, cfg, stopLocator, scanSummary);
                LogScanStage("apply generated bus lines", ref stageStartedAt);
                StartCoroutine(FinalizeScanAfterGeneratedLineSettlement(generatedLineProbes, existingLines, cfg, stopLocator, scanSummary, startedAt, stageStartedAt));
            }
            catch (Exception e)
            {
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
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            TransitScanSummary scanSummary,
            DateTime startedAt,
            DateTime stageStartedAt)
        {
            if (generatedLineProbes != null && generatedLineProbes.Count > 0)
            {
                AutoPublicTransitUI.UpdateScanStatus("Last scan: settling generated paths");

                for (int pass = 1; pass <= 5; pass++)
                {
                    for (int frame = 0; frame < 45; frame++)
                        yield return null;

                    bool requireSettledPaths = pass >= 3;
                    bool allowRetry = pass < 5;
                    int changed;
                    bool failed = false;
                    try
                    {
                        changed = ReconcileGeneratedLineProbes(generatedLineProbes, existingLines, cfg, stopLocator, pass, requireSettledPaths, !allowRetry, scanSummary);
                    }
                    catch (Exception e)
                    {
                        FailScanAfterAsyncError(scanSummary, e);
                        changed = 0;
                        failed = true;
                    }

                    if (failed)
                        yield break;

                    if (changed > 0)
                    {
                        TransitLogging.Log("Generated bus-line probe sweep pass " + pass + " reconciled " + changed + " candidate routes after path settling.");
                        AutoPublicTransitUI.UpdateScanSummary(scanSummary);
                    }

                    if (generatedLineProbes.Count == 0)
                        break;
                }
            }

            bool finalizeFailed = false;
            try
            {
                LogScanStage("settle generated bus lines", ref stageStartedAt);
                LogBusLineVisibilityAudit();
                RepairPublishedBusStopNodes("post-settlement");
                LogBusStopPublicationAudit();
                RefreshPublicTransportOverviewPanels("post-publish immediate");
                StartCoroutine(RefreshPublicTransportOverviewPanelsDeferred());
                BusEconomicsSummary busEconomicsSummary = new BusEconomicsAdvisor().BuildSummary(existingLines);
                scanSummary.BusEconomicsSummary = busEconomicsSummary;
                State.LastBusEconomicsSummary = busEconomicsSummary;
                LogBusEconomicsSummary(busEconomicsSummary);
                LogScanStage("build bus economics summary", ref stageStartedAt);
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
                TransitLogging.Log("Transit scan completed.");
                AutoPublicTransitUI.UpdateScanSummary(scanSummary);
                AutoPublicTransitUI.ShowTransitVehicleSpawnDelayDialogIfNeeded(scanSummary);
                AutoPublicTransitUI.ShowDepotShortageDialog(busEconomicsSummary);
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
            int strategicNodes = 0;
            int dualStrategicNodes = 0;

            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    bool hub = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TransitHub);
                    bool tourist = DemandNodePurpose.HasPurpose(nodes[i], DemandNodePurpose.TouristAnchor);
                    if (hub)
                        hubNodes++;
                    if (tourist)
                        touristNodes++;
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
                ", both=" + dualStrategicNodes + ".");
        }

        private void LogGeneratedRoutePurposeSummary(List<List<Vector3>> routes, List<DemandNode> nodes, TransitScanSummary summary)
        {
            int strategicRoutes = 0;
            int hubRoutes = 0;
            int touristRoutes = 0;
            int totalRoutes = routes != null ? routes.Count : 0;

            if (routes != null && nodes != null)
            {
                for (int i = 0; i < routes.Count; i++)
                {
                    bool routeHasHub = false;
                    bool routeHasTourist = false;
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
                    }

                    if (routeHasHub)
                        hubRoutes++;
                    if (routeHasTourist)
                        touristRoutes++;
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
