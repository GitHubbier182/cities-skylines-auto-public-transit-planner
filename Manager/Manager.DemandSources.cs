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
        private bool IsDemandService(ref Building b)
        {
            if (b.Info == null || b.Info.m_class == null)
                return false;

            ItemClass.Service svc = b.Info.m_class.m_service;
            return svc == ItemClass.Service.Residential
                || svc == ItemClass.Service.Commercial
                || svc == ItemClass.Service.Office
                || svc == ItemClass.Service.Industrial;
        }

        private bool TryGetNearestBusStopPositionCached(
            ushort buildingId,
            Vector3 buildingPos,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator,
            out CachedStopMatch stopMatch,
            out bool usedCache)
        {
            usedCache = false;
            stopMatch = new CachedStopMatch();
            CachedStopMatch cached;
            if (State.StopCache.TryGetValue(buildingId, out cached))
            {
                if (Geometry.DistanceXZ(cached.BuildingPosition, buildingPos) <= 8f
                    && stopLocator.IsCachedBusStopStillValid(cached))
                {
                    stopMatch = cached;
                    usedCache = true;
                    return true;
                }

                State.StopCache.Remove(buildingId);
            }

            CachedStopMatch match;
            if (!stopLocator.TryGetNearestBusStopMatch(buildingPos, cfg.MaxWalkingDistance, out match))
            {
                return false;
            }

            stopMatch = match;

            State.StopCache[buildingId] = match;

            return true;
        }

        private int GetQuickScanStride(AutoPublicTransitConfig cfg)
        {
            if (!cfg.QuickScanMode)
                return 1;

            BuildingManager bm = BuildingManager.instance;
            var buffer = bm.m_buildings.m_buffer;
            int eligibleBuildings = 0;

            for (ushort id = 1; id < buffer.Length; id++)
            {
                ref Building b = ref buffer[id];
                if (b.m_flags == 0 || !IsDemandService(ref b))
                    continue;

                eligibleBuildings++;
            }

            return Mathf.Max(1, Mathf.CeilToInt(eligibleBuildings / 2600f));
        }

        private List<Vector3> FlattenStops(List<ExistingLineSnapshot> existingLines)
        {
            var stops = new List<Vector3>();
            if (existingLines == null)
                return stops;

            for (int i = 0; i < existingLines.Count; i++)
            {
                stops.AddRange(existingLines[i].Stops);
            }

            return stops;
        }

        private List<ushort> FindWeakDuplicateLines(List<ExistingLineSnapshot> existingLines, AutoPublicTransitConfig cfg)
        {
            var weakLineIds = new List<ushort>();
            TransportManager tm = TransportManager.instance;

            for (int i = 0; i < existingLines.Count; i++)
            {
                ExistingLineSnapshot candidate = existingLines[i];
                ref TransportLine line = ref tm.m_lines.m_buffer[candidate.LineId];
                int riders = GetAverageRidership(ref line);
                if (riders > 12)
                    continue;

                bool duplicated = false;
                for (int j = 0; j < existingLines.Count; j++)
                {
                    if (i == j)
                        continue;

                    if (GetStopOverlapRatio(candidate.Stops, existingLines[j].Stops, cfg) >= 0.75f)
                    {
                        duplicated = true;
                        break;
                    }
                }

                if (duplicated)
                    weakLineIds.Add(candidate.LineId);
            }

            return weakLineIds;
        }

        private int GetAverageRidership(ref TransportLine line)
        {
            return (int)(line.m_passengers.m_residentPassengers.m_averageCount
                + line.m_passengers.m_touristPassengers.m_averageCount);
        }

        private float GetStopOverlapRatio(List<Vector3> aStops, List<Vector3> bStops, AutoPublicTransitConfig cfg)
        {
            if (aStops == null || bStops == null || aStops.Count == 0 || bStops.Count == 0)
                return 0f;

            float overlapDistance = Mathf.Max(50f, cfg.MaxWalkingDistance * 0.45f);
            int overlappingStops = 0;

            for (int i = 0; i < aStops.Count; i++)
            {
                if (IsNearAnyStop(aStops[i], bStops, overlapDistance))
                    overlappingStops++;
            }

            return (float)overlappingStops / aStops.Count;
        }

        private List<Vector3> CollectTransitHubs(AutoPublicTransitConfig cfg)
        {
            var hubs = new List<Vector3>();
            if (!cfg.LinkToOtherTransit)
                return hubs;

            BuildingManager bm = BuildingManager.instance;
            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null || building.Info.m_class == null)
                    continue;

                if (building.Info.m_class.m_service != ItemClass.Service.PublicTransport)
                    continue;

                if (building.Info.m_buildingAI is DepotAI)
                    continue;

                if (building.Info.m_class.m_subService == ItemClass.SubService.PublicTransportBus)
                    continue;

                hubs.Add(building.m_position);
            }

            return hubs;
        }

        private void InjectTransitHubDemand(
            DemandGrid demandGrid,
            List<Vector3> hubs,
            List<Vector3> existingStops,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (hubs == null || hubs.Count == 0)
                return;

            for (int i = 0; i < hubs.Count; i++)
            {
                Vector3 hubPos = hubs[i];
                if (ApplyExistingCoverageDiscount(12, hubPos, existingStops, cfg) <= 0)
                    continue;

                CachedStopMatch stopMatch;
                if (!stopLocator.TryGetNearestBusStopMatch(hubPos, cfg.MaxRoadDistance, out stopMatch))
                    continue;

                demandGrid.AddSample(hubPos, stopMatch, Mathf.Max(cfg.DemandThreshold + 10, 22), DemandNodePurpose.TransitHub);
            }
        }

        private List<Vector3> CollectTouristAnchors()
        {
            var anchors = new List<Vector3>();
            BuildingManager bm = BuildingManager.instance;

            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null)
                    continue;

                BuildingAI ai = building.Info.m_buildingAI;
                if (ai is MonumentAI
                    || ai is ParkBuildingAI
                    || ai is TourBuildingAI
                    || ai is HotelAI
                    || ai is MainCampusBuildingAI
                    || ai is CampusBuildingAI
                    || IsLeisureCommercial(ref building))
                {
                    anchors.Add(building.m_position);
                }
            }

            return anchors;
        }

        private bool IsLeisureCommercial(ref Building building)
        {
            if (building.Info == null || building.Info.m_class == null)
                return false;

            ItemClass.Service service = building.Info.m_class.m_service;
            ItemClass.SubService subService = building.Info.m_class.m_subService;
            return service == ItemClass.Service.Commercial
                && subService == ItemClass.SubService.CommercialLeisure;
        }

        private void InjectTouristDemand(
            DemandGrid demandGrid,
            List<Vector3> anchors,
            List<Vector3> existingStops,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (anchors == null || anchors.Count == 0)
                return;

            for (int i = 0; i < anchors.Count; i++)
            {
                Vector3 anchor = anchors[i];
                if (ApplyExistingCoverageDiscount(16, anchor, existingStops, cfg) <= 0)
                    continue;

                CachedStopMatch stopMatch;
                if (!stopLocator.TryGetNearestBusStopMatch(anchor, cfg.MaxRoadDistance, out stopMatch))
                    continue;

                demandGrid.AddSample(anchor, stopMatch, Mathf.Max(cfg.DemandThreshold + 8, 20), DemandNodePurpose.TouristAnchor);
            }
        }

        private List<Vector3> GetLineStops(List<Vector3> route)
        {
            var stops = new List<Vector3>();
            if (route == null)
                return stops;

            for (int i = 0; i < route.Count; i++)
            {
                Vector3 candidate = route[i];
                if (stops.Count > 0 && Geometry.DistanceXZ(stops[stops.Count - 1], candidate) < 1f)
                    continue;

                stops.Add(candidate);
            }

            if (stops.Count > 1 && Geometry.DistanceXZ(stops[0], stops[stops.Count - 1]) < 1f)
                stops.RemoveAt(stops.Count - 1);

            return stops;
        }
    }
}
