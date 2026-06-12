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
        private void EnsureBusDepotCoverage(List<DemandNode> nodes)
        {
            TransportManager tm = TransportManager.instance;
            TransportInfo busInfo = tm.GetTransportInfo(TransportInfo.TransportType.Bus);
            if (busInfo == null)
            {
                TransitLogging.Warn("Could not check bus depots because bus transport info was unavailable.");
                return;
            }

            int depotCount = CountBusDepots();

            TransitLogging.Log("Bus depots found: " + depotCount + ". Extra depot guidance is handled by the bus economics advisory.");

            if (depotCount > 0)
            {
                TransitLogging.Log("Existing bus depots will be reused; no extra depot is placed during this scan.");
                return;
            }

            Vector3 anchor = nodes != null && nodes.Count > 0 ? nodes[0].Centroid : Vector3.zero;
            if (TryPlaceBusDepot(anchor))
            {
                TransitLogging.Log("Placed a bus depot because none were found in the city.");
            }
            else
            {
                TransitLogging.Warn("No bus depots were found, and automatic depot placement did not succeed.");
            }
        }

        private int CountBusDepots()
        {
            BuildingManager bm = BuildingManager.instance;
            int depotCount = 0;

            for (ushort id = 1; id < bm.m_buildings.m_size; id++)
            {
                ref Building building = ref bm.m_buildings.m_buffer[id];
                if ((building.m_flags & Building.Flags.Created) == 0 || building.Info == null || building.Info.m_class == null)
                    continue;

                if (building.Info.m_class.m_service != ItemClass.Service.PublicTransport)
                    continue;

                if (building.Info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                    continue;

                if (building.Info.m_buildingAI is DepotAI)
                    depotCount++;
            }

            return depotCount;
        }

        private bool TryPlaceBusDepot(Vector3 anchor)
        {
            BuildingManager bm = BuildingManager.instance;
            var randomizer = new Randomizer((uint)(DateTime.UtcNow.Ticks ^ 0x5f3759df));

            BuildingInfo depotInfo = bm.GetRandomBuildingInfo(
                ref randomizer,
                ItemClass.Service.PublicTransport,
                ItemClass.SubService.PublicTransportBus,
                ItemClass.Level.Level1,
                0,
                0,
                (BuildingInfo.ZoningMode)0,
                0);

            if (depotInfo == null)
            {
                TransitLogging.Warn("No bus depot prefab was found for automatic placement.");
                return false;
            }

            Vector3 candidate = anchor;
            Vector3 snappedStop;
            if (Util.TryGetNearestBusStopPosition(anchor, 1200f, ConfigManager.Config.AvoidHighways, out snappedStop))
                candidate = snappedStop;

            ushort created;
            return bm.CreateBuilding(out created, ref randomizer, depotInfo, candidate, 0f, 0, 0u) && created != 0;
        }
    }
}
