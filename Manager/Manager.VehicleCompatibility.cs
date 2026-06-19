using System;
using ColossalFramework;
using UnityEngine;

namespace AutoPublicTransit
{
    public partial class Manager : MonoBehaviour
    {
        private static readonly string[] UnsafeCityBusVehicleNameMarkers =
        {
            "intercity",
            "coach",
            "tourismo",
            "setra",
            "irizar",
            "aeroking",
            "tasman",
            "sightseeing",
            "tourist",
            "school bus",
            "schoolbus",
            "evacuation"
        };

        private class BusLineVehicleAudit
        {
            public bool HasSelectedVehicle;
            public bool SelectedVehicleSafeCityBus;
            public bool SelectedVehicleUnsafe;
            public bool SelectedVehicleMissing;
            public string SelectedVehicleName;
            public int CompatibleVehicles;
            public int SelectableVehicles;
            public int SafeCityBusVehicles;
            public int SafeSelectableCityBusVehicles;
            public bool ReplacementApplied;
            public string ReplacementVehicleName;
            public bool ReplacementSelectable;
            public bool NoSafeCityBusVehicle;
            public string FailureReason;

            public bool HasUnresolvedIssue
            {
                get
                {
                    return NoSafeCityBusVehicle ||
                           (SelectedVehicleUnsafe && !ReplacementApplied) ||
                           (SelectedVehicleMissing && !ReplacementApplied);
                }
            }

            public string GetLogSummary()
            {
                return "selected=" + (string.IsNullOrEmpty(SelectedVehicleName) ? "none" : SelectedVehicleName) +
                       ", selectedSafeCityBus=" + SelectedVehicleSafeCityBus +
                       ", selectedUnsafe=" + SelectedVehicleUnsafe +
                       ", compatible=" + CompatibleVehicles +
                       ", selectable=" + SelectableVehicles +
                       ", safeCityBus=" + SafeCityBusVehicles +
                       ", safeSelectableCityBus=" + SafeSelectableCityBusVehicles +
                       ", replacementApplied=" + ReplacementApplied +
                       ", replacement=" + (string.IsNullOrEmpty(ReplacementVehicleName) ? "none" : ReplacementVehicleName) +
                       ", replacementSelectable=" + ReplacementSelectable +
                       ", noSafeCityBus=" + NoSafeCityBusVehicle +
                       (string.IsNullOrEmpty(FailureReason) ? "" : ", failure=" + FailureReason);
            }
        }

        private BusLineVehicleAudit EnsureGeneratedBusLineUsesSafeCityBusVehicle(ushort lineId, ref TransportLine line)
        {
            return AuditBusLineVehicleSelection(lineId, ref line, true);
        }

        private BusLineVehicleAudit AuditBusLineVehicleSelection(ushort lineId, ref TransportLine line, bool repairGeneratedLine)
        {
            var audit = new BusLineVehicleAudit();
            try
            {
                VehicleInfo selected = line.GetLineVehicle(lineId);
                audit.HasSelectedVehicle = selected != null;
                audit.SelectedVehicleMissing = selected == null;
                audit.SelectedVehicleName = GetVehicleInfoDiagnosticName(selected);
                audit.SelectedVehicleSafeCityBus = IsSafeCityBusVehicle(selected);
                audit.SelectedVehicleUnsafe = selected != null && !audit.SelectedVehicleSafeCityBus;

                VehicleInfo replacement;
                uint replacementPrefabIndex;
                bool replacementSelectable;
                if (TryFindSafeCityBusVehicle(lineId, ref line, out replacement, out replacementPrefabIndex, out replacementSelectable, audit))
                {
                    if (repairGeneratedLine && !audit.SelectedVehicleSafeCityBus)
                    {
                        TransportManager tm = TransportManager.instance;
                        if (tm != null && replacementPrefabIndex <= int.MaxValue)
                        {
                            tm.AssignSelectedLineVehicle(lineId, (int)replacementPrefabIndex);
                            TryRefreshLineVehicleSelection(tm, lineId);

                            audit.ReplacementApplied = true;
                            audit.ReplacementVehicleName = GetVehicleInfoDiagnosticName(replacement);
                            audit.ReplacementSelectable = replacementSelectable;
                            audit.SelectedVehicleSafeCityBus = true;
                            audit.SelectedVehicleUnsafe = false;
                            audit.SelectedVehicleMissing = false;
                            TransitLogging.Log(
                                "Reassigned generated bus line " + lineId +
                                " to ordinary city bus model '" + audit.ReplacementVehicleName +
                                "' after vehicle audit: " + audit.GetLogSummary() + ".");
                        }
                    }
                }
                else if (!audit.SelectedVehicleSafeCityBus)
                {
                    audit.NoSafeCityBusVehicle = true;
                    if (string.IsNullOrEmpty(audit.FailureReason))
                        audit.FailureReason = "no compatible ordinary city bus model was found";
                }
            }
            catch (Exception e)
            {
                audit.FailureReason = e.GetType().Name + ": " + e.Message;
                TransitLogging.Warn("Bus line vehicle audit failed for line " + lineId + ": " + e + ".");
            }

            return audit;
        }

        private bool TryFindSafeCityBusVehicle(
            ushort lineId,
            ref TransportLine line,
            out VehicleInfo vehicleInfo,
            out uint prefabIndex,
            out bool selectable,
            BusLineVehicleAudit audit)
        {
            vehicleInfo = null;
            prefabIndex = 0u;
            selectable = false;

            var compatible = new FastList<VehicleSelector.Item>();
            try
            {
                line.GetCompatibleVehiclesList(lineId, compatible);
            }
            catch (Exception e)
            {
                if (audit != null)
                    audit.FailureReason = "compatible vehicle list failed: " + e.GetType().Name + ": " + e.Message;
                return false;
            }

            for (int i = 0; i < compatible.m_size; i++)
            {
                VehicleSelector.Item item = compatible.m_buffer[i];
                VehicleInfo candidate = PrefabCollection<VehicleInfo>.GetPrefab(item.m_prefabIndex);
                if (candidate == null)
                    continue;

                if (audit != null)
                {
                    audit.CompatibleVehicles++;
                    if (item.m_selectable)
                        audit.SelectableVehicles++;
                }

                if (!IsSafeCityBusVehicle(candidate))
                    continue;

                if (audit != null)
                {
                    audit.SafeCityBusVehicles++;
                    if (item.m_selectable)
                        audit.SafeSelectableCityBusVehicles++;
                }

                if (item.m_selectable)
                {
                    vehicleInfo = candidate;
                    prefabIndex = item.m_prefabIndex;
                    selectable = true;
                    return true;
                }
            }

            return false;
        }

        private bool IsSafeCityBusVehicle(VehicleInfo info)
        {
            if (info == null || info.m_class == null)
                return false;

            if (info.m_class.m_service != ItemClass.Service.PublicTransport ||
                info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                return false;

            return !IsUnsafeCityBusVehicleName(GetVehicleInfoDiagnosticName(info));
        }

        private bool IsUnsafeCityBusVehicleName(string vehicleName)
        {
            if (string.IsNullOrEmpty(vehicleName))
                return false;

            string value = vehicleName.ToLowerInvariant();
            for (int i = 0; i < UnsafeCityBusVehicleNameMarkers.Length; i++)
            {
                if (value.Contains(UnsafeCityBusVehicleNameMarkers[i]))
                    return true;
            }

            return false;
        }

        private string GetVehicleInfoDiagnosticName(VehicleInfo info)
        {
            if (info == null)
                return null;

            string prefabName = info.name;
            string title = null;
            try
            {
                title = info.GetUncheckedLocalizedTitle();
            }
            catch
            {
            }

            if (string.IsNullOrEmpty(title))
                return string.IsNullOrEmpty(prefabName) ? "(unnamed vehicle)" : prefabName;

            if (string.IsNullOrEmpty(prefabName) || string.Equals(title, prefabName, StringComparison.Ordinal))
                return title;

            return title + " [" + prefabName + "]";
        }

        private void TryRefreshLineVehicleSelection(TransportManager tm, ushort lineId)
        {
            if (tm == null)
                return;

            try
            {
                tm.UpdateLine(lineId);
                tm.UpdateLinesNow();
            }
            catch
            {
            }
        }

        private string AppendVehicleModelIssueName(string existing, string vehicleName)
        {
            if (string.IsNullOrEmpty(vehicleName))
                vehicleName = "none";

            if (string.IsNullOrEmpty(existing))
                return vehicleName;

            if (existing.IndexOf(vehicleName, StringComparison.OrdinalIgnoreCase) >= 0)
                return existing;

            return existing + "; " + vehicleName;
        }
    }
}
