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
    [Serializable]
    public class AutoPublicTransitConfig
    {
        public static readonly bool BusLaneRoadUpgradesPlayerEnabled = false;

        public const int DefaultMinStopsPerRoute = 4;
        public const int DefaultMaxStopsPerRoute = 12;
        public const float DefaultMaxWalkingDistance = 250f;
        public const float DefaultMaxRoadDistance = 600f;
        public const float DefaultMaxLineLengthKm = 12f;
        public const int DefaultDemandThreshold = 5;
        public const float DefaultGridCellSize = 200f;
        public const bool DefaultLinkToOtherTransit = true;
        public const bool DefaultQuickScanMode = true;

        public int MinStopsPerRoute = DefaultMinStopsPerRoute;
        public int MaxStopsPerRoute = DefaultMaxStopsPerRoute;
        public float MaxWalkingDistance = DefaultMaxWalkingDistance;
        public float MaxRoadDistance = DefaultMaxRoadDistance;
        public float MaxLineLengthKm = DefaultMaxLineLengthKm;
        public int DemandThreshold = DefaultDemandThreshold;
        public float GridCellSize = DefaultGridCellSize;
        public bool AvoidHighways = true;
        public bool LinkToOtherTransit = DefaultLinkToOtherTransit;
        public bool QuickScanMode = DefaultQuickScanMode;
        public bool EnableBusLaneRecommendations = false;
        public int BusLaneRouteThreshold = 2;
        public int BusLaneTrafficDensityThreshold = 75;
        public int MaxBusLaneRecommendations = 12;
        public bool ShowLauncherButton = true;
        public float LauncherButtonX = 96f;
        public float LauncherButtonY = 96f;
        public bool EnableDebugLogging = false;
        public bool ShowDebugOverlay = false;

        public void ResetActiveBusPlanningSettings()
        {
            MaxWalkingDistance = DefaultMaxWalkingDistance;
            MaxRoadDistance = DefaultMaxRoadDistance;
            MaxLineLengthKm = DefaultMaxLineLengthKm;
            DemandThreshold = DefaultDemandThreshold;
            GridCellSize = DefaultGridCellSize;
            MinStopsPerRoute = DefaultMinStopsPerRoute;
            MaxStopsPerRoute = DefaultMaxStopsPerRoute;
            LinkToOtherTransit = DefaultLinkToOtherTransit;
            QuickScanMode = DefaultQuickScanMode;
        }

        public bool HasRiskyBusPlanningSettings(out string reason)
        {
            var reasons = new List<string>();
            int riskScore = 0;

            if (MinStopsPerRoute > MaxStopsPerRoute)
            {
                reasons.Add("minimum stops is greater than maximum stops");
                riskScore += 4;
            }

            if (!QuickScanMode)
            {
                reasons.Add("full scan mode");
                riskScore += 2;
            }

            if (DemandThreshold <= 2)
            {
                reasons.Add("very low demand threshold");
                riskScore += 2;
            }
            else if (DemandThreshold <= 3)
            {
                reasons.Add("low demand threshold");
                riskScore += 1;
            }

            if (GridCellSize < 100f)
            {
                reasons.Add("very small demand grid");
                riskScore += 2;
            }
            else if (GridCellSize < 150f)
            {
                reasons.Add("small demand grid");
                riskScore += 1;
            }

            if (MaxStopsPerRoute > 20)
            {
                reasons.Add("very high stops per route");
                riskScore += 2;
            }
            else if (MaxStopsPerRoute > 16)
            {
                reasons.Add("high stops per route");
                riskScore += 1;
            }

            if (MaxRoadDistance > 1000f)
            {
                reasons.Add("large road-link distance");
                riskScore += 1;
            }

            if (MaxWalkingDistance > 400f)
            {
                reasons.Add("large walking distance");
                riskScore += 1;
            }

            if (MaxLineLengthKm > 20f)
            {
                reasons.Add("long generated-line cap");
                riskScore += 1;
            }

            if (riskScore < 3)
            {
                reason = null;
                return false;
            }

            reason = string.Join(", ", reasons.ToArray());
            return true;
        }
    }
}
