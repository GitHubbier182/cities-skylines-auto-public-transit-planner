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

        private int GetDemandNodePurpose(ref Building b)
        {
            if (b.Info == null || b.Info.m_class == null)
                return DemandNodePurpose.Normal;

            switch (b.Info.m_class.m_service)
            {
                case ItemClass.Service.Residential:
                    return DemandNodePurpose.Residential;
                case ItemClass.Service.Commercial:
                    return DemandNodePurpose.Commercial;
                case ItemClass.Service.Office:
                    return DemandNodePurpose.Office;
                case ItemClass.Service.Industrial:
                    return DemandNodePurpose.Industrial;
                default:
                    return DemandNodePurpose.Normal;
            }
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

        private List<Vector3> FlattenStops(List<ExistingLineSnapshot> existingLines)
        {
            var stops = new List<Vector3>();
            if (existingLines == null)
                return stops;

            for (int i = 0; i < existingLines.Count; i++)
            {
                if (existingLines[i] != null && existingLines[i].Stops != null)
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
                if (!IsManagedExistingLine(candidate))
                    continue;

                ref TransportLine line = ref tm.m_lines.m_buffer[candidate.LineId];
                int riders = GetAverageRidership(ref line);
                if (riders > 12)
                    continue;

                bool duplicated = false;
                for (int j = 0; j < existingLines.Count; j++)
                {
                    if (i == j)
                        continue;

                    if (!IsManagedExistingLine(existingLines[j]))
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
            int publicTransportBuildings = 0;
            int cargoBuildings = 0;
            int buildingAccessPoints = 0;
            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null || building.Info.m_class == null)
                    continue;

                if (building.Info.m_buildingAI is DepotAI)
                    continue;

                bool publicTransportHub = IsNonBusPublicTransportHub(ref building);
                bool cargoHub = IsCargoTransportHub(ref building);
                if (!publicTransportHub && !cargoHub)
                    continue;

                bool addedHub = false;
                Vector3 accessPosition = GetBuildingAccessPosition(ref building);
                if (AddUniqueHubPosition(hubs, accessPosition, 35f))
                {
                    addedHub = true;
                    if (Geometry.DistanceXZ(accessPosition, building.m_position) > 8f)
                        buildingAccessPoints++;
                }

                if (Geometry.DistanceXZ(accessPosition, building.m_position) > 90f &&
                    AddUniqueHubPosition(hubs, building.m_position, 90f))
                {
                    addedHub = true;
                }

                if (addedHub)
                {
                    if (publicTransportHub)
                        publicTransportBuildings++;
                    else
                        cargoBuildings++;
                }
            }

            int lineStops = AddNonBusTransportLineStops(hubs);
            TransitLogging.Log(
                "Transit hub/station scan: publicTransportBuildings=" + publicTransportBuildings +
                ", cargoBuildings=" + cargoBuildings +
                ", buildingAccessPoints=" + buildingAccessPoints +
                ", nonBusLineStops=" + lineStops +
                ", total=" + hubs.Count + ".");
            return hubs;
        }

        private bool IsNonBusPublicTransportHub(ref Building building)
        {
            ItemClass itemClass = building.Info != null ? building.Info.m_class : null;
            if (itemClass == null || itemClass.m_service != ItemClass.Service.PublicTransport)
                return false;

            if (itemClass.m_subService == ItemClass.SubService.PublicTransportBus)
                return false;

            return true;
        }

        private bool IsCargoTransportHub(ref Building building)
        {
            if (building.Info == null)
                return false;

            string aiName = building.Info.m_buildingAI != null ? building.Info.m_buildingAI.GetType().Name : string.Empty;
            string prefabName = building.Info.name ?? string.Empty;

            bool cargoNamed = ContainsIgnoreCase(aiName, "Cargo") || ContainsIgnoreCase(prefabName, "Cargo");
            if (!cargoNamed)
                return false;

            return ContainsIgnoreCase(aiName, "Station")
                || ContainsIgnoreCase(prefabName, "Station")
                || ContainsIgnoreCase(aiName, "Harbor")
                || ContainsIgnoreCase(prefabName, "Harbor")
                || ContainsIgnoreCase(aiName, "Airport")
                || ContainsIgnoreCase(prefabName, "Airport")
                || ContainsIgnoreCase(aiName, "Terminal")
                || ContainsIgnoreCase(prefabName, "Terminal");
        }

        private bool ContainsIgnoreCase(string value, string pattern)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private int AddNonBusTransportLineStops(List<Vector3> hubs)
        {
            if (hubs == null)
                return 0;

            TransportManager tm = TransportManager.instance;
            if (tm == null)
                return 0;

            int added = 0;
            for (ushort lineId = 1; lineId < tm.m_lines.m_size; lineId++)
            {
                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                if ((line.m_flags & (TransportLine.Flags.Temporary | TransportLine.Flags.Hidden)) != 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType == TransportInfo.TransportType.Bus)
                    continue;

                List<Vector3> stops = GetExistingLineStops(ref line);
                for (int i = 0; i < stops.Count; i++)
                {
                    if (AddUniqueHubPosition(hubs, stops[i], 35f))
                        added++;
                }
            }

            return added;
        }

        private bool AddUniqueHubPosition(List<Vector3> hubs, Vector3 position, float mergeDistance)
        {
            if (hubs == null)
                return false;

            float mergeSqr = mergeDistance * mergeDistance;
            for (int i = 0; i < hubs.Count; i++)
            {
                float dx = hubs[i].x - position.x;
                float dz = hubs[i].z - position.z;
                if (dx * dx + dz * dz <= mergeSqr)
                    return false;
            }

            hubs.Add(position);
            return true;
        }

        private Vector3 GetBuildingAccessPosition(ref Building building)
        {
            NetManager nm = NetManager.instance;
            ushort segmentId = building.m_accessSegment;
            if (nm == null || segmentId == 0 || segmentId >= nm.m_segments.m_size)
                return building.m_position;

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
                return building.m_position;

            Vector3 accessPosition = segment.GetClosestPosition(building.m_position);
            if (float.IsNaN(accessPosition.x) || float.IsNaN(accessPosition.z))
                return building.m_position;

            return accessPosition;
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

            int injected = 0;
            int reducedByExistingBus = 0;
            int noRoad = 0;
            for (int i = 0; i < hubs.Count; i++)
            {
                Vector3 hubPos = hubs[i];
                CachedStopMatch stopMatch;
                if (!stopLocator.TryGetNearestBusStopMatch(hubPos, GetTransitHubBusStopSearchDistance(cfg), out stopMatch))
                {
                    noRoad++;
                    continue;
                }

                int hubDemand = Mathf.Max(cfg.DemandThreshold * 12, 70);
                if (ApplyExistingCoverageDiscount(hubDemand, hubPos, existingStops, cfg) <= 0)
                {
                    hubDemand = Mathf.Max(cfg.DemandThreshold * 8, 45);
                    reducedByExistingBus++;
                }

                demandGrid.AddSample(hubPos, stopMatch, hubDemand, DemandNodePurpose.TransitHub);
                injected++;
            }

            TransitLogging.Log(
                "Transit hub demand injection: hubs=" + hubs.Count +
                ", injected=" + injected +
                ", reducedByNearbyBus=" + reducedByExistingBus +
                ", noBusStopRoad=" + noRoad + ".");
        }

        private float GetTransitHubBusStopSearchDistance(AutoPublicTransitConfig cfg)
        {
            return Mathf.Max(cfg.MaxRoadDistance * 1.5f, Mathf.Max(cfg.MaxWalkingDistance * 4f, 900f));
        }

        private List<TouristAnchorInfo> CollectTouristAnchors()
        {
            var anchors = new List<TouristAnchorInfo>();
            var stats = new TouristAnchorStats();
            BuildingManager bm = BuildingManager.instance;

            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null)
                    continue;

                TouristAnchorCategory category;
                if (!TryGetTouristAnchorCategory(ref building, out category))
                    continue;

                int visitorCapacity = GetTouristAnchorVisitorCapacity(building.Info);
                if (visitorCapacity <= 0)
                    visitorCapacity = GetTouristAnchorFootprintVisitorCapacity(building.Info);

                int demandWeight = GetTouristAnchorDemandWeight(category, visitorCapacity);
                anchors.Add(new TouristAnchorInfo
                {
                    Position = building.m_position,
                    AccessPosition = GetBuildingAccessPosition(ref building),
                    Category = category,
                    VisitorCapacity = visitorCapacity,
                    DemandWeight = demandWeight
                });
                stats.Add(category, visitorCapacity, demandWeight);
            }

            TransitLogging.Log(
                "Tourist anchor scan: total=" + anchors.Count +
                ", monuments=" + stats.Monuments +
                ", uniqueBuildings=" + stats.UniqueBuildings +
                ", parks=" + stats.Parks +
                ", tours=" + stats.Tours +
                ", hotels=" + stats.Hotels +
                ", campuses=" + stats.Campuses +
                ", tourismCommercial=" + stats.TourismCommercial +
                ", leisureCommercial=" + stats.LeisureCommercial +
                ", visitorServices=" + stats.VisitorServices +
                ", capacityWeighted=" + stats.CapacityWeighted +
                ", visitorCapacityTotal=" + stats.VisitorCapacityTotal +
                ", maxVisitorCapacity=" + stats.MaxVisitorCapacity +
                ", demandWeightTotal=" + stats.DemandWeightTotal + ".");

            return anchors;
        }

        private List<Vector3> GetTouristAnchorPositions(List<TouristAnchorInfo> anchors)
        {
            var positions = new List<Vector3>();
            if (anchors == null)
                return positions;

            for (int i = 0; i < anchors.Count; i++)
            {
                positions.Add(anchors[i].Position);

                if (Geometry.DistanceXZ(anchors[i].AccessPosition, anchors[i].Position) > 8f)
                    positions.Add(anchors[i].AccessPosition);
            }

            return positions;
        }

        private bool TryGetTouristAnchorCategory(ref Building building, out TouristAnchorCategory category)
        {
            category = TouristAnchorCategory.None;
            if (building.Info == null || building.Info.m_class == null)
                return false;

            BuildingAI ai = building.Info.m_buildingAI;
            ItemClass.Service service = building.Info.m_class.m_service;
            ItemClass.SubService subService = building.Info.m_class.m_subService;
            if (ai is TourBuildingAI)
            {
                category = TouristAnchorCategory.Tour;
                return true;
            }

            if (ai is HotelAI)
            {
                category = TouristAnchorCategory.Hotel;
                return true;
            }

            if (ai is MonumentAI)
            {
                category = TouristAnchorCategory.Monument;
                return true;
            }

            if (service == ItemClass.Service.Monument)
            {
                category = TouristAnchorCategory.Monument;
                return true;
            }

            if (ai is ParkBuildingAI)
            {
                category = TouristAnchorCategory.Park;
                return true;
            }

            if (ai is MainCampusBuildingAI || ai is CampusBuildingAI)
            {
                category = TouristAnchorCategory.Campus;
                return true;
            }

            if (service == ItemClass.Service.Commercial && subService == ItemClass.SubService.CommercialTourist)
            {
                category = TouristAnchorCategory.TourismCommercial;
                return true;
            }

            if (service == ItemClass.Service.Commercial && subService == ItemClass.SubService.CommercialLeisure)
            {
                category = TouristAnchorCategory.LeisureCommercial;
                return true;
            }

            if (IsUniqueBuildingTouristAnchor(ref building, service, subService, ai))
            {
                category = TouristAnchorCategory.UniqueBuilding;
                return true;
            }

            if (IsVisitorService(service))
            {
                category = TouristAnchorCategory.VisitorService;
                return true;
            }

            return false;
        }

        private bool IsVisitorService(ItemClass.Service service)
        {
            return service == ItemClass.Service.Tourism
                || service == ItemClass.Service.Monument
                || service == ItemClass.Service.Beautification
                || service == ItemClass.Service.Museums
                || service == ItemClass.Service.VarsitySports
                || service == ItemClass.Service.Hotel
                || service == ItemClass.Service.Race;
        }

        private bool IsUniqueBuildingTouristAnchor(ref Building building, ItemClass.Service service, ItemClass.SubService subService, BuildingAI ai)
        {
            if (service == ItemClass.Service.Monument)
                return true;

            if (service == ItemClass.Service.Museums ||
                service == ItemClass.Service.VarsitySports ||
                service == ItemClass.Service.Race ||
                service == ItemClass.Service.Tourism)
            {
                return true;
            }

            string aiName = ai != null ? ai.GetType().Name : string.Empty;
            string prefabName = building.Info != null ? building.Info.name : string.Empty;
            string serviceName = service.ToString();
            string subServiceName = subService.ToString();

            if (ContainsAnyTouristAnchorToken(aiName) ||
                ContainsAnyTouristAnchorToken(prefabName) ||
                ContainsAnyTouristAnchorToken(serviceName) ||
                ContainsAnyTouristAnchorToken(subServiceName))
            {
                return true;
            }

            if (GetTouristAnchorVisitorCapacity(building.Info) <= 0)
                return false;

            return service == ItemClass.Service.Beautification ||
                service == ItemClass.Service.Monument ||
                service == ItemClass.Service.Museums ||
                service == ItemClass.Service.VarsitySports ||
                service == ItemClass.Service.Race ||
                service == ItemClass.Service.Tourism ||
                ContainsIgnoreCase(aiName, "Unique") ||
                ContainsIgnoreCase(prefabName, "Unique") ||
                ContainsIgnoreCase(aiName, "Monument") ||
                ContainsIgnoreCase(prefabName, "Monument");
        }

        private bool ContainsAnyTouristAnchorToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            return ContainsIgnoreCase(value, "Unique") ||
                ContainsIgnoreCase(value, "Monument") ||
                ContainsIgnoreCase(value, "Landmark") ||
                ContainsIgnoreCase(value, "Museum") ||
                ContainsIgnoreCase(value, "Stadium") ||
                ContainsIgnoreCase(value, "Arena") ||
                ContainsIgnoreCase(value, "Sports") ||
                ContainsIgnoreCase(value, "Sport") ||
                ContainsIgnoreCase(value, "Football") ||
                ContainsIgnoreCase(value, "Baseball") ||
                ContainsIgnoreCase(value, "Zoo") ||
                ContainsIgnoreCase(value, "Aquarium") ||
                ContainsIgnoreCase(value, "Theater") ||
                ContainsIgnoreCase(value, "Theatre") ||
                ContainsIgnoreCase(value, "Castle") ||
                ContainsIgnoreCase(value, "Cathedral") ||
                ContainsIgnoreCase(value, "Observatory") ||
                ContainsIgnoreCase(value, "Expo") ||
                ContainsIgnoreCase(value, "Festival") ||
                ContainsIgnoreCase(value, "Tourist") ||
                ContainsIgnoreCase(value, "Tourism");
        }

        private void InjectTouristDemand(
            DemandGrid demandGrid,
            List<TouristAnchorInfo> anchors,
            List<Vector3> existingStops,
            AutoPublicTransitConfig cfg,
            BusStopLocator stopLocator)
        {
            if (anchors == null || anchors.Count == 0)
                return;

            int injected = 0;
            int fallbackInjected = 0;
            int alreadyCovered = 0;
            int coveredCapacityKept = 0;
            int noRoad = 0;
            int accessPositionSnaps = 0;
            int injectedDemandTotal = 0;
            float primaryRoadDistance = cfg.MaxRoadDistance;
            float fallbackRoadDistance = GetTouristAnchorFallbackRoadDistance(cfg);

            for (int i = 0; i < anchors.Count; i++)
            {
                TouristAnchorInfo anchor = anchors[i];
                Vector3 servicePosition = Geometry.DistanceXZ(anchor.AccessPosition, anchor.Position) > 8f
                    ? anchor.AccessPosition
                    : anchor.Position;
                int demandWeight = Mathf.Max(anchor.DemandWeight, Mathf.Max(cfg.DemandThreshold + 8, 20));
                int discountedDemand = ApplyExistingCoverageDiscount(demandWeight, servicePosition, existingStops, cfg);
                if (discountedDemand <= 0)
                {
                    if (!ShouldKeepCoveredTouristCapacity(anchor, cfg))
                    {
                        alreadyCovered++;
                        continue;
                    }

                    discountedDemand = Mathf.Max(Mathf.Max(cfg.DemandThreshold + 8, 20), demandWeight / 3);
                    coveredCapacityKept++;
                }

                CachedStopMatch stopMatch;
                bool matchedFromAccess = false;
                if (!TryGetNearestTouristAnchorBusStopMatch(
                    anchor,
                    primaryRoadDistance,
                    stopLocator,
                    out stopMatch,
                    out matchedFromAccess))
                {
                    if (!TryGetNearestTouristAnchorBusStopMatch(
                        anchor,
                        fallbackRoadDistance,
                        stopLocator,
                        out stopMatch,
                        out matchedFromAccess))
                    {
                        noRoad++;
                        continue;
                    }

                    fallbackInjected++;
                }

                if (matchedFromAccess)
                    accessPositionSnaps++;

                demandGrid.AddSample(servicePosition, stopMatch, discountedDemand, DemandNodePurpose.TouristAnchor);
                injectedDemandTotal += discountedDemand;
                injected++;
            }

            TransitLogging.Log(
                "Tourist anchor demand injection: anchors=" + anchors.Count +
                ", injected=" + injected +
                ", fallbackInjected=" + fallbackInjected +
                ", alreadyCovered=" + alreadyCovered +
                ", coveredCapacityKept=" + coveredCapacityKept +
                ", noRoad=" + noRoad +
                ", accessPositionSnaps=" + accessPositionSnaps +
                ", injectedDemandTotal=" + injectedDemandTotal +
                ", primaryRoadDistance=" + primaryRoadDistance.ToString("0", CultureInfo.InvariantCulture) +
                ", fallbackRoadDistance=" + fallbackRoadDistance.ToString("0", CultureInfo.InvariantCulture) + ".");
        }

        private bool TryGetNearestTouristAnchorBusStopMatch(
            TouristAnchorInfo anchor,
            float maxDistance,
            BusStopLocator stopLocator,
            out CachedStopMatch stopMatch,
            out bool matchedFromAccess)
        {
            matchedFromAccess = false;
            stopMatch = new CachedStopMatch();
            if (stopLocator == null)
                return false;

            if (Geometry.DistanceXZ(anchor.AccessPosition, anchor.Position) > 8f &&
                stopLocator.TryGetNearestBusStopMatch(anchor.AccessPosition, maxDistance, out stopMatch))
            {
                matchedFromAccess = true;
                return true;
            }

            return stopLocator.TryGetNearestBusStopMatch(anchor.Position, maxDistance, out stopMatch);
        }

        private bool ShouldKeepCoveredTouristCapacity(TouristAnchorInfo anchor, AutoPublicTransitConfig cfg)
        {
            int highCapacityThreshold = Mathf.Max(80, cfg.DemandThreshold * 12);
            return anchor.VisitorCapacity >= highCapacityThreshold || anchor.DemandWeight >= highCapacityThreshold;
        }

        private int GetTouristAnchorDemandWeight(TouristAnchorCategory category, int visitorCapacity)
        {
            int baseline = GetTouristAnchorBaselineDemand(category);
            if (visitorCapacity > 0)
                return Mathf.Max(baseline, visitorCapacity);

            return baseline;
        }

        private int GetTouristAnchorBaselineDemand(TouristAnchorCategory category)
        {
            switch (category)
            {
                case TouristAnchorCategory.Monument:
                    return 80;
                case TouristAnchorCategory.UniqueBuilding:
                    return 75;
                case TouristAnchorCategory.Tour:
                    return 90;
                case TouristAnchorCategory.Hotel:
                    return 70;
                case TouristAnchorCategory.Park:
                    return 60;
                case TouristAnchorCategory.Campus:
                    return 50;
                case TouristAnchorCategory.TourismCommercial:
                    return 55;
                case TouristAnchorCategory.LeisureCommercial:
                    return 45;
                case TouristAnchorCategory.VisitorService:
                    return 45;
                default:
                    return 35;
            }
        }

        private int GetTouristAnchorVisitorCapacity(BuildingInfo info)
        {
            if (info == null || info.m_buildingAI == null)
                return 0;

            object ai = info.m_buildingAI;
            int capacity = 0;
            capacity += TryReadPositiveIntField(ai, "m_visitPlaceCount0");
            capacity += TryReadPositiveIntField(ai, "m_visitPlaceCount1");
            capacity += TryReadPositiveIntField(ai, "m_visitPlaceCount2");
            capacity = Mathf.Max(capacity, TryReadPositiveIntField(ai, "m_visitPlaceCount"));
            capacity = Mathf.Max(capacity, TryReadPositiveIntProperty(ai, "visitorCapacity"));
            capacity = Mathf.Max(capacity, TryInvokePositiveIntMethod(ai, "GetVisitorCapacity"));
            capacity = Mathf.Max(capacity, TryInvokePositiveIntMethod(ai, "GetVisitPlaceCount"));
            capacity = Mathf.Max(capacity, TryInvokePositiveIntMethod(ai, "GetVisitplaceCount"));
            return capacity;
        }

        private int GetTouristAnchorFootprintVisitorCapacity(BuildingInfo info)
        {
            if (info == null)
                return 0;

            int width = TryReadPositiveIntField(info, "m_cellWidth");
            int length = TryReadPositiveIntField(info, "m_cellLength");
            if (width <= 0 || length <= 0)
                return 0;

            return width * length * 2;
        }

        private int TryReadPositiveIntField(object target, string fieldName)
        {
            if (target == null)
                return 0;

            FieldInfo field = FindField(target.GetType(), fieldName);
            if (field == null)
                return 0;

            return ToPositiveInt(field.GetValue(target));
        }

        private int TryReadPositiveIntProperty(object target, string propertyName)
        {
            if (target == null)
                return 0;

            PropertyInfo property = FindProperty(target.GetType(), propertyName);
            if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                return 0;

            return ToPositiveInt(property.GetValue(target, null));
        }

        private int TryInvokePositiveIntMethod(object target, string methodName)
        {
            if (target == null)
                return 0;

            MethodInfo method = FindMethod(target.GetType(), methodName);
            if (method == null || method.GetParameters().Length != 0)
                return 0;

            return ToPositiveInt(method.Invoke(target, null));
        }

        private FieldInfo FindField(Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field;

                type = type.BaseType;
            }

            return null;
        }

        private PropertyInfo FindProperty(Type type, string propertyName)
        {
            while (type != null)
            {
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null)
                    return property;

                type = type.BaseType;
            }

            return null;
        }

        private MethodInfo FindMethod(Type type, string methodName)
        {
            while (type != null)
            {
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                    return method;

                type = type.BaseType;
            }

            return null;
        }

        private int ToPositiveInt(object value)
        {
            if (value == null)
                return 0;

            try
            {
                return Mathf.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            catch
            {
                return 0;
            }
        }

        private float GetTouristAnchorFallbackRoadDistance(AutoPublicTransitConfig cfg)
        {
            if (cfg == null)
                return 1200f;

            return Mathf.Min(1600f, Mathf.Max(cfg.MaxRoadDistance * 2f, cfg.MaxWalkingDistance * 4f));
        }

        private enum TouristAnchorCategory
        {
            None,
            Monument,
            UniqueBuilding,
            Park,
            Tour,
            Hotel,
            Campus,
            TourismCommercial,
            LeisureCommercial,
            VisitorService
        }

        private class TouristAnchorInfo
        {
            public Vector3 Position;
            public Vector3 AccessPosition;
            public TouristAnchorCategory Category;
            public int VisitorCapacity;
            public int DemandWeight;
        }

        private class TouristAnchorStats
        {
            public int Monuments;
            public int UniqueBuildings;
            public int Parks;
            public int Tours;
            public int Hotels;
            public int Campuses;
            public int TourismCommercial;
            public int LeisureCommercial;
            public int VisitorServices;
            public int CapacityWeighted;
            public int VisitorCapacityTotal;
            public int MaxVisitorCapacity;
            public int DemandWeightTotal;

            public void Add(TouristAnchorCategory category, int visitorCapacity, int demandWeight)
            {
                switch (category)
                {
                    case TouristAnchorCategory.Monument:
                        Monuments++;
                        break;
                    case TouristAnchorCategory.UniqueBuilding:
                        UniqueBuildings++;
                        break;
                    case TouristAnchorCategory.Park:
                        Parks++;
                        break;
                    case TouristAnchorCategory.Tour:
                        Tours++;
                        break;
                    case TouristAnchorCategory.Hotel:
                        Hotels++;
                        break;
                    case TouristAnchorCategory.Campus:
                        Campuses++;
                        break;
                    case TouristAnchorCategory.TourismCommercial:
                        TourismCommercial++;
                        break;
                    case TouristAnchorCategory.LeisureCommercial:
                        LeisureCommercial++;
                        break;
                    case TouristAnchorCategory.VisitorService:
                        VisitorServices++;
                        break;
                }

                if (visitorCapacity > 0)
                    CapacityWeighted++;

                VisitorCapacityTotal += Mathf.Max(0, visitorCapacity);
                MaxVisitorCapacity = Mathf.Max(MaxVisitorCapacity, visitorCapacity);
                DemandWeightTotal += Mathf.Max(0, demandWeight);
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
