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
        private const int MaxVehicleSelectionDiagnosticItems = 64;
        private static readonly string[] NonOrdinaryBusTransportInfoNameMarkers =
        {
            "intercity",
            "coach",
            "tourist",
            "sightseeing",
            "school",
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

        private BusLineVehicleAudit EnsureGeneratedBusLineUsesSafeCityBusVehicle(ushort lineId, ref TransportLine line, string diagnosticPhase = null)
        {
            return AuditBusLineVehicleSelection(lineId, ref line, true, diagnosticPhase);
        }

        private TransportInfo GetOrdinaryCityBusTransportInfo(TransportManager tm, string diagnosticPhase)
        {
            if (tm == null)
                return null;

            TransportInfo defaultBusInfo = tm.GetTransportInfo(TransportInfo.TransportType.Bus);
            TransportInfo selected = IsOrdinaryCityBusTransportInfo(defaultBusInfo)
                ? defaultBusInfo
                : FindLoadedOrdinaryCityBusTransportInfo();

            string phase = string.IsNullOrEmpty(diagnosticPhase) ? "unspecified" : diagnosticPhase;
            if (selected != null)
            {
                if (!ReferenceEquals(selected, defaultBusInfo))
                {
                    TransitLogging.Warn(
                        "Selected ordinary city bus transport info for " + phase +
                        " from loaded transport infos instead of TransportManager default: selected=" +
                        FormatTransportInfoDiagnostic(selected) +
                        ", default=" + FormatTransportInfoDiagnostic(defaultBusInfo) + ".");
                }
                else
                {
                    TransitLogging.Log(
                        "Selected ordinary city bus transport info for " + phase +
                        ": " + FormatTransportInfoDiagnostic(selected) + ".");
                }

                return selected;
            }

            if (defaultBusInfo != null)
            {
                TransitLogging.Warn(
                    "No clearly ordinary city bus transport info was found for " + phase +
                    "; falling back to TransportManager default: " +
                    FormatTransportInfoDiagnostic(defaultBusInfo) + ".");
            }

            return defaultBusInfo;
        }

        private TransportInfo FindLoadedOrdinaryCityBusTransportInfo()
        {
            TransportInfo best = null;
            int bestScore = int.MinValue;

            try
            {
                int loadedCount = PrefabCollection<TransportInfo>.LoadedCount();
                for (int i = 0; i < loadedCount; i++)
                {
                    TransportInfo candidate = PrefabCollection<TransportInfo>.GetLoaded((uint)i);
                    if (!IsOrdinaryCityBusTransportInfo(candidate))
                        continue;

                    int score = ScoreOrdinaryCityBusTransportInfo(candidate);
                    if (best == null || score > bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to scan loaded bus transport infos: " + e.GetType().Name + ": " + e.Message + ".");
            }

            return best;
        }

        private bool IsOrdinaryCityBusTransportInfo(TransportInfo info)
        {
            if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                return false;

            if (info.m_class != null)
            {
                if (info.m_class.m_service != ItemClass.Service.PublicTransport)
                    return false;

                if (info.m_class.m_subService != ItemClass.SubService.PublicTransportBus)
                    return false;
            }

            return !HasNonOrdinaryBusTransportInfoName(info);
        }

        private int ScoreOrdinaryCityBusTransportInfo(TransportInfo info)
        {
            if (info == null)
                return int.MinValue;

            int score = 0;
            string name = GetTransportInfoDisplayName(info).ToLowerInvariant();
            if (string.Equals(name, "bus", StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (name.Contains("bus"))
                score += 20;
            if (info.m_defaultVehicleDistance > 0f)
                score += 10;
            if (info.m_class != null && info.m_class.m_subService == ItemClass.SubService.PublicTransportBus)
                score += 10;

            return score;
        }

        private BusLineVehicleAudit AuditBusLineVehicleSelection(ushort lineId, ref TransportLine line, bool repairGeneratedLine, string diagnosticPhase = null)
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
                    LogBusVehicleSelectionDiagnostics(lineId, ref line, audit, diagnosticPhase);
                }
            }
            catch (Exception e)
            {
                audit.FailureReason = e.GetType().Name + ": " + e.Message;
                TransitLogging.Warn("Bus line vehicle audit failed for line " + lineId + ": " + e + ".");
            }

            return audit;
        }

        private void LogBusVehicleSelectionDiagnostics(ushort lineId, ref TransportLine line, BusLineVehicleAudit audit, string diagnosticPhase)
        {
            string phase = string.IsNullOrEmpty(diagnosticPhase) ? "unspecified" : diagnosticPhase;
            try
            {
                TransitLogging.Warn(
                    "Bus vehicle selector diagnostics phase=" + phase +
                    ", line=" + lineId +
                    ", lineNumber=" + line.m_lineNumber +
                    ", flags=" + line.m_flags +
                    ", transportInfo=" + GetTransportInfoDiagnosticName(line.Info) +
                    ", selected=" + FormatVehicleInfoDiagnostic(line.GetLineVehicle(lineId)) +
                    ", audit=" + (audit == null ? "none" : audit.GetLogSummary()) + ".");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Bus vehicle selector diagnostics phase=" + phase + " failed before list dump: " + e.GetType().Name + ": " + e.Message + ".");
            }

            LogCompatibleVehicleDiagnostics(lineId, ref line, phase);
            LogLoadedBusVehiclePrefabDiagnostics(phase);
        }

        private void LogCompatibleVehicleDiagnostics(ushort lineId, ref TransportLine line, string phase)
        {
            var compatible = new FastList<VehicleSelector.Item>();
            try
            {
                line.GetCompatibleVehiclesList(lineId, compatible);
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Bus vehicle selector compatible-list diagnostics phase=" + phase + " failed: " + e.GetType().Name + ": " + e.Message + ".");
                return;
            }

            int nullPrefabs = 0;
            int selectable = 0;
            int busSubService = 0;
            int safeCityBus = 0;
            int safeSelectableCityBus = 0;
            int logged = 0;
            for (int i = 0; i < compatible.m_size; i++)
            {
                VehicleSelector.Item item = compatible.m_buffer[i];
                if (item.m_selectable)
                    selectable++;

                VehicleInfo candidate = PrefabCollection<VehicleInfo>.GetPrefab(item.m_prefabIndex);
                if (candidate == null)
                {
                    nullPrefabs++;
                    if (logged < MaxVehicleSelectionDiagnosticItems)
                    {
                        TransitLogging.Log(
                            "Bus vehicle selector compatible item phase=" + phase +
                            ", index=" + i +
                            ", prefabIndex=" + item.m_prefabIndex +
                            ", selectable=" + item.m_selectable +
                            ", vehicle=null.");
                        logged++;
                    }
                    continue;
                }

                bool isBus = IsBusSubServiceVehicle(candidate);
                bool isSafe = IsSafeCityBusVehicle(candidate);
                if (isBus)
                    busSubService++;
                if (isSafe)
                    safeCityBus++;
                if (isSafe && item.m_selectable)
                    safeSelectableCityBus++;

                if (logged < MaxVehicleSelectionDiagnosticItems)
                {
                    TransitLogging.Log(
                        "Bus vehicle selector compatible item phase=" + phase +
                        ", index=" + i +
                        ", prefabIndex=" + item.m_prefabIndex +
                        ", selectable=" + item.m_selectable +
                        ", " + FormatVehicleInfoDiagnostic(candidate) + ".");
                    logged++;
                }
            }

            TransitLogging.Warn(
                "Bus vehicle selector compatible summary phase=" + phase +
                ", rawItems=" + compatible.m_size +
                ", selectable=" + selectable +
                ", nullPrefabs=" + nullPrefabs +
                ", busSubService=" + busSubService +
                ", safeCityBus=" + safeCityBus +
                ", safeSelectableCityBus=" + safeSelectableCityBus +
                ", loggedItems=" + logged +
                (compatible.m_size > logged ? ", truncated=" + (compatible.m_size - logged) : "") + ".");
        }

        private void LogLoadedBusVehiclePrefabDiagnostics(string phase)
        {
            try
            {
                int loadedCount = PrefabCollection<VehicleInfo>.LoadedCount();
                int busSubService = 0;
                int safeCityBus = 0;
                int unsafeNamedBus = 0;
                int logged = 0;

                for (int i = 0; i < loadedCount; i++)
                {
                    VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                    if (!IsBusSubServiceVehicle(info))
                        continue;

                    busSubService++;
                    bool safe = IsSafeCityBusVehicle(info);
                    if (safe)
                        safeCityBus++;
                    else
                        unsafeNamedBus++;

                    if (logged < MaxVehicleSelectionDiagnosticItems)
                    {
                        TransitLogging.Log(
                            "Loaded bus vehicle prefab phase=" + phase +
                            ", loadedIndex=" + i +
                            ", " + FormatVehicleInfoDiagnostic(info) + ".");
                        logged++;
                    }
                }

                TransitLogging.Warn(
                    "Loaded bus vehicle prefab summary phase=" + phase +
                    ", loadedVehiclePrefabs=" + loadedCount +
                    ", busSubService=" + busSubService +
                    ", safeCityBus=" + safeCityBus +
                    ", unsafeNamedBus=" + unsafeNamedBus +
                    ", loggedItems=" + logged +
                    (busSubService > logged ? ", truncated=" + (busSubService - logged) : "") + ".");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Loaded bus vehicle prefab diagnostics phase=" + phase + " failed: " + e.GetType().Name + ": " + e.Message + ".");
            }
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
            if (!IsBusSubServiceVehicle(info))
                return false;

            return !IsUnsafeCityBusVehicleName(GetVehicleInfoDiagnosticName(info));
        }

        private bool IsBusSubServiceVehicle(VehicleInfo info)
        {
            return info != null &&
                   info.m_class != null &&
                   info.m_class.m_service == ItemClass.Service.PublicTransport &&
                   info.m_class.m_subService == ItemClass.SubService.PublicTransportBus;
        }

        private string FormatVehicleInfoDiagnostic(VehicleInfo info)
        {
            if (info == null)
                return "vehicle=null";

            string service = "class=null";
            if (info.m_class != null)
            {
                service = "service=" + info.m_class.m_service +
                          ", subService=" + info.m_class.m_subService +
                          ", level=" + info.m_class.m_level;
            }

            return "vehicle=\"" + GetVehicleInfoDiagnosticName(info) + "\"" +
                   ", " + service +
                   ", busSubService=" + IsBusSubServiceVehicle(info) +
                   ", safeCityBus=" + IsSafeCityBusVehicle(info) +
                   ", unsafeName=" + IsUnsafeCityBusVehicleName(GetVehicleInfoDiagnosticName(info));
        }

        private string GetTransportInfoDiagnosticName(TransportInfo info)
        {
            if (info == null)
                return "null";

            return GetTransportInfoDisplayName(info) + "/" + info.m_transportType;
        }

        private string FormatTransportInfoDiagnostic(TransportInfo info)
        {
            if (info == null)
                return "transportInfo=null";

            string service = "class=null";
            if (info.m_class != null)
            {
                service = "service=" + info.m_class.m_service +
                          ", subService=" + info.m_class.m_subService +
                          ", level=" + info.m_class.m_level;
            }

            return "transportInfo=\"" + GetTransportInfoDisplayName(info) + "\"" +
                   ", prefabName=\"" + (string.IsNullOrEmpty(info.name) ? "(unnamed)" : info.name) + "\"" +
                   ", type=" + info.m_transportType +
                   ", vehicleType=" + info.m_vehicleType +
                   ", " + service +
                   ", defaultVehicleDistance=" + FormatKilometers(info.m_defaultVehicleDistance) +
                   ", ordinaryCityBus=" + IsOrdinaryCityBusTransportInfo(info);
        }

        private string GetTransportInfoDisplayName(TransportInfo info)
        {
            if (info == null)
                return "null";

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
                return string.IsNullOrEmpty(prefabName) ? "(unnamed transport info)" : prefabName;

            if (string.IsNullOrEmpty(prefabName) || string.Equals(title, prefabName, StringComparison.Ordinal))
                return title;

            return title + " [" + prefabName + "]";
        }

        private bool HasNonOrdinaryBusTransportInfoName(TransportInfo info)
        {
            string name = GetTransportInfoDisplayName(info);
            if (string.IsNullOrEmpty(name))
                return false;

            string value = name.ToLowerInvariant();
            for (int i = 0; i < NonOrdinaryBusTransportInfoNameMarkers.Length; i++)
            {
                if (value.Contains(NonOrdinaryBusTransportInfoNameMarkers[i]))
                    return true;
            }

            return false;
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
