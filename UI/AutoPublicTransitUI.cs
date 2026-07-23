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
    public class AutoPublicTransitUI : UIPanel
    {
        public static AutoPublicTransitUI Instance;

        private UIPanel _titleBar;
        private UIButton _closeButton;
        private UIButton _scanButton;
        private UILabel _scanStatus;
        private SummaryRow _scanSummaryRow;
        private SummaryRow _demandSummaryRow;
        private SummaryRow _lineActionsSummaryRow;
        private SummaryRow _depotPlacementBillboardRow;
        private UIButton _metroScanButton;
        private UILabel _metroScanStatus;
        private SummaryRow _metroStationsSummaryRow;
        private SummaryRow _metroNetworkSummaryRow;
        private SummaryRow _metroLinesSummaryRow;
        private SummaryRow _metroCompatibilitySummaryRow;
        private UIButton _trainScanButton;
        private UILabel _trainScanStatus;
        private SummaryRow _trainStationsSummaryRow;
        private SummaryRow _trainNetworkSummaryRow;
        private SummaryRow _trainLinesSummaryRow;
        private SummaryRow _trainCompatibilitySummaryRow;
        private UILabel _roadUpgradeStatus;
        private BusRouteRoadUpgradePlan _pendingRoadUpgradePlan;
        private bool _roadUpgradePlanDialogOpen;
        private UIPanel _roadUpgradePage;
        private SummaryRow _roadUpgradeSummaryRow;
        private SummaryRow _roadUpgradeRulesRow;
        private SummaryRow _roadUpgradeBatchesRow;
        private SummaryRow _roadUpgradeGuidanceRow;
        private UIButton _roadUpgradeApplyButton;
        private UIButton _roadUpgradeCancelButton;
        private readonly List<UILabel> _roadUpgradeReviewBatchLabels = new List<UILabel>();
        private UILabel _lineToolsStatus;
        private UIPanel _navPanel;
        private readonly List<UIButton> _tabButtons = new List<UIButton>();
        private readonly List<UIPanel> _tabPages = new List<UIPanel>();
        private static readonly Color32 PanelTint = new Color32(58, 76, 80, 245);
        private static readonly Color32 CardTint = new Color32(78, 99, 104, 245);
        private static readonly Color32 CardBorderTint = new Color32(104, 139, 146, 255);
        private static readonly Color32 CardHeaderTint = new Color32(66, 95, 102, 255);
        private static readonly Color32 NavTint = new Color32(43, 57, 62, 245);
        private static readonly Color32 TitleTint = new Color32(128, 158, 166, 255);
        private static readonly Color32 TextTint = new Color32(235, 242, 242, 255);
        private static readonly Color32 MutedTextTint = new Color32(196, 212, 214, 255);
        private static readonly Color32 GuidanceCardTint = new Color32(91, 119, 126, 255);
        private static readonly Color32 GuidanceNotesTint = new Color32(222, 238, 240, 255);
        private static readonly Color32 GuidanceAlertTint = new Color32(152, 76, 38, 255);
        private static readonly Color32 GuidanceAlertTextTint = new Color32(255, 248, 226, 255);
        private static readonly Color32 GuidanceAlertNotesTint = new Color32(255, 226, 190, 255);
        private const float OverviewHealthRefreshIntervalSeconds = 2f;
        private const float UiTextScaleMultiplier = 1.12f;
        private float _lastViewWidth = -1f;
        private float _lastViewHeight = -1f;
        private float _nextOverviewHealthRefreshAt;
        private int _selectedTabIndex;

        public override void Start()
        {
            base.Start();

            Instance = this;

            name = "AutoPublicTransitUI";
            width = 1000;
            height = 660;
            backgroundSprite = "MenuPanel2";
            color = PanelTint;
            canFocus = true;
            isInteractive = true;
            isVisible = false;
            relativePosition = new Vector3(120, 120);
            ClampToView();

            _titleBar = AddUIComponent<UIPanel>();
            _titleBar.width = width - 12;
            _titleBar.height = 34;
            _titleBar.relativePosition = new Vector3(6, 6);
            _titleBar.backgroundSprite = "GenericPanel";
            _titleBar.color = TitleTint;

            UIDragHandle dragHandle = _titleBar.AddUIComponent<UIDragHandle>();
            dragHandle.width = _titleBar.width;
            dragHandle.height = _titleBar.height;
            dragHandle.relativePosition = Vector3.zero;
            dragHandle.target = this;

            UILabel title = _titleBar.AddUIComponent<UILabel>();
            title.text = "Auto Public Transit";
            title.textScale = ScaleText(1.08f);
            title.textColor = TextTint;
            title.relativePosition = new Vector3(10, 8);

            _closeButton = UIHelpers.AddButton(_titleBar, "X", OnCloseClicked);
            _closeButton.width = 32;
            _closeButton.height = 26;
            _closeButton.textScale = ScaleText(0.86f);
            _closeButton.relativePosition = new Vector3(_titleBar.width - 40, 4);

            _navPanel = AddUIComponent<UIPanel>();
            _navPanel.width = 188;
            _navPanel.height = height - 58;
            _navPanel.relativePosition = new Vector3(12, 48);
            _navPanel.backgroundSprite = "GenericPanel";
            _navPanel.color = NavTint;

            AddTabButton("Bus", 8, 10, CreateOverviewPage());
            AddTabButton("Metros", 8, 48, CreateMetroPage());
            AddTabButton("Trains", 8, 86, CreateTrainPage());
            AddTabButton("Line Tools", 8, 124, CreateLineToolsPage());

            ShowTab(0);
        }

        public override void OnDestroy()
        {
            ClearRoadUpgradeBatchRows();

            if (Instance == this)
                Instance = null;

            base.OnDestroy();
        }

        public override void Update()
        {
            base.Update();

            if (!isVisible)
                return;

            UIView view = UIView.GetAView();
            if (view == null)
                return;

            if (!Mathf.Approximately(_lastViewWidth, view.fixedWidth)
                || !Mathf.Approximately(_lastViewHeight, view.fixedHeight)
                || IsOutsideView(view))
            {
                ClampToView(view);
            }

            if (_selectedTabIndex == 0 && _scanStatus != null && Time.realtimeSinceStartup >= _nextOverviewHealthRefreshAt)
                RefreshOverviewStatus();

            TryShowPendingRoadUpgradePlanDialog();
        }

        public static void DestroyInstance()
        {
            if (Instance == null)
                return;

            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        public static void Toggle()
        {
            if (Instance == null)
                return;

            bool wasVisible = Instance.isVisible;
            Instance.isVisible = !Instance.isVisible;
            if (Instance.isVisible)
            {
                Instance.ClampToView();
                Instance.BringToFront();
                Instance.RefreshOverviewStatus();
            }
            else if (wasVisible)
            {
                Instance.ClearOverviewGuidance();
            }
        }

        private void ClampToView()
        {
            UIView view = UIView.GetAView();
            if (view != null)
                ClampToView(view);
        }

        private void ClampToView(UIView view)
        {
            if (view == null)
                return;

            float maxX = Mathf.Max(0f, view.fixedWidth - width);
            float maxY = Mathf.Max(0f, view.fixedHeight - height);
            relativePosition = new Vector3(
                Mathf.Clamp(relativePosition.x, 0f, maxX),
                Mathf.Clamp(relativePosition.y, 0f, maxY),
                relativePosition.z);
            _lastViewWidth = view.fixedWidth;
            _lastViewHeight = view.fixedHeight;
        }

        private bool IsOutsideView(UIView view)
        {
            if (view == null)
                return false;

            float maxX = Mathf.Max(0f, view.fixedWidth - width);
            float maxY = Mathf.Max(0f, view.fixedHeight - height);
            return relativePosition.x < 0f
                   || relativePosition.y < 0f
                   || relativePosition.x > maxX
                   || relativePosition.y > maxY;
        }

        public static void UpdateBusLaneSummary(int count)
        {
            if (Instance == null)
                return;

            if (State.LastScanSummary != null && State.LastScanSummary.Completed)
                Instance.ApplyScanSummary(State.LastScanSummary);
        }

        public static void UpdateScanStatus(string text)
        {
            if (Instance == null || Instance._scanStatus == null)
                return;

            Instance.ShowScanStatus(text);
        }

        public static void UpdateScanSummary(TransitScanSummary summary)
        {
            if (Instance == null || Instance._scanStatus == null)
                return;

            Instance.ApplyScanSummary(summary);
        }

        public static void UpdateMetroScanStatus(string text)
        {
            if (Instance == null || Instance._metroScanStatus == null)
                return;

            Instance._metroScanStatus.text = text ?? string.Empty;
        }

        public static void UpdateMetroScanSummary(MetroScanSummary summary)
        {
            if (Instance == null)
                return;

            Instance.ApplyMetroScanSummary(summary);
        }

        public static void ShowMetroScanResult(MetroScanSummary summary)
        {
            ShowPassengerRailScanResult(summary, "Metro", "Metros");
        }

        public static void UpdateTrainScanStatus(string text)
        {
            if (Instance == null || Instance._trainScanStatus == null)
                return;

            Instance._trainScanStatus.text = text ?? string.Empty;
        }

        public static void UpdateTrainScanSummary(MetroScanSummary summary)
        {
            if (Instance == null)
                return;

            Instance.ApplyTrainScanSummary(summary);
        }

        public static void ShowTrainScanResult(MetroScanSummary summary)
        {
            ShowPassengerRailScanResult(summary, "Train", "Trains");
        }

        private static void ShowPassengerRailScanResult(MetroScanSummary summary, string modeName, string pageName)
        {
            if (summary == null)
                return;

            string title = string.IsNullOrEmpty(summary.FailureMessage)
                ? modeName + " Scan Complete"
                : modeName + " Scan Failed";
            string message;
            if (!string.IsNullOrEmpty(summary.FailureMessage))
            {
                message = summary.FailureMessage;
            }
            else
            {
                message = "Stations found: " + summary.StationsFound + "\n" +
                          "Connected station groups: " + summary.ConnectedStationGroups + "\n" +
                          modeName + " lines created: " + summary.CreatedLines + "\n" +
                          "Failed validation: " + summary.FailedLines;

                if (summary.StationsFound == 0)
                    message += "\n\nAPT found " + modeName.ToLowerInvariant() + " track but no eligible passenger station buildings. No infrastructure or lines were changed.";
                else if (summary.CreatedLines == 0)
                    message += "\n\nNo eligible new corridor was found. Check the " + pageName + " summary for existing, single-station, or failed topology corridors.";
            }

            try
            {
                ExceptionPanel panel = UIView.library != null
                    ? UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel")
                    : null;
                if (panel != null)
                    panel.SetMessage(title, message, false);
                else
                    ConfirmPanel.ShowModal(title, message, null);
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to display " + modeName + " scan result dialog: " + e.Message);
            }
        }

        public static void UpdateRoadUpgradeStatus(string text)
        {
            if (Instance == null || Instance._roadUpgradeStatus == null)
                return;

            Instance._roadUpgradeStatus.text = text;
        }

        internal static void ShowRoadUpgradePlan(BusRouteRoadUpgradePlan plan)
        {
            if (Instance == null)
                return;

            if (!AutoPublicTransitConfig.RouteRoadUpgradesPlayerEnabled)
            {
                Instance._pendingRoadUpgradePlan = null;
                Instance._roadUpgradePlanDialogOpen = false;
                UpdateRoadUpgradeStatus(AutoPublicTransitConfig.RouteRoadUpgradesDisabledStatus);
                return;
            }

            Instance._pendingRoadUpgradePlan = plan;
            Instance._roadUpgradePlanDialogOpen = false;

            if (plan == null)
            {
                UpdateRoadUpgradeStatus("Road upgrades: preview failed");
                return;
            }

            UpdateRoadUpgradeStatus(plan.ToPreviewStatusText());
        }

        public static void ShowDepotShortageDialog(BusEconomicsSummary economics)
        {
            if (economics == null)
                return;

            if (economics.DepotCount > 0)
                return;

            int recommended = Mathf.Max(1, economics.RecommendedDepotCount);
            string title = "No Bus Depots";
            string message = "APT found no working bus depots in the city.\n\n" +
                "Current depots: " + economics.DepotCount + "\n" +
                "Recommended depots: " + recommended + "\n\n" +
                "Place and connect at least one bus depot before expecting new APT lines to dispatch buses.";

            try
            {
                if (UIView.library != null)
                {
                    ExceptionPanel panel = UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel");
                    if (panel != null)
                    {
                        panel.SetMessage(title, message, false);
                        TransitLogging.Log("Displayed no bus depot advisory dialog: recommended=" + recommended + ".");
                        return;
                    }
                }

                ConfirmPanel.ShowModal(title, message, null);
                TransitLogging.Log("Displayed fallback no bus depot advisory dialog: recommended=" + recommended + ".");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to display no bus depot advisory dialog: " + e.Message);
            }
        }

        private void ShowScanStatus(string text)
        {
            SetLabelText(_scanStatus, text);
            if (text == null)
                return;

            if (text.IndexOf("queued", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("running", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetSummaryRow(_scanSummaryRow, "Scan", text, "In progress", "Results update when done.");
                SetSummaryRow(_demandSummaryRow, "Demand", "Checking city", "Stops and anchors", "Building demand map.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Waiting", "No changes yet", "Line changes appear after scan.");
                if (!TryApplyBusSpawnHealthGuidance())
                    SetSummaryRow(_depotPlacementBillboardRow, "Guidance", "Wait", "Scan running", "No action until the scan completes.");
            }
        }

        private void ApplyScanSummary(TransitScanSummary summary)
        {
            if (summary == null)
            {
                if (State.HasScanRun)
                {
                    SetLabelText(_scanStatus, "Last scan: complete");
                    SetSummaryRow(_scanSummaryRow, "Scan", "Complete", "Legacy result", "Run another scan for full detail.");
                    SetSummaryRow(_demandSummaryRow, "Demand", "Unavailable", "Run scan", "Latest demand detail not captured.");
                    SetSummaryRow(_lineActionsSummaryRow, "Lines", "Unavailable", "Run scan", "Latest line changes not captured.");
                    ApplyOverviewGuidance(null);
                    return;
                }

                SetLabelText(_scanStatus, "Last scan: not run yet");
                SetSummaryRow(_scanSummaryRow, "Scan", "Not run", "No changes", "Run Scan & Build Bus Lines.");
                SetSummaryRow(_demandSummaryRow, "Demand", "Pending", "City demand", "Demand summary appears after scan.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Pending", "No changes", "APT has not built or adjusted lines yet.");
                ApplyOverviewGuidance(null);
                return;
            }

            if (!string.IsNullOrEmpty(summary.FailureMessage))
            {
                SetLabelText(_scanStatus, "Last scan: failed");
                SetSummaryRow(_scanSummaryRow, "Scan", "Failed", "No safe changes", TrimForUi(summary.FailureMessage, 90));
                SetSummaryRow(_demandSummaryRow, "Demand", "Failed", "Unavailable", "Check APT log.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "No changes", "Scan failed", "APT did not apply line changes.");
                if (!TryApplyBusSpawnHealthGuidance())
                    SetSummaryRow(_depotPlacementBillboardRow, "Guidance", "Check log", "Scan failed", "Fix the logged problem, then rerun scan.");
                return;
            }

            if (!summary.Completed)
            {
                SetLabelText(_scanStatus, "Last scan: running");
                SetSummaryRow(_scanSummaryRow, "Scan", "Running", "Checking city", "Panel updates after completion.");
                SetSummaryRow(_demandSummaryRow, "Demand", "Running", "Buildings and stops", "Demand nodes pending.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Waiting", "No changes yet", "Line changes appear after scan.");
                if (!TryApplyBusSpawnHealthGuidance())
                    SetSummaryRow(_depotPlacementBillboardRow, "Guidance", "Wait", "Scan running", "No action until the scan completes.");
                return;
            }

            SetLabelText(
                _scanStatus,
                "Last scan: complete in " + summary.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                "s (full city demand)");
            SetSummaryRow(_scanSummaryRow, "Scan", "Complete", summary.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s", "Full city demand.");
            SetSummaryRow(_demandSummaryRow, "Demand", summary.DemandNodeCount + " demand areas", summary.ValidStopCandidates + " usable stops", GetDemandNotes(summary));
            string[] lineCells = GetLineActionCells(summary);
            SetSummaryRow(_lineActionsSummaryRow, "Lines", lineCells[0], lineCells[1], lineCells[2]);
            ApplyOverviewGuidance(summary);
        }

        private void ApplyOverviewGuidance(TransitScanSummary summary)
        {
            if (_depotPlacementBillboardRow == null)
                return;

            BusEconomicsSummary economics = summary != null ? summary.BusEconomicsSummary : null;
            if (TryApplyBusSpawnHealthGuidance())
                return;

            ApplyGuidanceRowStyle(false);
            List<DepotPlacementRecommendation> recommendations = summary != null ? State.LastDepotPlacementRecommendations : null;
            if (recommendations != null && recommendations.Count > 0)
            {
                DepotPlacementRecommendation top = recommendations[0];
                SetSummaryRow(
                    _depotPlacementBillboardRow,
                    "Guidance",
                    "Place depot",
                    GetDepotPlacementSiteText(top),
                    GetDepotPlacementNotes(top, recommendations.Count));
                return;
            }

            string[] guidanceCells = GetOverviewGuidanceCells(summary, economics);
            SetSummaryRow(_depotPlacementBillboardRow, "Guidance", guidanceCells[0], guidanceCells[1], guidanceCells[2]);
        }

        private void ClearOverviewGuidance()
        {
            ApplyGuidanceRowStyle(false);
            SetSummaryRow(_depotPlacementBillboardRow, "Guidance", "Pending", "Scan or monitor", "Guidance appears after scans or active alerts.");
        }

        private void RefreshOverviewStatus()
        {
            if (Manager.Instance != null)
                Manager.Instance.RefreshActiveBusSpawnHealthForUi();

            _nextOverviewHealthRefreshAt = Time.realtimeSinceStartup + OverviewHealthRefreshIntervalSeconds;
            ApplyScanSummary(State.LastScanSummary);
        }

        private bool TryApplyBusSpawnHealthGuidance()
        {
            BusSpawnHealthSummary spawnHealth = State.LastBusSpawnHealthSummary;
            if (!ShouldShowBusSpawnHealthGuidance(spawnHealth))
                return false;

            bool depotIssue = State.HasBusDepotIssue(spawnHealth);
            ApplyGuidanceRowStyle(depotIssue);
            SetSummaryRow(
                _depotPlacementBillboardRow,
                "Guidance",
                depotIssue ? "Depot issue" : "Dispatch status",
                GetBusSpawnHealthStatusText(spawnHealth, depotIssue),
                GetBusSpawnHealthNotes(spawnHealth, depotIssue));
            return true;
        }

        private static bool ShouldShowBusSpawnHealthGuidance(BusSpawnHealthSummary health)
        {
            if (health == null || health.CheckedLineCount <= 0)
                return false;

            return health.NeedsPlayerAttention ||
                   health.DepotDispatchPressureVehicleCount > 0 ||
                   health.VehicleShortfallCount > 0 ||
                   health.ReturningToDepotVehicleCount > 0 ||
                   health.VehicleModelRepairCount > 0;
        }

        private static void SetLabelText(UILabel label, string text)
        {
            if (label != null)
                label.text = string.IsNullOrEmpty(text) ? "" : text;
        }

        private static void SetSummaryRow(SummaryRow row, string area, string result, string changed, string notes)
        {
            if (row == null)
                return;

            SetLabelText(row.Area, area);
            SetLabelText(row.Result, TrimForUi(result, row.MaxResultLength));
            SetLabelText(row.Changed, TrimForUi(changed, row.MaxChangedLength));
            SetLabelText(row.Notes, TrimForUi(notes, row.MaxNotesLength));
        }

        private void ApplyGuidanceRowStyle(bool alert)
        {
            SummaryRow row = _depotPlacementBillboardRow;
            if (row == null)
                return;

            if (row.Panel != null)
                row.Panel.color = alert ? GuidanceAlertTint : GuidanceCardTint;

            Color32 primary = alert ? GuidanceAlertTextTint : TextTint;
            Color32 notes = alert ? GuidanceAlertNotesTint : GuidanceNotesTint;
            if (row.Area != null)
                row.Area.textColor = primary;
            if (row.Result != null)
                row.Result.textColor = primary;
            if (row.Changed != null)
                row.Changed.textColor = primary;
            if (row.Notes != null)
                row.Notes.textColor = notes;
        }

        private static string[] GetLineActionCells(TransitScanSummary summary)
        {
            int totalRemoved = summary.LinesRemoved + summary.WeakDuplicateLinesRetired;
            return new[]
            {
                GetLineBuiltText(summary),
                GetLineAdjustmentText(summary, totalRemoved),
                GetLineChangeNotes(summary, totalRemoved)
            };
        }

        private static string[] GetOverviewGuidanceCells(TransitScanSummary summary, BusEconomicsSummary economics)
        {
            if (summary == null)
            {
                return new[]
                {
                    "No active alert",
                    "Run scan",
                    "APT will flag depot jams while enabled."
                };
            }

            if (IsSmallCityNoRouteResult(summary))
            {
                return new[]
                {
                    "Build city up",
                    "Scan later",
                    "Grow more RCI areas, then run APT again."
                };
            }

            if (economics != null && economics.DepotCount <= 0)
            {
                int recommended = Mathf.Max(1, economics.RecommendedDepotCount);
                return new[]
                {
                    "No bus depots",
                    "Place depot",
                    "APT can build lines, but buses need at least 1 connected depot to dispatch. Recommended: " + recommended + "."
                };
            }

            string[] spawnDelayGuidance;
            if (summary.CreatedLines > 0 && TryGetTransitVehicleSpawnDelayGuidance(summary, out spawnDelayGuidance))
                return spawnDelayGuidance;

            if (summary.GeneratedLinesWithoutSafeCityBusVehicle > 0)
            {
                return new[]
                {
                    "Vehicle models",
                    "Enable city bus",
                    "APT skipped generated routes because no ordinary city bus model was available."
                };
            }

            if (summary.CreatedLines > 0)
            {
                return new[]
                {
                    "Let buses settle",
                    summary.CreatedLines + " new " + Plural(summary.CreatedLines, "line", "lines"),
                    "New buses may take time to spawn. Keep depot exits clear."
                };
            }

            if (summary.RepairedGeneratedLines > 0 || summary.LinesRemoved > 0 || summary.WeakDuplicateLinesRetired > 0)
            {
                return new[]
                {
                    "Review map",
                    GetNextChangeText(summary),
                    "APT cleaned up weak or duplicate service."
                };
            }

            if (economics != null && economics.DepotCount < economics.RecommendedDepotCount)
            {
                int missing = Mathf.Max(1, economics.RecommendedDepotCount - economics.DepotCount);
                return new[]
                {
                    "Depot capacity",
                    "Add " + missing + " " + Plural(missing, "depot", "depots"),
                    "Use clear road access near busy lines."
                };
            }

            if (AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled && summary.BusLaneRecommendationCount > 0)
            {
                return new[]
                {
                    "Review roads",
                    summary.BusLaneRecommendationCount + " upgrade " + Plural(summary.BusLaneRecommendationCount, "idea", "ideas"),
                    "Apply road changes only after reviewing them."
                };
            }

            return new[]
            {
                "No action",
                "Network OK",
                "Rerun after major road, zoning, or transport changes."
            };
        }

        private static bool IsSmallCityNoRouteResult(TransitScanSummary summary)
        {
            if (summary == null || !summary.Completed)
                return false;

            if (summary.CreatedLines > 0 ||
                summary.RepairedGeneratedLines > 0 ||
                summary.LinesRemoved > 0 ||
                summary.WeakDuplicateLinesRetired > 0 ||
                summary.ExistingLinesRetained > 0)
                return false;

            return summary.BuiltRouteCount <= 0 &&
                   summary.DemandNodeCount > 0 &&
                   summary.DemandNodeCount <= 6;
        }

        private static bool TryGetTransitVehicleSpawnDelayGuidance(TransitScanSummary summary, out string[] guidanceCells)
        {
            guidanceCells = null;
            if (summary == null || summary.CreatedLines <= 0)
                return false;

            TransitVehicleSpawnDelayStatus status;
            if (!TransitVehicleSpawnDelayCompatibility.TryGetActiveStatus(out status) || status == null || !status.IsActive)
                return false;

            bool unknownDelay = !status.HasBusDelay;
            bool highDelay = status.HasBusDelay && status.BusDelay > 1u;
            if (!unknownDelay && !highDelay)
                return false;

            string delayText = unknownDelay
                ? "Bus delay unknown"
                : "Bus delay " + status.BusDelay.ToString(CultureInfo.InvariantCulture) + "s";
            guidanceCells = new[]
            {
                "Spawn delay active",
                delayText,
                "Set Transit Vehicle Spawn Delay bus spawning delay to 1 or lower until new APT buses spawn."
            };
            return true;
        }

        private static string GetDemandNotes(TransitScanSummary summary)
        {
            string strategic = summary.DemandStrategicNodeCount > 0
                ? summary.DemandStrategicNodeCount + " hub/visitor " + Plural(summary.DemandStrategicNodeCount, "anchor", "anchors")
                : "local demand only";
            return strategic + ".";
        }

        private static string GetLineChangeNotes(TransitScanSummary summary, int totalRemoved)
        {
            if (summary.CreatedLines > 0)
                return "Complete lines published.";

            if (IsSmallCityNoRouteResult(summary))
                return "Build more RCI, then scan again.";

            if (summary.RepairedGeneratedLines > 0)
                return "Looped or awkward lines simplified.";

            if (totalRemoved > 0)
                return "Weak or duplicate lines retired.";

            if (summary.GeneratedLinesWithoutSafeCityBusVehicle > 0)
                return "Routes skipped: no ordinary city bus model.";

            return "No line changes applied.";
        }

        private static string GetLineAdjustmentText(TransitScanSummary summary, int totalRemoved)
        {
            if (IsSmallCityNoRouteResult(summary))
                return "No route yet";

            if (summary.RepairedGeneratedLines <= 0 && totalRemoved <= 0)
                return "No cleanup";

            string text = summary.RepairedGeneratedLines + " trimmed";
            if (totalRemoved > 0)
                text += ", " + totalRemoved + " deleted";
            return text;
        }

        private static string GetLineBuiltText(TransitScanSummary summary)
        {
            if (IsSmallCityNoRouteResult(summary))
                return "Too early";

            if (summary.CreatedLines <= 0)
                return "No new lines";

            return summary.CreatedLines + " built";
        }

        private static string GetNextChangeText(TransitScanSummary summary)
        {
            string text = "";
            if (summary.CreatedLines > 0)
                text = summary.CreatedLines + " built";

            if (summary.RepairedGeneratedLines > 0)
                text = AppendComma(text, summary.RepairedGeneratedLines + " trimmed");

            if (summary.LinesRemoved > 0)
                text = AppendComma(text, summary.LinesRemoved + " deleted");

            if (summary.WeakDuplicateLinesRetired > 0)
                text = AppendComma(text, summary.WeakDuplicateLinesRetired + " duplicates retired");

            if (string.IsNullOrEmpty(text))
                return "No line changes";

            return text;
        }

        private static string AppendComma(string text, string addition)
        {
            if (string.IsNullOrEmpty(text))
                return addition;

            return text + ", " + addition;
        }

        private static string GetBusSpawnHealthStatusText(BusSpawnHealthSummary health, bool depotIssue)
        {
            if (health == null)
                return "Depot access";

            int jammedAtEntrances = Mathf.Max(0, health.DepotDispatchPressureVehicleCount);
            int depotPressureThreshold = Mathf.Max(0, health.DepotDispatchPressureThreshold);
            if (depotIssue)
                return "Jams " + jammedAtEntrances + "/" + depotPressureThreshold + "; buses " + health.AssignedVehicleCount + "/" + health.TargetVehicleCount;

            if (health.LinesWithoutSafeCityBusVehicle > 0 || health.UnsafeVehicleModelLineCount > 0)
                return "Vehicle model issue";

            if (health.VehicleModelRepairCount > 0)
                return "Vehicle model fixed";

            return "Buses " + health.AssignedVehicleCount + "/" + health.TargetVehicleCount + "; checked " + health.CheckedLineCount + " " + Plural(health.CheckedLineCount, "line", "lines");
        }

        private static string GetBusSpawnHealthNotes(BusSpawnHealthSummary health, bool depotIssue)
        {
            if (health == null)
                return "Dispatch pressure details unavailable.";

            int jammedAtEntrances = Mathf.Max(0, health.DepotDispatchPressureVehicleCount);
            int depotPressureThreshold = Mathf.Max(0, health.DepotDispatchPressureThreshold);
            int unassignedTargetVehicles = Mathf.Max(0, health.VehicleShortfallCount);
            int pathWaitingVehicles = Mathf.Max(0, health.WaitingPathVehicleCount);
            int returningToDepots = Mathf.Max(0, health.ReturningToDepotVehicleCount);
            string vehicleModelIssue = health.LinesWithoutSafeCityBusVehicle > 0 || health.UnsafeVehicleModelLineCount > 0 || health.VehicleModelRepairCount > 0
                ? "\nVehicle models: " + GetVehicleModelHealthText(health)
                : "";
            return "Jammed entrances: " + jammedAtEntrances + "/" + depotPressureThreshold +
                "\nUnassigned target: " + unassignedTargetVehicles +
                "\nPath-waiting buses: " + pathWaitingVehicles +
                "\nReturning to depots: " + returningToDepots +
                "\nNo-bus lines: " + health.LinesWithoutVehicles +
                "\nBelow-target lines: " + health.LinesBelowTarget +
                "\nPath-waiting lines: " + health.LinesOnlyWaitingPathVehicles +
                "\nDepots: " + health.DepotCount +
                vehicleModelIssue +
                "\nAction: " + GetBusSpawnHealthActionText(health, depotIssue);
        }

        private static string GetVehicleModelHealthText(BusSpawnHealthSummary health)
        {
            if (health == null)
                return "unavailable";

            if (health.LinesWithoutSafeCityBusVehicle > 0)
                return health.LinesWithoutSafeCityBusVehicle + " no safe city bus";

            if (health.UnsafeVehicleModelLineCount > 0)
                return health.UnsafeVehicleModelLineCount + " unsafe selection";

            if (health.VehicleModelRepairCount > 0)
                return health.VehicleModelRepairCount + " fixed";

            return "OK";
        }

        private static string GetBusSpawnHealthActionText(BusSpawnHealthSummary health, bool depotIssue)
        {
            if (health == null)
                return "Check depot access.";

            if (health.DepotCount <= 0)
                return "Place a connected bus depot.";

            if (health.LinesWithoutSafeCityBusVehicle > 0)
                return "Enable an ordinary city bus model.";

            if (health.UnsafeVehicleModelLineCount > 0)
                return "Select ordinary city buses.";

            if (health.TransitVehicleSpawnDelayActive && health.LinesWithoutVehicles > 0)
                return "Set spawn delay to 1 or lower.";

            if (depotIssue)
                return "Clear exits or add nearby depots.";

            if (health.LinesWithoutVehicles > 0)
                return "Check depot access and vehicle limits.";

            if (health.LinesOnlyWaitingPathVehicles > 0)
                return "Check depot road access.";

            return "Let the city run while vanilla dispatch settles.";
        }

        private static string GetDepotPlacementSiteText(DepotPlacementRecommendation recommendation)
        {
            if (recommendation == null)
                return "No site";

            if (!string.IsNullOrEmpty(recommendation.RoadName))
                return "Near " + recommendation.RoadName;

            return "Near x" + recommendation.Position.x.ToString("0", CultureInfo.InvariantCulture) +
                ", z" + recommendation.Position.z.ToString("0", CultureInfo.InvariantCulture);
        }

        private static string GetDepotPlacementNotes(DepotPlacementRecommendation recommendation, int totalRecommendations)
        {
            if (recommendation == null)
                return "Depot siting guidance unavailable.";

            string extra = totalRecommendations > 1
                ? " " + (totalRecommendations - 1) + " more in log."
                : "";

            return "Place a bus depot near this line cluster." + extra;
        }

        private static string Plural(int count, string singular, string plural)
        {
            return count == 1 ? singular : plural;
        }

        private static string TrimForUi(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;

            return text.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private UIPanel CreateTabPage()
        {
            UIPanel page = AddUIComponent<UIPanel>();
            page.width = width - 224;
            page.height = height - 68;
            page.relativePosition = new Vector3(212, 52);
            page.isVisible = false;
            _tabPages.Add(page);
            return page;
        }

        private void AddTabButton(string text, float x, float y, UIPanel page)
        {
            UIButton tab = UIHelpers.AddButton(_navPanel != null ? (UIComponent)_navPanel : this, text, (c, p) =>
            {
                int index = _tabButtons.IndexOf((UIButton)c);
                if (index >= 0)
                    ShowTab(index);
            });

            tab.width = GetTabWidth(text);
            tab.height = 34;
            tab.textScale = ScaleText(text.Length > 13 ? 0.7f : text.Length > 10 ? 0.76f : 0.82f);
            tab.relativePosition = new Vector3(x, y);
            tab.tooltip = text;
            _tabButtons.Add(tab);
        }

        private static float GetTabWidth(string text)
        {
            return 172f;
        }

        private void ShowTab(int index)
        {
            _selectedTabIndex = index;
            for (int i = 0; i < _tabPages.Count; i++)
            {
                bool selected = i == index;
                _tabPages[i].isVisible = selected;
                _tabButtons[i].normalBgSprite = selected ? "ButtonMenuPressed" : "ButtonMenu";
            }

            if (index == 0 && _scanStatus != null)
                RefreshOverviewStatus();
        }

        private UIPanel CreateOverviewPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Bus", 0, 0);
            float summaryWidth = 760f;

            _scanButton = UIHelpers.AddButton(page, "Scan & Build Bus Lines", OnScanClicked);
            _scanButton.width = 250;
            _scanButton.height = 42;
            _scanButton.textScale = ScaleText(0.9f);
            _scanButton.relativePosition = new Vector3(0, 38);

            _scanStatus = AddFixedLabel(page, "", 264, 44, 492, 32, 0.82f);
            _scanSummaryRow = AddSummaryCard(page, "Scan", 0, 90, summaryWidth, 78, true);
            _demandSummaryRow = AddSummaryCard(page, "Demand", 0, 176, summaryWidth, 80, true);
            _lineActionsSummaryRow = AddSummaryCard(page, "Lines", 0, 264, summaryWidth, 80, true);
            _depotPlacementBillboardRow = AddSummaryCard(page, "Guidance", 0, 352, summaryWidth, 220, true);
            ConfigureGuidanceRow(_depotPlacementBillboardRow);
            ApplyGuidanceRowStyle(false);

            ApplyScanSummary(State.LastScanSummary);
            return page;
        }

        private UIPanel CreateMetroPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Metros", 0, 0);
            float summaryWidth = 760f;

            _metroScanButton = UIHelpers.AddButton(page, "Scan & Build Metro Lines", OnMetroScanClicked);
            _metroScanButton.width = 270;
            _metroScanButton.height = 42;
            _metroScanButton.textScale = ScaleText(0.84f);
            _metroScanButton.relativePosition = new Vector3(0, 38);
            _metroScanButton.tooltip = "Scan only player-built Metro stations and tracks; no city demand scan";

            _metroScanStatus = AddFixedLabel(page, "Last Metro scan: not run", 284, 44, 472, 32, 0.78f);
            _metroStationsSummaryRow = AddSummaryCard(page, "Stations", 0, 90, summaryWidth, 78, true);
            _metroNetworkSummaryRow = AddSummaryCard(page, "Infrastructure", 0, 176, summaryWidth, 88, true);
            _metroLinesSummaryRow = AddSummaryCard(page, "Lines", 0, 272, summaryWidth, 88, true);
            _metroCompatibilitySummaryRow = AddSummaryCard(page, "Compatibility", 0, 368, summaryWidth, 124, true);

            ApplyMetroScanSummary(State.LastMetroScanSummary);
            return page;
        }

        private void ApplyMetroScanSummary(MetroScanSummary summary)
        {
            if (_metroStationsSummaryRow == null)
                return;

            if (summary == null)
            {
                SetSummaryRow(_metroStationsSummaryRow, "Stations", "Not scanned", "No changes", "Build Metro stations and tracks first, then run the Metro-only scan.");
                SetSummaryRow(_metroNetworkSummaryRow, "Infrastructure", "Player-built only", "No demand scan", "APT reads connected vanilla Metro track components and does not construct or alter infrastructure.");
                SetSummaryRow(_metroLinesSummaryRow, "Lines", "Not scanned", "No changes", "APT creates a line only after a complete hidden validation line settles successfully.");
                SetSummaryRow(_metroCompatibilitySummaryRow, "Compatibility", "Vanilla line APIs", "No setting overrides", "Existing Metro lines are left unchanged. IPT/IPTE and TLM remain authoritative for names, vehicles, budgets, colours, and service settings.");
                return;
            }

            if (!string.IsNullOrEmpty(summary.FailureMessage))
            {
                _metroScanStatus.text = "Last Metro scan: failed";
                SetSummaryRow(_metroStationsSummaryRow, "Stations", "Scan failed", "No lines kept", TrimForUi(summary.FailureMessage, 180));
                SetSummaryRow(_metroNetworkSummaryRow, "Infrastructure", "Unchanged", "No construction", "APT did not alter player-built stations or tracks.");
                SetSummaryRow(_metroLinesSummaryRow, "Lines", "0 created", "Validation failed", "Any temporary Metro validation line was released.");
                SetSummaryRow(_metroCompatibilitySummaryRow, "Compatibility", "Vanilla line APIs", "No setting overrides", "Existing Metro lines and third-party transport settings were not changed.");
                return;
            }

            if (!summary.Completed)
            {
                _metroScanStatus.text = "Last Metro scan: settling validation lines";
            }
            else if (summary.CreatedLines > 0)
            {
                _metroScanStatus.text = "Last Metro scan: " + summary.CreatedLines + " line(s) created";
            }
            else if (summary.StationsFound == 0)
            {
                _metroScanStatus.text = "Last Metro scan: no Metro stations found";
            }
            else
            {
                _metroScanStatus.text = "Last Metro scan: no new eligible lines";
            }
            SetSummaryRow(
                _metroStationsSummaryRow,
                "Stations",
                summary.StationsFound + " found",
                summary.StationsWithoutTrack + " without usable track",
                "Metro scans use player-built station and track topology only; demand scoring is not run.");
            SetSummaryRow(
                _metroNetworkSummaryRow,
                "Infrastructure",
                summary.ConnectedStationGroups + " connected groups",
                summary.ExistingLineGroupsSkipped + " already served",
                summary.PlannedCorridors + " topology corridors planned; " + summary.JunctionGroupsPlanned + " junction groups decomposed; " + summary.SingleStationGroupsSkipped + " single-station groups unchanged.");
            SetSummaryRow(
                _metroLinesSummaryRow,
                "Lines",
                summary.CreatedLines + " created",
                summary.FailedLines + " failed validation",
                summary.CandidateLines + " candidate groups; only complete path-settled Metro lines are published.");
            SetSummaryRow(
                _metroCompatibilitySummaryRow,
                "Compatibility",
                summary.ExistingLines + " existing lines retained",
                "No settings changed",
                "APT uses vanilla line creation only and does not set vehicles, names, colours, budgets, ticket prices, or IPT/IPTE/TLM options.");
        }

        private UIPanel CreateTrainPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Passenger Trains", 0, 0);
            float summaryWidth = 760f;

            _trainScanButton = UIHelpers.AddButton(page, "Scan & Build Train Lines", OnTrainScanClicked);
            _trainScanButton.width = 270;
            _trainScanButton.height = 42;
            _trainScanButton.textScale = ScaleText(0.84f);
            _trainScanButton.relativePosition = new Vector3(0, 38);
            _trainScanButton.tooltip = "Scan only player-built passenger train stations and rail tracks; cargo stations are excluded";

            _trainScanStatus = AddFixedLabel(page, "Last Train scan: not run", 284, 44, 472, 32, 0.78f);
            _trainStationsSummaryRow = AddSummaryCard(page, "Stations", 0, 90, summaryWidth, 78, true);
            _trainNetworkSummaryRow = AddSummaryCard(page, "Infrastructure", 0, 176, summaryWidth, 88, true);
            _trainLinesSummaryRow = AddSummaryCard(page, "Lines", 0, 272, summaryWidth, 88, true);
            _trainCompatibilitySummaryRow = AddSummaryCard(page, "Compatibility", 0, 368, summaryWidth, 124, true);

            ApplyTrainScanSummary(State.LastTrainScanSummary);
            return page;
        }

        private void ApplyTrainScanSummary(MetroScanSummary summary)
        {
            if (_trainStationsSummaryRow == null)
                return;

            if (summary == null)
            {
                SetSummaryRow(_trainStationsSummaryRow, "Passenger stations", "Not scanned", "Cargo excluded", "Build passenger train stations and tracks first, then run the Train-only scan.");
                SetSummaryRow(_trainNetworkSummaryRow, "Infrastructure", "Player-built only", "No demand scan", "APT reads connected vanilla-compatible train track components and does not construct or alter infrastructure.");
                SetSummaryRow(_trainLinesSummaryRow, "Lines", "Not scanned", "No changes", "APT creates a passenger line only after a complete hidden validation line settles successfully.");
                SetSummaryRow(_trainCompatibilitySummaryRow, "Compatibility", "Vanilla line APIs", "No setting overrides", "Existing passenger Train lines are left unchanged. Cargo stations and cargo lines are never planned or modified.");
                return;
            }

            if (!string.IsNullOrEmpty(summary.FailureMessage))
            {
                _trainScanStatus.text = "Last Train scan: failed";
                SetSummaryRow(_trainStationsSummaryRow, "Passenger stations", "Scan failed", summary.CargoStationsSkipped + " cargo skipped", TrimForUi(summary.FailureMessage, 180));
                SetSummaryRow(_trainNetworkSummaryRow, "Infrastructure", "Unchanged", "No construction", "APT did not alter player-built stations or tracks.");
                SetSummaryRow(_trainLinesSummaryRow, "Lines", "0 created", "Validation failed", "Any temporary passenger Train validation line was released.");
                SetSummaryRow(_trainCompatibilitySummaryRow, "Compatibility", "Vanilla line APIs", "No setting overrides", "Existing passenger and cargo Train lines were not changed.");
                return;
            }

            if (!summary.Completed)
                _trainScanStatus.text = "Last Train scan: settling validation lines";
            else if (summary.CreatedLines > 0)
                _trainScanStatus.text = "Last Train scan: " + summary.CreatedLines + " line(s) created";
            else if (summary.StationsFound == 0)
                _trainScanStatus.text = "Last Train scan: no passenger stations found";
            else
                _trainScanStatus.text = "Last Train scan: no new eligible lines";

            SetSummaryRow(
                _trainStationsSummaryRow,
                "Passenger stations",
                summary.StationsFound + " found",
                summary.CargoStationsSkipped + " cargo skipped",
                summary.StationsWithoutTrack + " passenger stations lacked owned usable track; demand scoring is not run.");
            SetSummaryRow(
                _trainNetworkSummaryRow,
                "Infrastructure",
                summary.ConnectedStationGroups + " connected groups",
                summary.ExistingLineGroupsSkipped + " already served",
                summary.PlannedCorridors + " topology corridors planned; " + summary.JunctionGroupsPlanned + " junction groups decomposed; " + summary.SingleStationGroupsSkipped + " single-station groups unchanged.");
            SetSummaryRow(
                _trainLinesSummaryRow,
                "Passenger lines",
                summary.CreatedLines + " created",
                summary.FailedLines + " failed validation",
                summary.CandidateLines + " candidate groups; only complete path-settled passenger Train lines are published.");
            SetSummaryRow(
                _trainCompatibilitySummaryRow,
                "Compatibility",
                summary.ExistingLines + " passenger lines retained",
                "Cargo untouched",
                "APT uses vanilla passenger-line creation only and does not set vehicles, names, colours, budgets, tickets, timetables, or cargo service.");
        }

        private UIPanel CreateRoadUpgradesPage()
        {
            UIPanel page = CreateTabPage();
            _roadUpgradePage = page;
            AddSectionLabel(page, "Road Upgrades", 0, 0);
            float summaryWidth = 760f;

            UIButton roadUpgradeButton = UIHelpers.AddButton(page, "Upgrade Roads", OnRoadUpgradesClicked);
            roadUpgradeButton.width = 176;
            roadUpgradeButton.height = 42;
            roadUpgradeButton.textScale = ScaleText(0.82f);
            roadUpgradeButton.relativePosition = new Vector3(0, 38);
            roadUpgradeButton.tooltip = "Preview grouped RON bus-lane road upgrades";

            _roadUpgradeStatus = AddFixedLabel(page, "Road upgrades: ready", 194, 43, 560, 28, 0.78f);
            _roadUpgradeStatus.textColor = MutedTextTint;

            _roadUpgradeSummaryRow = AddSummaryCard(page, "Review", 0, 90, summaryWidth, 78, true);
            _roadUpgradeRulesRow = AddSummaryCard(page, "Rules", 0, 176, summaryWidth, 88, true);
            _roadUpgradeBatchesRow = AddSummaryCard(page, "Batches", 0, 272, summaryWidth, 190, true);
            _roadUpgradeGuidanceRow = AddSummaryCard(page, "Guidance", 0, 470, summaryWidth, 102, true);
            ConfigureRoadUpgradeRow(_roadUpgradeSummaryRow, 140);
            ConfigureRoadUpgradeRow(_roadUpgradeRulesRow, 220);
            ConfigureRoadUpgradeRow(_roadUpgradeBatchesRow, 120);
            ConfigureRoadUpgradeRow(_roadUpgradeGuidanceRow, 160);
            ApplyDefaultRoadUpgradePageText();

            _roadUpgradeApplyButton = UIHelpers.AddButton(page, "Apply with RON", (c, p) => ApplyPendingRoadUpgradeReview());
            _roadUpgradeApplyButton.width = 168;
            _roadUpgradeApplyButton.height = 34;
            _roadUpgradeApplyButton.textScale = ScaleText(0.78f);
            _roadUpgradeApplyButton.relativePosition = new Vector3(408, 42);
            _roadUpgradeApplyButton.isVisible = false;

            _roadUpgradeCancelButton = UIHelpers.AddButton(page, "Cancel", (c, p) => CancelPendingRoadUpgradeReview());
            _roadUpgradeCancelButton.width = 150;
            _roadUpgradeCancelButton.height = 34;
            _roadUpgradeCancelButton.textScale = ScaleText(0.78f);
            _roadUpgradeCancelButton.relativePosition = new Vector3(586, 42);
            _roadUpgradeCancelButton.isVisible = false;

            return page;
        }

        private UIPanel CreateLineToolsPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Line Tools", 0, 0);

            UIPanel deleteCard = AddOptionCard(page, "Delete All Lines", 0, 42, 700, 466);
            LineDeleteMode[] modes = GetLineDeleteModes();
            for (int i = 0; i < modes.Length; i++)
            {
                int column = i % 2;
                int row = i / 2;
                AddDeleteLinesButton(deleteCard, modes[i], 16 + column * 334, 50 + row * 38);
            }

            _lineToolsStatus = AddFixedLabel(deleteCard, "Select a transport type to delete its lines.", 16, 428, 660, 30, 0.74f);
            _lineToolsStatus.textColor = MutedTextTint;
            return page;
        }

        private void AddDeleteLinesButton(UIComponent parent, LineDeleteMode mode, float x, float y)
        {
            UIButton button = UIHelpers.AddButton(parent, "Delete " + mode.DisplayName, (c, p) => OnDeleteLinesClicked(mode));
            button.width = 316;
            button.height = 32;
            button.textScale = ScaleText(mode.DisplayName.Length > 14 ? 0.68f : 0.74f);
            button.relativePosition = new Vector3(x, y);
            button.tooltip = "Delete all " + mode.DisplayName.ToLowerInvariant() + " lines";
        }

        private void OnDeleteLinesClicked(LineDeleteMode mode)
        {
            if (Manager.Instance == null)
            {
                SetLineToolsStatus("Manager is not ready.");
                TransitLogging.Error("Manager instance was not ready when delete lines was pressed.");
                return;
            }

            int lineCount = Manager.Instance.CountDeletableLines(mode.TransportType);
            if (lineCount <= 0)
            {
                SetLineToolsStatus("No unprotected " + mode.DisplayName.ToLowerInvariant() + " lines found.");
                TransitLogging.Log("Delete all " + mode.TransportType + " lines skipped because no unprotected lines were found.");
                return;
            }

            string message =
                "Delete all " + lineCount + " unprotected " + mode.DisplayName.ToLowerInvariant() + " " + Plural(lineCount, "line", "lines") + "?\n\n" +
                "Lines with DND in the name will be skipped.\n\n" +
                "This cannot be undone.";
            ConfirmPanel.ShowModal(
                "Delete " + mode.DisplayName + " Lines",
                message,
                (component, result) =>
            {
                if (result != 1)
                {
                    SetLineToolsStatus("Delete cancelled.");
                    TransitLogging.Log("Delete all " + mode.TransportType + " lines cancelled by player.");
                    return;
                }

                DeleteLines(mode);
            });
        }

        private void DeleteLines(LineDeleteMode mode)
        {
            if (Manager.Instance == null)
            {
                SetLineToolsStatus("Manager is not ready.");
                return;
            }

            LineDeletionResult result = Manager.Instance.DeleteAllLines(mode.TransportType);
            if (result.DeletedLines > 0)
                ApplyScanSummary(State.LastScanSummary);

            string status = result.DeletedLines + " " + mode.DisplayName.ToLowerInvariant() + " " +
                Plural(result.DeletedLines, "line", "lines") + " deleted.";
            if (result.ProtectedLinesSkipped > 0)
                status += " " + result.ProtectedLinesSkipped + " protected.";
            if (result.FailedLines > 0)
                status += " " + result.FailedLines + " failed.";

            SetLineToolsStatus(status);
        }

        private void SetLineToolsStatus(string text)
        {
            SetLabelText(_lineToolsStatus, text);
        }

        private UIPanel AddOptionCard(UIComponent parent, string title, float x, float y, float cardWidth, float cardHeight)
        {
            UIPanel border = parent.AddUIComponent<UIPanel>();
            border.width = cardWidth;
            border.height = cardHeight;
            border.relativePosition = new Vector3(x, y);
            border.backgroundSprite = "GenericPanel";
            border.color = CardBorderTint;

            UIPanel card = border.AddUIComponent<UIPanel>();
            card.width = cardWidth - 4;
            card.height = cardHeight - 4;
            card.relativePosition = new Vector3(2, 2);
            card.backgroundSprite = "GenericPanel";
            card.color = CardTint;

            UIPanel headerBand = card.AddUIComponent<UIPanel>();
            headerBand.width = card.width;
            headerBand.height = 38;
            headerBand.relativePosition = Vector3.zero;
            headerBand.backgroundSprite = "GenericPanel";
            headerBand.color = CardHeaderTint;

            UILabel header = AddFixedLabel(card, title, 16, 9, card.width - 32, 24, 0.74f);
            header.textColor = TextTint;
            header.wordWrap = false;
            return card;
        }

        private UILabel AddSectionLabel(UIComponent parent, string text, float x, float y)
        {
            UILabel label = AddFixedLabel(parent, text, x, y, 500, 30, 1.0f);
            label.textColor = TextTint;
            label.wordWrap = false;
            return label;
        }

        private SummaryRow AddSummaryCard(UIComponent parent, string area, float x, float y, float cardWidth, float cardHeight, bool wide)
        {
            UIPanel card = parent.AddUIComponent<UIPanel>();
            card.width = cardWidth;
            card.height = cardHeight;
            card.relativePosition = new Vector3(x, y);
            card.backgroundSprite = "GenericPanel";
            card.color = CardTint;

            var row = new SummaryRow();
            row.Panel = card;
            row.MaxResultLength = wide ? 48 : 26;
            row.MaxChangedLength = wide ? 54 : 28;
            row.MaxNotesLength = wide ? 120 : 58;
            row.Area = AddFixedLabel(card, area, 12, 7, cardWidth - 24, 22, wide ? 0.78f : 0.72f);
            row.Area.textColor = TextTint;
            row.Result = AddFixedLabel(card, "", 12, 31, (cardWidth - 32) * 0.5f, 24, wide ? 0.76f : 0.7f);
            row.Result.textColor = TextTint;
            row.Changed = AddFixedLabel(card, "", 20 + (cardWidth - 32) * 0.5f, 31, (cardWidth - 32) * 0.5f, 24, wide ? 0.76f : 0.7f);
            row.Changed.textColor = TextTint;
            row.Notes = AddFixedLabel(card, "", 12, 58, cardWidth - 24, cardHeight - 62, wide ? 0.7f : 0.64f);
            row.Notes.textColor = MutedTextTint;
            return row;
        }

        private UILabel AddFixedLabel(UIComponent parent, string text, float x, float y, float labelWidth, float labelHeight, float textScale)
        {
            UILabel label = parent.AddUIComponent<UILabel>();
            label.text = text;
            label.width = labelWidth;
            label.height = labelHeight;
            label.textScale = ScaleText(textScale);
            label.textColor = TextTint;
            label.autoSize = false;
            label.autoHeight = false;
            label.wordWrap = true;
            label.relativePosition = new Vector3(x, y);
            return label;
        }

        private static void ConfigureGuidanceRow(SummaryRow row)
        {
            if (row == null)
                return;

            row.MaxResultLength = 40;
            row.MaxChangedLength = 64;
            row.MaxNotesLength = 260;
            if (row.Area != null)
                row.Area.textScale = ScaleText(0.84f);
            if (row.Result != null)
                row.Result.textScale = ScaleText(0.82f);
            if (row.Changed != null)
                row.Changed.textScale = ScaleText(0.78f);
            if (row.Notes != null)
            {
                row.Notes.textScale = ScaleText(0.72f);
                row.Notes.wordWrap = false;
            }
        }

        private static void ConfigureRoadUpgradeRow(SummaryRow row, int maxNotesLength)
        {
            if (row == null)
                return;

            row.MaxResultLength = 44;
            row.MaxChangedLength = 54;
            row.MaxNotesLength = maxNotesLength;
            if (row.Notes != null)
            {
                row.Notes.textScale = ScaleText(0.66f);
                row.Notes.wordWrap = true;
            }
        }

        private static float ScaleText(float textScale)
        {
            return textScale * UiTextScaleMultiplier;
        }

        private class SummaryRow
        {
            public UIPanel Panel;
            public UILabel Area;
            public UILabel Result;
            public UILabel Changed;
            public UILabel Notes;
            public int MaxResultLength = 48;
            public int MaxChangedLength = 48;
            public int MaxNotesLength = 88;
        }

        private void OnScanClicked(UIComponent c, UIMouseEventParameter p)
        {
            TransitLogging.Log("Scan button pressed.");

            if (Manager.Instance == null)
            {
                TransitLogging.Error("Manager instance was not ready when Scan was pressed.");
                return;
            }

            ConfigManager.ApplyLockedBusPlanningProfile();
            string riskReason;
            bool riskySettings = ConfigManager.Config.HasRiskyBusPlanningSettings(out riskReason);
            bool initialNetworkScan = !Manager.Instance.HasCompletePublicBusLines();
            if (riskySettings || initialNetworkScan)
            {
                ShowSlowScanConfirmation(initialNetworkScan, riskySettings ? riskReason : null);
                return;
            }

            QueueTransitScan();
        }

        private void OnMetroScanClicked(UIComponent c, UIMouseEventParameter p)
        {
            TransitLogging.Log("Metro scan button pressed.");
            if (Manager.Instance == null)
            {
                TransitLogging.Error("Manager instance was not ready when Metro scan was pressed.");
                UpdateMetroScanStatus("Last Metro scan: manager is not ready");
                return;
            }

            UpdateMetroScanStatus("Last Metro scan: queued");
            AutoPublicTransitThreading.RunMetroScan = true;
        }

        private void OnTrainScanClicked(UIComponent c, UIMouseEventParameter p)
        {
            TransitLogging.Log("Passenger Train scan button pressed.");
            if (Manager.Instance == null)
            {
                TransitLogging.Error("Manager instance was not ready when passenger Train scan was pressed.");
                UpdateTrainScanStatus("Last Train scan: manager is not ready");
                return;
            }

            UpdateTrainScanStatus("Last Train scan: queued");
            AutoPublicTransitThreading.RunTrainScan = true;
        }

        private void ShowSlowScanConfirmation(bool initialNetworkScan, string riskReason)
        {
            bool hasRiskReason = !string.IsNullOrEmpty(riskReason);
            string logReason = initialNetworkScan
                ? "no complete public bus lines exist"
                : "bus planning settings look risky";
            if (hasRiskReason && initialNetworkScan)
                logReason += "; risky settings: " + riskReason;
            else if (hasRiskReason)
                logReason = "bus planning settings look risky: " + riskReason;
            TransitLogging.Warn("Scan requires confirmation because " + logReason + ".");

            if (_scanStatus != null)
                _scanStatus.text = "Last scan: waiting for confirmation";

            try
            {
                string trimmedRiskReason = TrimForUi(riskReason, 80);
                string message = initialNetworkScan
                    ? "No public bus lines exist.\n\nAPT will build the first bus network. This first run can take a while."
                    : "This scan may be slow.";
                if (hasRiskReason)
                    message += "\n\nSettings risk: " + trimmedRiskReason;
                message += "\n\nRun scan?";

                ConfirmPanel.ShowModal(
                    initialNetworkScan ? "Initial Network Scan" : "Potentially Slow Scan",
                    message,
                    OnSlowScanConfirmed);
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to show slow-scan confirmation: " + e.Message);
                if (_scanStatus != null)
                    _scanStatus.text = "Last scan: confirmation failed";
            }
        }

        private void OnSlowScanConfirmed(UIComponent component, int result)
        {
            if (result != 1)
            {
                TransitLogging.Log("Slow scan cancelled by player.");
                if (_scanStatus != null)
                    _scanStatus.text = "Last scan: cancelled";
                return;
            }

            TransitLogging.Log("Slow scan confirmed by player.");
            QueueTransitScan();
        }

        private void QueueTransitScan()
        {
            if (_scanStatus != null)
                _scanStatus.text = "Last scan: queued";

            AutoPublicTransitThreading.RunScan = true;
        }

        private void OnRoadUpgradesClicked(UIComponent c, UIMouseEventParameter p)
        {
            TransitLogging.Log("Upgrade Roads button pressed.");

            if (!AutoPublicTransitConfig.RouteRoadUpgradesPlayerEnabled)
            {
                TransitLogging.Warn(AutoPublicTransitConfig.RouteRoadUpgradesDisabledLog);
                UpdateRoadUpgradeStatus(AutoPublicTransitConfig.RouteRoadUpgradesDisabledStatus);
                return;
            }

            if (Manager.Instance == null)
            {
                TransitLogging.Error("Manager instance was not ready when road upgrades were pressed.");
                return;
            }

            var ron = new RonNetworkReplacer();
            if (!ron.IsAvailable)
            {
                TransitLogging.Warn("Upgrade Roads requires RON: " + ron.UnavailableReason + ".");
                UpdateRoadUpgradeStatus("Road upgrades: RON required");
                return;
            }

            _pendingRoadUpgradePlan = null;
            _roadUpgradePlanDialogOpen = false;
            UpdateRoadUpgradeStatus("Road upgrades: scanning routes");
            AutoPublicTransitThreading.PreviewRoadUpgrades = true;
        }

        private void TryShowPendingRoadUpgradePlanDialog()
        {
            if (!AutoPublicTransitConfig.RouteRoadUpgradesPlayerEnabled)
            {
                _pendingRoadUpgradePlan = null;
                _roadUpgradePlanDialogOpen = false;
                return;
            }

            if (_pendingRoadUpgradePlan == null || _roadUpgradePlanDialogOpen || !isVisible)
                return;

            BusRouteRoadUpgradePlan plan = _pendingRoadUpgradePlan;
            if (plan.Groups.Count <= 0)
            {
                _pendingRoadUpgradePlan = null;
                return;
            }

            _roadUpgradePlanDialogOpen = true;
            ShowRoadUpgradeReviewPage(plan);
        }

        private void ApplyDefaultRoadUpgradePageText()
        {
            SetSummaryRow(
                _roadUpgradeSummaryRow,
                "Review",
                "Ready",
                "RON required",
                "Press Upgrade Roads to scan complete bus routes and prepare grouped RON replacement batches.");
            SetSummaryRow(
                _roadUpgradeRulesRow,
                "Rules",
                "Same width",
                "Same direction",
                "APT only considers vanilla roads that can gain bus lanes without changing footprint, direction, highway class, or surface/elevated/bridge/tunnel variant.");
            SetSummaryRow(
                _roadUpgradeBatchesRow,
                "Batches",
                "No preview",
                "Run upgrade",
                "Grouped source-road to bus-lane-road batches will appear here before anything is applied.");
            SetSummaryRow(
                _roadUpgradeGuidanceRow,
                "Guidance",
                "After apply",
                "Let city run",
                "APT checks each applied RON chunk for newly collapsed route roads before moving on.");
        }

        private static string GetRoadUpgradeSkippedText(BusRouteRoadUpgradePlan plan)
        {
            if (plan == null || plan.Result == null)
                return "Review grouped replacements before applying.";

            return "Skipped: " + plan.Result.AlreadyBusLaneSkipped + " already had bus lanes; " +
                plan.Result.UnsafeSkipped + " unsafe or special; " +
                plan.Result.NoTargetSkipped + " had no same-width same-variant vanilla bus-lane match.";
        }

        private void ShowRoadUpgradeReviewPage(BusRouteRoadUpgradePlan plan)
        {
            if (_roadUpgradePage != null)
            {
                int roadUpgradeTabIndex = _tabPages.IndexOf(_roadUpgradePage);
                if (roadUpgradeTabIndex >= 0)
                    ShowTab(roadUpgradeTabIndex);
            }

            SetSummaryRow(
                _roadUpgradeSummaryRow,
                "Review",
                plan.Groups.Count + " " + Plural(plan.Groups.Count, "batch", "batches"),
                plan.TotalSegmentCount + " segments",
                GetRoadUpgradeSkippedText(plan));
            SetSummaryRow(
                _roadUpgradeRulesRow,
                "Rules",
                "Same width",
                "Same direction",
                "Vanilla roads only. Matches surface, elevated, bridge, slope, tunnel, and highway variants like-for-like. Skips existing bus lanes, unsafe or special roads, custom roads, and bus-only targets.");
            SetSummaryRow(
                _roadUpgradeBatchesRow,
                "Batches",
                plan.Groups.Count + " road types",
                plan.TotalSegmentCount + " segments",
                "Largest grouped RON replacements are listed below. Chunk audits and batch detail are written to the APT log.");
            SetSummaryRow(
                _roadUpgradeGuidanceRow,
                "Guidance",
                "After apply",
                "Let city run",
                "APT checks each applied RON chunk for newly collapsed route roads before moving on. Rerun Scan & Build Bus Lines after roads settle.");

            AddRoadUpgradeBatchRows(plan);

            if (_roadUpgradeApplyButton != null)
                _roadUpgradeApplyButton.isVisible = true;
            if (_roadUpgradeCancelButton != null)
                _roadUpgradeCancelButton.isVisible = true;
        }

        private void AddRoadUpgradeBatchRows(BusRouteRoadUpgradePlan plan)
        {
            ClearRoadUpgradeBatchRows();
            if (plan == null || _roadUpgradeBatchesRow == null || _roadUpgradeBatchesRow.Panel == null)
                return;

            int rowCount = Mathf.Min(plan.Groups.Count, 4);
            for (int i = 0; i < rowCount; i++)
            {
                BusRouteRoadUpgradeGroup group = plan.Groups[i];
                UILabel row = AddFixedLabel(
                    _roadUpgradeBatchesRow.Panel,
                    (i + 1) + ". " + TrimForUi(group.CurrentName, 30) + " -> " + TrimForUi(group.TargetName, 36) +
                    " | " + group.SegmentCount + " segments | " +
                    group.BusLineUseCount + " uses",
                    12,
                    84 + (i * 20),
                    732,
                    18,
                    0.52f);
                row.textColor = i % 2 == 0 ? TextTint : MutedTextTint;
                row.wordWrap = false;
                _roadUpgradeReviewBatchLabels.Add(row);
            }

            if (plan.Groups.Count > rowCount)
            {
                UILabel more = AddFixedLabel(
                    _roadUpgradeBatchesRow.Panel,
                    (plan.Groups.Count - rowCount) + " more road-type batches are in the APT log.",
                    12,
                    84 + (rowCount * 20) + 2,
                    732,
                    18,
                    0.54f);
                more.textColor = MutedTextTint;
                more.wordWrap = false;
                _roadUpgradeReviewBatchLabels.Add(more);
            }
        }

        private void ApplyPendingRoadUpgradeReview()
        {
            BusRouteRoadUpgradePlan plan = _pendingRoadUpgradePlan;
            if (plan == null)
            {
                UpdateRoadUpgradeStatus("Road upgrades: preview expired");
                ResetRoadUpgradeReviewPage();
                return;
            }

            if (_pendingRoadUpgradePlan == null || _pendingRoadUpgradePlan.PlanId != plan.PlanId)
            {
                UpdateRoadUpgradeStatus("Road upgrades: preview expired");
                ResetRoadUpgradeReviewPage();
                return;
            }

            if (Manager.Instance != null && Manager.Instance.QueuePendingRouteRoadUpgradePlan(plan.PlanId))
            {
                _pendingRoadUpgradePlan = null;
                UpdateRoadUpgradeStatus("Road upgrades: queued for RON");
                TransitLogging.Log("Route road upgrades confirmed by player: groups=" + plan.Groups.Count + ", segments=" + plan.TotalSegmentCount + ".");
            }
            else
            {
                UpdateRoadUpgradeStatus("Road upgrades: preview expired");
            }

            ResetRoadUpgradeReviewPage();
        }

        private void CancelPendingRoadUpgradeReview()
        {
            BusRouteRoadUpgradePlan plan = _pendingRoadUpgradePlan;
            if (Manager.Instance != null && plan != null)
                Manager.Instance.ClearPendingRouteRoadUpgradePlan(plan.PlanId);

            _pendingRoadUpgradePlan = null;
            UpdateRoadUpgradeStatus("Road upgrades: cancelled");
            TransitLogging.Log("Route road upgrades cancelled by player after grouped RON preview.");
            ResetRoadUpgradeReviewPage();
        }

        private void ResetRoadUpgradeReviewPage()
        {
            _roadUpgradePlanDialogOpen = false;
            ClearRoadUpgradeBatchRows();

            ApplyDefaultRoadUpgradePageText();
            if (_roadUpgradeApplyButton != null)
                _roadUpgradeApplyButton.isVisible = false;
            if (_roadUpgradeCancelButton != null)
                _roadUpgradeCancelButton.isVisible = false;
        }

        private void ClearRoadUpgradeBatchRows()
        {
            for (int i = 0; i < _roadUpgradeReviewBatchLabels.Count; i++)
            {
                UILabel label = _roadUpgradeReviewBatchLabels[i];
                if (label != null)
                    UnityEngine.Object.Destroy(label.gameObject);
            }

            _roadUpgradeReviewBatchLabels.Clear();
        }

        private void OnCloseClicked(UIComponent c, UIMouseEventParameter p)
        {
            ClearOverviewGuidance();
            Hide();
        }

        private static LineDeleteMode[] GetLineDeleteModes()
        {
            return new[]
            {
                new LineDeleteMode("Bus", TransportInfo.TransportType.Bus),
                new LineDeleteMode("Trolleybus", TransportInfo.TransportType.Trolleybus),
                new LineDeleteMode("Tram", TransportInfo.TransportType.Tram),
                new LineDeleteMode("Metro", TransportInfo.TransportType.Metro),
                new LineDeleteMode("Train", TransportInfo.TransportType.Train),
                new LineDeleteMode("Monorail", TransportInfo.TransportType.Monorail),
                new LineDeleteMode("Cable Car", TransportInfo.TransportType.CableCar),
                new LineDeleteMode("Ship/Ferry", TransportInfo.TransportType.Ship),
                new LineDeleteMode("Airplane/Blimp", TransportInfo.TransportType.Airplane),
                new LineDeleteMode("Helicopter", TransportInfo.TransportType.Helicopter),
                new LineDeleteMode("Tourist Bus", TransportInfo.TransportType.TouristBus),
                new LineDeleteMode("Evacuation Bus", TransportInfo.TransportType.EvacuationBus),
                new LineDeleteMode("Walking Tour", TransportInfo.TransportType.Pedestrian),
                new LineDeleteMode("Hot Air Balloon", TransportInfo.TransportType.HotAirBalloon),
                new LineDeleteMode("Fishing", TransportInfo.TransportType.Fishing),
                new LineDeleteMode("Taxi", TransportInfo.TransportType.Taxi),
                new LineDeleteMode("Post", TransportInfo.TransportType.Post)
            };
        }

        private class LineDeleteMode
        {
            public readonly string DisplayName;
            public readonly TransportInfo.TransportType TransportType;

            public LineDeleteMode(string displayName, TransportInfo.TransportType transportType)
            {
                DisplayName = displayName;
                TransportType = transportType;
            }
        }
    }
}
