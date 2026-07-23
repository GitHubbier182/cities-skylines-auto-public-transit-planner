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
    public class BusEconomicsAdvisor
    {
        private const float DepotFixedCostEstimate = 720f;
        private const float VehicleOperatingCostEstimate = 48f;
        private const float DefaultTicketPriceEstimate = 2f;
        private const float PassengerTripMultiplierEstimate = 2.5f;
        private const float UsefulRidershipThreshold = 8f;
        private const int UsefulLinesPerDepotAmortizationTarget = 6;
        private const int UsefulLinesPerDepotSoftCapacity = 12;

        public BusEconomicsSummary BuildSummary(List<ExistingLineSnapshot> busLines)
        {
            var summary = new BusEconomicsSummary();
            summary.DepotCount = CountBusDepots();

            if (busLines == null || busLines.Count == 0)
            {
                ApplyDepotSufficiency(summary);
                summary.Recommendation = summary.DepotCount > 0
                    ? "No complete bus lines were available for economics scoring."
                    : "No bus depot or complete bus lines were available for economics scoring.";
                return summary;
            }

            TransportManager tm = TransportManager.instance;
            float totalRiders = 0f;
            int totalVehicles = 0;
            float totalLengthKm = 0f;
            float totalPositiveContribution = 0f;
            float totalContribution = 0f;

            for (int i = 0; i < busLines.Count; i++)
            {
                ExistingLineSnapshot snapshot = busLines[i];
                if (snapshot == null || snapshot.IsProtectedFromAptManagement)
                    continue;

                ushort lineId = snapshot.LineId;
                if (lineId == 0 || lineId >= tm.m_lines.m_size)
                    continue;

                ref TransportLine line = ref tm.m_lines.m_buffer[lineId];
                if ((line.m_flags & TransportLine.Flags.Created) == 0)
                    continue;

                TransportInfo info = line.Info;
                if (info == null || info.m_transportType != TransportInfo.TransportType.Bus || !line.Complete)
                    continue;

                int riders = GetAverageRidership(ref line);
                int vehicles = CountVehicles(ref line, lineId);
                bool fareRevenueIgnored = TransitPolicyHelper.IsFreeTransportPolicyActiveForStops(snapshot.Stops);
                float contribution = 0f;

                summary.CompleteLineCount++;
                totalRiders += riders;
                totalVehicles += vehicles;
                totalLengthKm += Mathf.Max(0f, snapshot.TotalLength) / 1000f;

                if (fareRevenueIgnored)
                {
                    summary.FareRevenueIgnoredLineCount++;
                }
                else
                {
                    contribution = EstimateLineContribution(riders, vehicles);
                    summary.FareScoredLineCount++;
                    totalContribution += contribution;

                    if (contribution > 0f)
                    {
                        summary.PositiveLineCount++;
                        totalPositiveContribution += contribution;
                    }
                }

                if (IsUsefulLine(snapshot, riders, fareRevenueIgnored, contribution))
                    summary.UsefulLineCount++;
            }

            if (summary.CompleteLineCount == 0)
            {
                ApplyDepotSufficiency(summary);
                summary.Recommendation = "No complete bus lines were available for economics scoring.";
                return summary;
            }

            summary.AveragePassengersPerLine = totalRiders / summary.CompleteLineCount;
            summary.VehicleCount = totalVehicles;
            summary.AverageVehiclesPerLine = (float)totalVehicles / summary.CompleteLineCount;
            summary.AverageRouteLengthKm = totalLengthKm / summary.CompleteLineCount;
            float fareScoredRatio = (float)summary.FareScoredLineCount / summary.CompleteLineCount;
            summary.EstimatedNetworkContribution = summary.FareScoredLineCount > 0
                ? totalContribution - summary.DepotCount * DepotFixedCostEstimate * fareScoredRatio
                : 0f;

            if (summary.FareScoredLineCount == 0 && summary.FareRevenueIgnoredLineCount > 0)
            {
                summary.EstimatedBreakEvenLineCount = float.PositiveInfinity;
            }
            else if (summary.PositiveLineCount > 0)
            {
                summary.AveragePositiveLineContribution = totalPositiveContribution / summary.PositiveLineCount;
                summary.EstimatedBreakEvenLineCount = summary.DepotCount > 0
                    ? (summary.DepotCount * DepotFixedCostEstimate) / Mathf.Max(1f, summary.AveragePositiveLineContribution)
                    : 0f;
            }
            else
            {
                summary.EstimatedBreakEvenLineCount = summary.DepotCount > 0 ? float.PositiveInfinity : 0f;
            }

            ApplyDepotSufficiency(summary);
            summary.Recommendation = BuildRecommendation(summary);
            return summary;
        }

        private string BuildRecommendation(BusEconomicsSummary summary)
        {
            if (summary.DepotCount == 0)
                return "Add or enable a bus depot before expanding bus economics.";

            if (summary.DepotCount < summary.RecommendedDepotCount)
            {
                int missing = summary.RecommendedDepotCount - summary.DepotCount;
                return "APT recommends " + summary.RecommendedDepotCount + " bus " + Plural(summary.RecommendedDepotCount, "depot", "depots") +
                    " for the current bus network; add " + missing + " more.";
            }

            if (summary.DepotCount > summary.RecommendedDepotCount)
                return "APT recommends " + summary.RecommendedDepotCount + " bus " + Plural(summary.RecommendedDepotCount, "depot", "depots") +
                    " for the current bus network; do not add more depots.";

            if (summary.CompleteLineCount == 0)
                return "Build complete useful lines before reviewing bus economics.";

            if (summary.UsefulLineCount < summary.DepotCount * UsefulLinesPerDepotAmortizationTarget)
                return "Use existing depot capacity for more useful lines before adding another depot.";

            if (summary.FareScoredLineCount == 0 && summary.FareRevenueIgnoredLineCount > 0)
                return "Free public transport policy is active on served bus districts; ignore fare-income break-even and maintain by ridership, coverage, and vehicle supply.";

            if (summary.AveragePassengersPerLine < 8f)
                return "Improve ridership or stop purpose before adding more vehicles or depots.";

            if (summary.AverageVehiclesPerLine > 0f && summary.AveragePassengersPerLine / summary.AverageVehiclesPerLine < 5f)
                return "Review vehicle oversupply on weaker bus lines.";

            if (summary.EstimatedBreakEvenLineCount > summary.CompleteLineCount + 1f)
                return "Use existing depot capacity for more useful lines before adding another depot.";

            if (!summary.DepotCapacityLooksSufficient)
                return "Current depots look busy; add another depot only for clear distant demand or vehicle supply pressure.";

            if (summary.EstimatedNetworkContribution > 0f)
                return "Bus network economics look healthy; prioritize coverage gaps and crowded corridors.";

            return "Network is close to useful but not clearly positive; improve demand coverage and protect hub/tourist links.";
        }

        private void ApplyDepotSufficiency(BusEconomicsSummary summary)
        {
            summary.DepotPlanningLineCount = Mathf.Max(summary.CompleteLineCount, summary.UsefulLineCount);
            summary.RecommendedDepotCount = CalculateRecommendedDepotCount(summary.DepotPlanningLineCount);
            summary.DepotCountDifference = summary.RecommendedDepotCount - summary.DepotCount;
            summary.EstimatedUsefulLineCapacity = summary.DepotCount * UsefulLinesPerDepotSoftCapacity;

            if (summary.DepotCount <= 0)
            {
                summary.AdditionalUsefulLinesBeforeDepotPressure = 0;
                summary.UsefulLinesPerDepot = 0f;
                summary.DepotCapacityLooksSufficient = false;
                summary.DepotSufficiencyNote = summary.RecommendedDepotCount <= 1
                    ? "No depot found; add one bus depot before expanding buses."
                    : "No depot found; APT recommends " + summary.RecommendedDepotCount + " bus depots for current lines.";
                return;
            }

            summary.UsefulLinesPerDepot = (float)summary.UsefulLineCount / summary.DepotCount;
            summary.AdditionalUsefulLinesBeforeDepotPressure = Mathf.Max(0, summary.EstimatedUsefulLineCapacity - summary.DepotPlanningLineCount);
            summary.DepotCapacityLooksSufficient = summary.DepotCount >= summary.RecommendedDepotCount;

            if (summary.DepotCount < summary.RecommendedDepotCount)
            {
                int missing = summary.RecommendedDepotCount - summary.DepotCount;
                summary.DepotSufficiencyNote = "APT recommends " + summary.RecommendedDepotCount + " bus " + Plural(summary.RecommendedDepotCount, "depot", "depots") +
                    " for " + summary.DepotPlanningLineCount + " complete " + Plural(summary.DepotPlanningLineCount, "line", "lines") +
                    "; add " + missing + " more.";
                return;
            }

            if (summary.DepotCount > summary.RecommendedDepotCount)
            {
                summary.DepotSufficiencyNote = "APT recommends " + summary.RecommendedDepotCount + " bus " + Plural(summary.RecommendedDepotCount, "depot", "depots") +
                    "; current city has " + summary.DepotCount + ".";
                return;
            }

            if (summary.UsefulLineCount < summary.DepotCount * UsefulLinesPerDepotAmortizationTarget)
            {
                summary.DepotSufficiencyNote = "Depot count is right; let new lines settle before adding more.";
                return;
            }

            summary.DepotSufficiencyNote = "Depot count matches the current bus network.";
        }

        private int CalculateRecommendedDepotCount(int planningLineCount)
        {
            if (planningLineCount <= 0)
                return 1;

            return Mathf.Max(1, Mathf.CeilToInt((float)planningLineCount / UsefulLinesPerDepotSoftCapacity));
        }

        private bool IsUsefulLine(ExistingLineSnapshot snapshot, int riders, bool fareRevenueIgnored, float contribution)
        {
            if (riders >= UsefulRidershipThreshold)
                return true;

            if (snapshot != null && snapshot.StrategicStopCount > 0 && riders > 0)
                return true;

            return !fareRevenueIgnored && riders > 0 && contribution > 0f;
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

        private int GetAverageRidership(ref TransportLine line)
        {
            return (int)(line.m_passengers.m_residentPassengers.m_averageCount
                + line.m_passengers.m_touristPassengers.m_averageCount);
        }

        private int CountVehicles(ref TransportLine line, ushort lineId)
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

        private float EstimateLineContribution(int riders, int vehicles)
        {
            float ticketPrice = DefaultTicketPriceEstimate;
            float estimatedIncome = riders * ticketPrice * PassengerTripMultiplierEstimate;
            float estimatedVehicleCost = vehicles * VehicleOperatingCostEstimate;
            return estimatedIncome - estimatedVehicleCost;
        }

        private string Plural(int count, string singular, string plural)
        {
            return count == 1 ? singular : plural;
        }
    }
}
