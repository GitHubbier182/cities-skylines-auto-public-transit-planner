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
    public static class State
    {
        public static bool HasScanRun;
        public static Dictionary<ushort, int> LastDemandMap;
        public static List<List<Vector3>> LastRoutes;
        public static TransitScanSummary LastScanSummary;
        public static BusEconomicsSummary LastBusEconomicsSummary;
        public static BusSpawnHealthSummary LastBusSpawnHealthSummary;
        public static MetroScanSummary LastMetroScanSummary;
        public static MetroScanSummary LastTrainScanSummary;
        public static List<BusLaneUpgradeRecommendation> LastBusLaneRecommendations = new List<BusLaneUpgradeRecommendation>();
        public static List<DepotPlacementRecommendation> LastDepotPlacementRecommendations = new List<DepotPlacementRecommendation>();
        public static Dictionary<ushort, CachedStopMatch> StopCache = new Dictionary<ushort, CachedStopMatch>();
        public static void ClearTransient()
        {
            HasScanRun = false;
            LastDemandMap = null;
            LastRoutes = null;
            LastScanSummary = null;
            LastBusEconomicsSummary = null;
            LastBusSpawnHealthSummary = null;
            LastMetroScanSummary = null;
            LastTrainScanSummary = null;
            LastBusLaneRecommendations = new List<BusLaneUpgradeRecommendation>();
            LastDepotPlacementRecommendations = new List<DepotPlacementRecommendation>();
            StopCache.Clear();
        }

        public static bool HasBusDepotIssue()
        {
            return HasBusDepotIssue(LastBusSpawnHealthSummary);
        }

        public static bool HasBusDepotIssue(BusSpawnHealthSummary health)
        {
            return health != null && health.DepotDispatchPressureLikely;
        }
    }

    public class MetroScanSummary
    {
        public bool Completed;
        public string FailureMessage;
        public float DurationSeconds;
        public int StationsFound;
        public int StationsWithoutTrack;
        public int CargoStationsSkipped;
        public int TrackComponents;
        public int ConnectedStationGroups;
        public int ExistingLines;
        public int ExistingLineGroupsSkipped;
        public int JunctionGroupsPlanned;
        public int PlannedCorridors;
        public int SingleStationGroupsSkipped;
        public int CandidateLines;
        public int CreatedLines;
        public int FailedLines;
        public List<ushort> CreatedLineIds = new List<ushort>();
    }

    public class DepotPlacementRecommendation
    {
        public int Rank;
        public Vector3 Position;
        public string RoadName;
        public int NearbyLineCount;
        public int NearbyStopCount;
        public float AverageLineDistance;
        public float DistanceToNearestDepot;
        public string Reason;
    }

    public class BusLaneUpgradeRecommendation
    {
        public ushort SegmentId;
        public string CurrentRoadName;
        public string RecommendedRoadName;
        public int Score;
        public int BusLineCount;
        public int TrafficDensity;
        public float Length;
        public Vector3 Position;
        public string Reason;
    }

    public class BusEconomicsSummary
    {
        public int DepotCount;
        public int CompleteLineCount;
        public int UsefulLineCount;
        public int PositiveLineCount;
        public int FareScoredLineCount;
        public int FareRevenueIgnoredLineCount;
        public int VehicleCount;
        public float AveragePassengersPerLine;
        public float AverageVehiclesPerLine;
        public float AverageRouteLengthKm;
        public float UsefulLinesPerDepot;
        public int DepotPlanningLineCount;
        public int RecommendedDepotCount;
        public int DepotCountDifference;
        public float AveragePositiveLineContribution;
        public float EstimatedBreakEvenLineCount;
        public float EstimatedNetworkContribution;
        public int EstimatedUsefulLineCapacity;
        public int AdditionalUsefulLinesBeforeDepotPressure;
        public bool DepotCapacityLooksSufficient;
        public string DepotSufficiencyNote;
        public string Recommendation;
    }

    public class BusSpawnHealthSummary
    {
        public int CreatedLineCount;
        public int CheckedLineCount;
        public int CompleteLineCount;
        public int TargetVehicleCount;
        public int AssignedVehicleCount;
        public int ActiveVehicleCount;
        public int WaitingPathVehicleCount;
        public int ReturningToDepotVehicleCount;
        public int VehicleShortfallCount;
        public float AssignedVehicleRatio;
        public int LinesWithTargetVehicles;
        public int LinesWithoutVehicles;
        public int LinesBelowTarget;
        public int LinesOnlyWaitingPathVehicles;
        public int UnsafeVehicleModelLineCount;
        public int VehicleModelRepairCount;
        public int LinesWithoutSafeCityBusVehicle;
        public string VehicleModelIssueNames;
        public int DepotCount;
        public int DepotProblemCount;
        public int DepotDispatchPressureVehicleCount;
        public int DepotDispatchPressureThreshold;
        public int WorstDepotNearbyBusCount;
        public int DepotDispatchPressureConsecutiveJamScans;
        public int DepotDispatchPressureConsecutiveClearScans;
        public Vector3 WorstDepotPosition;
        public bool DepotDispatchPressureLikely;
        public bool ActiveNetworkMonitor;
        public bool TransitVehicleSpawnDelayActive;
        public bool TransitVehicleSpawnDelaySettingKnown;
        public uint TransitVehicleSpawnDelayBusDelay;
        public bool NeedsPlayerAttention;
        public string Recommendation;
    }

    public class TransitScanSummary
    {
        public bool Completed;
        public string FailureMessage;
        public float DurationSeconds;
        public int EligibleBuildings;
        public int ScannedBuildings;
        public int AcceptedDemandBuildings;
        public int ZeroWeightBuildings;
        public int AlreadyCoveredBuildings;
        public int NoStopBuildings;
        public int StopCacheHits;
        public int StopCacheMisses;
        public int StopCacheInvalidations;
        public int StopCacheSize;
        public int RejectedUndergroundStopSegments;
        public int RejectedCollapsedStopSegments;
        public int RejectedMissingNodeStopSegments;
        public int ValidStopCandidates;
        public int TransitHubCount;
        public int TouristAnchorCount;
        public int DemandNodeCount;
        public int DemandTransitHubNodeCount;
        public int DemandTouristAnchorNodeCount;
        public int DemandStrategicNodeCount;
        public int DemandDualStrategicNodeCount;
        public int BuiltRouteCount;
        public int StrategicRouteCandidateCount;
        public int TransitHubRouteCandidateCount;
        public int TouristAnchorRouteCandidateCount;
        public int ExistingLinesRetained;
        public int LinesRemoved;
        public int VeryWeakLinesRemoved;
        public int InvalidAnchorLines;
        public int BrokenPathLines;
        public int ComplexShapeLines;
        public int MaintenanceDeferredLineReleases;
        public int MaintenanceRetainedIssueLines;
        public int PrunedStops;
        public int WeakDuplicateLinesRetired;
        public int RidershipProtectedLines;
        public int StrategicProtectedLines;
        public int WeakOversuppliedLines;
        public int RidershipProtectedStops;
        public int CreatedLines;
        public int RepairedGeneratedLines;
        public int GeneratedRoutesSkipped;
        public int GeneratedRoutesTooShort;
        public int GeneratedRoutesDuplicate;
        public int GeneratedRoutesCircuitous;
        public int GeneratedRoutesIntegrityFailed;
        public int GeneratedStopsSkipped;
        public int ClosureBackoffs;
        public int GeneratedVehicleModelsRepaired;
        public int GeneratedUnsafeVehicleModelsDetected;
        public int GeneratedLinesWithoutSafeCityBusVehicle;
        public List<ushort> CreatedLineIds = new List<ushort>();
        public int BusLaneRecommendationCount;
        public int DepotPlacementRecommendationCount;
        public BusEconomicsSummary BusEconomicsSummary;
    }

    public struct CachedStopMatch
    {
        public Vector3 BuildingPosition;
        public Vector3 StopPosition;
        public ushort SegmentId;
        public string RoadName;
    }
}
