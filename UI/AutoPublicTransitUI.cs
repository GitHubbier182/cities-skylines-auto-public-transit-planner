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
        private UILabel _upgradeBusLaneSummary;
        private UILabel _scanStatus;
        private SummaryRow _scanSummaryRow;
        private SummaryRow _demandSummaryRow;
        private SummaryRow _lineActionsSummaryRow;
        private SummaryRow _generatedRouteSummaryRow;
        private SummaryRow _maintenanceSummaryRow;
        private SummaryRow _economicsSummaryRow;
        private SummaryRow _nextActionSummaryRow;
        private UILabel _roadUpgradeStatus;
        private UIPanel _navPanel;
        private UITextField _maxWalkingDistanceField;
        private UITextField _maxRoadDistanceField;
        private UITextField _maxLineLengthField;
        private UITextField _demandThresholdField;
        private UITextField _gridCellSizeField;
        private UITextField _minStopsPerRouteField;
        private UITextField _maxStopsPerRouteField;
        private UICheckBox _linkToOtherTransitCheckbox;
        private UICheckBox _quickScanModeCheckbox;
        private UILabel _busSettingsSafetyStatus;
        private bool _updatingSettingControls;
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
        private float _lastViewWidth = -1f;
        private float _lastViewHeight = -1f;

        public override void Start()
        {
            base.Start();

            Instance = this;

            name = "AutoPublicTransitUI";
            width = 920;
            height = 580;
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
            title.textScale = 1.0f;
            title.textColor = TextTint;
            title.relativePosition = new Vector3(10, 8);

            _closeButton = UIHelpers.AddButton(_titleBar, "X", OnCloseClicked);
            _closeButton.width = 32;
            _closeButton.height = 24;
            _closeButton.textScale = 0.8f;
            _closeButton.relativePosition = new Vector3(_titleBar.width - 40, 5);

            _navPanel = AddUIComponent<UIPanel>();
            _navPanel.width = 180;
            _navPanel.height = height - 58;
            _navPanel.relativePosition = new Vector3(12, 48);
            _navPanel.backgroundSprite = "GenericPanel";
            _navPanel.color = NavTint;

            AddTabButton("Overview", 8, 10, CreateOverviewPage());
            AddTabButton("Policy Options", 8, 48, CreatePolicyOptionsPage());
            AddTabButton("Bus Options", 8, 86, CreateBusOptionsPage());
            AddTabButton("Metro Options", 8, 124, CreateModeOptionsPage("Metro Options"));
            AddTabButton("Train Options", 8, 162, CreateModeOptionsPage("Train Options"));
            AddTabButton("Tram Options", 8, 200, CreateModeOptionsPage("Tram Options"));
            AddTabButton("Water Options", 8, 238, CreateModeOptionsPage("Water Options"));
            AddTabButton("Air Options", 8, 276, CreateModeOptionsPage("Air Options"));
            AddTabButton("Monorail Options", 8, 314, CreateModeOptionsPage("Monorail Options"));
            AddTabButton("Cable Car Options", 8, 352, CreateModeOptionsPage("Cable Car Options"));

            ShowTab(0);
        }

        public override void OnDestroy()
        {
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

            Instance.isVisible = !Instance.isVisible;
            if (Instance.isVisible)
            {
                Instance.ClampToView();
                Instance.BringToFront();
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

            string text = GetBusLaneSummaryText(count);
            if (Instance._upgradeBusLaneSummary != null)
                Instance._upgradeBusLaneSummary.text = text;
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

        public static void UpdateRoadUpgradeStatus(string text)
        {
            if (Instance == null || Instance._roadUpgradeStatus == null)
                return;

            Instance._roadUpgradeStatus.text = text;
        }

        public static void ShowDepotShortageDialog(BusEconomicsSummary economics)
        {
            if (economics == null)
                return;

            if (economics.DepotCount >= economics.RecommendedDepotCount)
                return;

            int missing = economics.RecommendedDepotCount - economics.DepotCount;
            string title = "Bus Depot Recommendation";
            string message = "APT recommends " + economics.RecommendedDepotCount + " bus " + Plural(economics.RecommendedDepotCount, "depot", "depots") +
                " for the current bus network.\n\n" +
                "Current depots: " + economics.DepotCount + "\n" +
                "Recommended depots: " + economics.RecommendedDepotCount + "\n" +
                "Place " + missing + " more bus " + Plural(missing, "depot", "depots") + ".";

            try
            {
                if (UIView.library != null)
                {
                    ExceptionPanel panel = UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel");
                    if (panel != null)
                    {
                        panel.SetMessage(title, message, false);
                        TransitLogging.Log("Displayed bus depot shortage advisory dialog: current=" + economics.DepotCount + ", recommended=" + economics.RecommendedDepotCount + ".");
                        return;
                    }
                }

                ConfirmPanel.ShowModal(title, message, null);
                TransitLogging.Log("Displayed fallback bus depot shortage advisory dialog: current=" + economics.DepotCount + ", recommended=" + economics.RecommendedDepotCount + ".");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to display bus depot shortage advisory dialog: " + e.Message);
            }
        }

        public static void ShowTransitVehicleSpawnDelayDialogIfNeeded(TransitScanSummary summary)
        {
            if (summary == null || summary.CreatedLines <= 0 || State.TransitVehicleSpawnDelayWarningShown)
                return;

            TransitVehicleSpawnDelayStatus status;
            if (!TransitVehicleSpawnDelayCompatibility.TryGetActiveStatus(out status) || status == null || !status.IsActive)
                return;

            if (!status.HasBusDelay || status.BusDelay <= 1u)
                return;

            State.TransitVehicleSpawnDelayWarningShown = true;
            int delaySeconds = status.BusDelay > int.MaxValue ? int.MaxValue : (int)status.BusDelay;
            string delayLine = "\n\nDetected bus delay setting: " + status.BusDelay + " normal-speed " + Plural(delaySeconds, "second", "seconds") + ".";
            string message =
                "APT created " + summary.CreatedLines + " new bus " + Plural(summary.CreatedLines, "line", "lines") + ", but Transit Vehicle Spawn Delay is active." +
                delayLine +
                "\n\nOpen Options > Transit Vehicle Spawn Delay and set Bus spawning delay to 1 or lower, or temporarily disable Transit Vehicle Spawn Delay, until the new APT services have spawned their first buses and are running normally." +
                "\n\nAfter the services stabilize, you can turn the delay back on.";

            try
            {
                if (UIView.library != null)
                {
                    ExceptionPanel panel = UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel");
                    if (panel != null)
                    {
                        panel.SetMessage("Bus Spawn Delay Detected", message, false);
                        TransitLogging.Warn("Displayed Transit Vehicle Spawn Delay advisory after creating " + summary.CreatedLines + " bus lines.");
                        return;
                    }
                }

                ConfirmPanel.ShowModal("Bus Spawn Delay Detected", message, null);
                TransitLogging.Warn("Displayed fallback Transit Vehicle Spawn Delay advisory after creating " + summary.CreatedLines + " bus lines.");
            }
            catch (Exception e)
            {
                State.TransitVehicleSpawnDelayWarningShown = false;
                TransitLogging.Warn("Failed to display Transit Vehicle Spawn Delay advisory: " + e.Message);
            }
        }

        private static string GetBusLaneSummaryText(int count)
        {
            if (!AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled)
                return "Road upgrades are in development.";

            if (count <= 0)
                return "Latest scan: no bus-lane upgrade recommendations.";

            return "Latest scan: " + count + " bus-lane upgrade " + Plural(count, "recommendation", "recommendations") + ". Review before applying.";
        }

        private void ShowScanStatus(string text)
        {
            SetLabelText(_scanStatus, text);
            if (text == null)
                return;

            if (text.IndexOf("queued", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("running", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SetSummaryRow(_scanSummaryRow, "Scan", text, "In progress", "Panel updates after completion.");
                SetSummaryRow(_demandSummaryRow, "Demand", "Waiting", "Buildings and stops", "Demand nodes pending.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Waiting", "Line changes pending", "Built/deleted counts pending.");
                SetSummaryRow(_generatedRouteSummaryRow, "Build", "Waiting", "Routes pending", "Built/trimmed counts pending.");
                SetSummaryRow(_maintenanceSummaryRow, "Utilisation", "Waiting", "Line changes pending", "Built/deleted counts pending.");
                SetSummaryRow(_economicsSummaryRow, "Economics", "Waiting", "Lines and depots pending", "Policy notes pending.");
                SetSummaryRow(_nextActionSummaryRow, "Next", "Wait", "Scan running", "No action until complete.");
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
                    SetSummaryRow(_demandSummaryRow, "Demand", "Unavailable", "Run scan", "Detailed demand summary not captured.");
                    SetSummaryRow(_lineActionsSummaryRow, "Lines", "Unavailable", "Run scan", "Line changes not captured.");
                    SetSummaryRow(_generatedRouteSummaryRow, "Build", "Unavailable", "Run scan", "Build changes not captured.");
                    SetSummaryRow(_maintenanceSummaryRow, "Utilisation", "Unavailable", "Run scan", "Utilisation detail not captured.");
                    SetSummaryRow(_economicsSummaryRow, "Economics", "Unavailable", "Run scan", "Advisory not captured.");
                    SetSummaryRow(_nextActionSummaryRow, "Next", "Refresh", "Run scan", "This table will populate afterward.");
                    return;
                }

                SetLabelText(_scanStatus, "Last scan: not run yet");
                SetSummaryRow(_scanSummaryRow, "Scan", "Not run", "No changes", "Run Scan, Build & Apply.");
                SetSummaryRow(_demandSummaryRow, "Demand", "Pending", "Buildings -> stops", "Demand summary will appear.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Pending", "Built/deleted", "Line changes will appear.");
                SetSummaryRow(_generatedRouteSummaryRow, "Build", "Pending", "Built/trimmed", "Build changes will appear.");
                SetSummaryRow(_maintenanceSummaryRow, "Utilisation", "Pending", "Built/deleted", "Line changes will appear.");
                SetSummaryRow(_economicsSummaryRow, "Economics", "Pending", "Lines and depots", "Break-even and policy notes will appear.");
                SetSummaryRow(_nextActionSummaryRow, "Next", "Scan", "Build & Apply", "Results will populate this table.");
                return;
            }

            if (!string.IsNullOrEmpty(summary.FailureMessage))
            {
                SetLabelText(_scanStatus, "Last scan: failed");
                SetSummaryRow(_scanSummaryRow, "Scan", "Failed", "No safe changes", TrimForUi(summary.FailureMessage, 90));
                SetSummaryRow(_demandSummaryRow, "Demand", "Failed", "Unavailable", "Check APT log.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Unknown", "No reliable summary", "Scan failed before completion.");
                SetSummaryRow(_generatedRouteSummaryRow, "Build", "Unknown", "No reliable summary", "Scan failed before completion.");
                SetSummaryRow(_maintenanceSummaryRow, "Utilisation", "Unknown", "No reliable summary", "Scan failed before completion.");
                SetSummaryRow(_economicsSummaryRow, "Economics", "Unknown", "No reliable summary", "Scan failed before completion.");
                SetSummaryRow(_nextActionSummaryRow, "Next", "Fix log issue", "Rerun scan", "Do not trust partial results.");
                return;
            }

            if (!summary.Completed)
            {
                SetLabelText(_scanStatus, "Last scan: running");
                SetSummaryRow(_scanSummaryRow, "Scan", "Running", "Checking city", "Panel updates after completion.");
                SetSummaryRow(_demandSummaryRow, "Demand", "Running", "Buildings and stops", "Demand nodes pending.");
                SetSummaryRow(_lineActionsSummaryRow, "Lines", "Running", "Line changes", "Built/deleted counts pending.");
                SetSummaryRow(_generatedRouteSummaryRow, "Build", "Waiting", "Demand routes pending", "Built/trimmed counts pending.");
                SetSummaryRow(_maintenanceSummaryRow, "Utilisation", "Running", "Ridership checks", "Pruning/protection pending.");
                SetSummaryRow(_economicsSummaryRow, "Economics", "Waiting", "Complete lines pending", "Policy notes pending.");
                SetSummaryRow(_nextActionSummaryRow, "Next", "Wait", "Scan running", "No action until complete.");
                return;
            }

            SetLabelText(
                _scanStatus,
                "Last scan: complete in " + summary.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) +
                "s (" + (summary.QuickScanMode ? "sampled demand" : "full demand") + ", stride " + summary.QuickScanStride + ")");
            SetSummaryRow(_scanSummaryRow, "Scan", "Complete", summary.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s", (summary.QuickScanMode ? "Sampled demand" : "Full demand") + ", stride " + summary.QuickScanStride + ".");
            SetSummaryRow(_demandSummaryRow, "Demand", summary.ScannedBuildings + "/" + summary.EligibleBuildings + " buildings", summary.ValidStopCandidates + " stops, " + summary.DemandNodeCount + " nodes", GetDemandNotes(summary));
            string[] lineCells = GetLineActionCells(summary);
            SetSummaryRow(_lineActionsSummaryRow, "Lines", lineCells[0], lineCells[1], lineCells[2]);
            string[] buildCells = GetGeneratedRouteCells(summary);
            SetSummaryRow(_generatedRouteSummaryRow, "Build", buildCells[0], buildCells[1], buildCells[2]);
            string[] maintenanceCells = GetMaintenanceCells(summary);
            SetSummaryRow(_maintenanceSummaryRow, "Utilisation", maintenanceCells[0], maintenanceCells[1], maintenanceCells[2]);
            string[] economicsCells = GetEconomicsCells(summary.BusEconomicsSummary);
            SetSummaryRow(_economicsSummaryRow, "Economics", economicsCells[0], economicsCells[1], economicsCells[2]);
            string[] nextCells = GetNextActionCells(summary);
            SetSummaryRow(_nextActionSummaryRow, "Next", nextCells[0], nextCells[1], nextCells[2]);
            SetLabelText(_upgradeBusLaneSummary, GetBusLaneSummaryText(summary.BusLaneRecommendationCount));
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

        private static string[] GetLineActionCells(TransitScanSummary summary)
        {
            int totalRemoved = summary.LinesRemoved + summary.WeakDuplicateLinesRetired;
            return new[]
            {
                summary.CreatedLines + " built",
                totalRemoved + " deleted",
                GetLineChangeNotes(summary, totalRemoved)
            };
        }

        private static string[] GetGeneratedRouteCells(TransitScanSummary summary)
        {
            return new[]
            {
                summary.CreatedLines + " built",
                GetTrimmedLineText(summary),
                GetBuildNotes(summary)
            };
        }

        private static string GetDemandNotes(TransitScanSummary summary)
        {
            string strategic = summary.DemandStrategicNodeCount > 0
                ? summary.DemandStrategicNodeCount + " hub/visitor " + Plural(summary.DemandStrategicNodeCount, "anchor", "anchors")
                : "local demand only";
            return strategic + ".";
        }

        private static string GetBuildNotes(TransitScanSummary summary)
        {
            if (summary.CreatedLines <= 0 && summary.RepairedGeneratedLines <= 0 && summary.LinesRemoved <= 0)
                return "No line changes applied.";

            if (summary.RepairedGeneratedLines > 0)
                return summary.RepairedGeneratedLines + " " + Plural(summary.RepairedGeneratedLines, "line", "lines") + " trimmed before build.";

            if (summary.CreatedLines > 0)
                return "Built lines are ready to settle.";

            return "No build changes applied.";
        }

        private static string[] GetMaintenanceCells(TransitScanSummary summary)
        {
            string change = summary.LinesRemoved > 0
                ? summary.LinesRemoved + " deleted"
                : "No deletions";
            return new[]
            {
                summary.RepairedGeneratedLines + " trimmed",
                change,
                GetLineChangeNotes(summary, summary.LinesRemoved)
            };
        }

        private static string GetTrimmedLineText(TransitScanSummary summary)
        {
            int trimmed = summary.RepairedGeneratedLines;
            if (trimmed <= 0)
                return "0 trimmed";

            return trimmed + " trimmed";
        }

        private static string GetLineChangeNotes(TransitScanSummary summary, int totalRemoved)
        {
            if (summary.CreatedLines > 0 && totalRemoved > 0)
                return summary.CreatedLines + " built, " + totalRemoved + " deleted.";

            if (summary.CreatedLines > 0)
                return summary.CreatedLines + " built.";

            if (summary.RepairedGeneratedLines > 0)
                return summary.RepairedGeneratedLines + " trimmed.";

            if (totalRemoved > 0)
                return totalRemoved + " deleted.";

            return "No line changes applied.";
        }

        private static string[] GetEconomicsCells(BusEconomicsSummary economics)
        {
            if (economics == null)
            {
                return new[]
                {
                    "Unavailable",
                    "No advisory",
                    "Run scan to populate economics."
                };
            }

            string contribution = economics.EstimatedNetworkContribution >= 0f
                ? "+" + economics.EstimatedNetworkContribution.ToString("0", CultureInfo.InvariantCulture)
                : economics.EstimatedNetworkContribution.ToString("0", CultureInfo.InvariantCulture);
            string depotNote = string.IsNullOrEmpty(economics.DepotSufficiencyNote)
                ? "Depot guidance unavailable."
                : economics.DepotSufficiencyNote;
            string fareNote = economics.FareRevenueIgnoredLineCount > 0
                ? " Free fares make profit estimates less certain."
                : "";

            return new[]
            {
                economics.UsefulLineCount + "/" + economics.CompleteLineCount + " useful lines",
                GetDepotSummaryText(economics),
                TrimForUi(depotNote + " Net " + contribution + "." + fareNote, 84)
            };
        }

        private static string GetDepotSummaryText(BusEconomicsSummary economics)
        {
            if (economics == null)
                return "No depot advisory";

            string buses = economics.VehicleCount + " " + Plural(economics.VehicleCount, "bus", "buses");
            if (economics.RecommendedDepotCount <= 0)
                return economics.DepotCount + " " + Plural(economics.DepotCount, "depot", "depots") + ", " + buses;

            if (economics.DepotCount < economics.RecommendedDepotCount)
                return "Add " + economics.DepotCountDifference + " " + Plural(economics.DepotCountDifference, "depot", "depots") + ", " + buses;

            if (economics.DepotCount > economics.RecommendedDepotCount)
                return "Target " + economics.RecommendedDepotCount + " " + Plural(economics.RecommendedDepotCount, "depot", "depots") + ", " + buses;

            return economics.DepotCount + " " + Plural(economics.DepotCount, "depot", "depots") + " ok, " + buses;
        }

        private static string[] GetNextActionCells(TransitScanSummary summary)
        {
            if (summary.CreatedLines > 0 || summary.LinesRemoved > 0 || summary.RepairedGeneratedLines > 0)
            {
                return new[]
                {
                    "Let settle",
                    GetNextChangeText(summary),
                    "Rerun after major road or zoning changes."
                };
            }

            if (AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled && summary.BusLaneRecommendationCount > 0)
            {
                return new[]
                {
                    "Review Upgrades",
                    summary.BusLaneRecommendationCount + " recommended " + Plural(summary.BusLaneRecommendationCount, "road", "roads"),
                    "Open Upgrades and apply only after reviewing the list."
                };
            }

            return new[]
            {
                "No action",
                "Routine maintenance",
                "No bus-lane recommendations queued."
            };
        }

        private static string GetNextChangeText(TransitScanSummary summary)
        {
            string text = summary.CreatedLines + " built";
            if (summary.RepairedGeneratedLines > 0)
                text += ", " + summary.RepairedGeneratedLines + " trimmed";
            if (summary.LinesRemoved > 0)
                text += ", " + summary.LinesRemoved + " deleted";
            return text;
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
            page.width = width - 220;
            page.height = height - 68;
            page.relativePosition = new Vector3(204, 52);
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
            tab.height = 31;
            tab.textScale = text.Length > 13 ? 0.64f : text.Length > 10 ? 0.68f : 0.74f;
            tab.relativePosition = new Vector3(x, y);
            tab.tooltip = text;
            _tabButtons.Add(tab);
        }

        private static float GetTabWidth(string text)
        {
            return 164f;
        }

        private void ShowTab(int index)
        {
            for (int i = 0; i < _tabPages.Count; i++)
            {
                bool selected = i == index;
                _tabPages[i].isVisible = selected;
                _tabButtons[i].normalBgSprite = selected ? "ButtonMenuPressed" : "ButtonMenu";
            }
        }

        private UIPanel CreateOverviewPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Overview", 0, 0);

            _scanButton = UIHelpers.AddButton(page, "Scan, Build & Apply", OnScanClicked);
            _scanButton.width = 224;
            _scanButton.height = 40;
            _scanButton.textScale = 0.84f;
            _scanButton.relativePosition = new Vector3(0, 38);

            _scanStatus = AddFixedLabel(page, "", 236, 45, 448, 28, 0.76f);
            _scanSummaryRow = AddSummaryCard(page, "Scan", 0, 88, 700, 70, true);
            _demandSummaryRow = AddSummaryCard(page, "Demand", 0, 168, 342, 78, false);
            _lineActionsSummaryRow = AddSummaryCard(page, "Lines", 358, 168, 342, 78, false);
            _generatedRouteSummaryRow = AddSummaryCard(page, "Build", 0, 254, 342, 78, false);
            _maintenanceSummaryRow = AddSummaryCard(page, "Utilisation", 358, 254, 342, 78, false);
            _economicsSummaryRow = AddSummaryCard(page, "Economics", 0, 340, 342, 78, false);
            _nextActionSummaryRow = AddSummaryCard(page, "Next", 358, 340, 342, 78, false);

            ApplyScanSummary(State.LastScanSummary);
            return page;
        }

        private UIPanel CreateBusOptionsPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Bus Options", 0, 0);

            UIPanel routeCard = AddOptionCard(page, "Route Planning", 0, 42, 336, 348);
            UIPanel upgradeCard = AddOptionCard(page, "Road Upgrades", 364, 42, 336, 150);

            AddNumberField(
                routeCard,
                "Max Walking Distance",
                "How far a building can be from a bus stop.",
                ConfigManager.Config.MaxWalkingDistance,
                v =>
            {
                ConfigManager.Config.MaxWalkingDistance = v;
                SaveBusPlanningSettingChange();
            },
                out _maxWalkingDistanceField).relativePosition = new Vector3(16, 46);

            AddNumberField(
                routeCard,
                "Max Road Distance",
                "Maximum spacing between generated stops in one route.",
                ConfigManager.Config.MaxRoadDistance,
                v =>
            {
                ConfigManager.Config.MaxRoadDistance = v;
                SaveBusPlanningSettingChange();
            },
                out _maxRoadDistanceField).relativePosition = new Vector3(16, 82);

            AddNumberField(
                routeCard,
                "Max Line Length (km)",
                "Upper length cap for a generated or evaluated bus line.",
                ConfigManager.Config.MaxLineLengthKm,
                v =>
            {
                ConfigManager.Config.MaxLineLengthKm = Mathf.Clamp(v, 1f, 50f);
                SaveBusPlanningSettingChange();
            },
                out _maxLineLengthField).relativePosition = new Vector3(16, 118);

            AddNumberField(
                routeCard,
                "Demand Threshold",
                "Minimum aggregated demand required before a stop is kept.",
                ConfigManager.Config.DemandThreshold,
                v =>
            {
                ConfigManager.Config.DemandThreshold = (int)v;
                SaveBusPlanningSettingChange();
            },
                out _demandThresholdField).relativePosition = new Vector3(16, 154);

            AddNumberField(
                routeCard,
                "Grid Cell Size",
                "Demand clustering size used to merge nearby buildings.",
                ConfigManager.Config.GridCellSize,
                v =>
            {
                ConfigManager.Config.GridCellSize = v;
                SaveBusPlanningSettingChange();
            },
                out _gridCellSizeField).relativePosition = new Vector3(16, 190);

            AddNumberField(
                routeCard,
                "Min Stops Per Route",
                "Routes smaller than this are discarded.",
                ConfigManager.Config.MinStopsPerRoute,
                v =>
            {
                ConfigManager.Config.MinStopsPerRoute = (int)v;
                SaveBusPlanningSettingChange();
            },
                out _minStopsPerRouteField).relativePosition = new Vector3(16, 226);

            AddNumberField(
                routeCard,
                "Max Stops Per Route",
                "Upper limit for stops generated into a single route.",
                ConfigManager.Config.MaxStopsPerRoute,
                v =>
            {
                ConfigManager.Config.MaxStopsPerRoute = (int)v;
                SaveBusPlanningSettingChange();
            },
                out _maxStopsPerRouteField).relativePosition = new Vector3(16, 262);

            UIButton resetButton = UIHelpers.AddButton(routeCard, "Reset Defaults", OnResetBusDefaultsClicked);
            resetButton.width = 128;
            resetButton.height = 28;
            resetButton.textScale = 0.7f;
            resetButton.relativePosition = new Vector3(16, 306);

            _busSettingsSafetyStatus = AddFixedLabel(routeCard, "", 156, 308, 160, 30, 0.58f);
            _busSettingsSafetyStatus.textColor = MutedTextTint;
            UpdateBusSettingsSafetyStatus();

            _upgradeBusLaneSummary = AddFixedLabel(upgradeCard, "In development", 16, 50, 292, 26, 0.78f);
            _upgradeBusLaneSummary.textColor = TextTint;
            _upgradeBusLaneSummary.wordWrap = false;

            _roadUpgradeStatus = AddFixedLabel(upgradeCard, "Bus-lane road upgrades are not active in this release.", 16, 84, 292, 42, 0.64f);
            _roadUpgradeStatus.textColor = MutedTextTint;
            return page;
        }

        private UIPanel CreatePolicyOptionsPage()
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, "Policy Options", 0, 0);

            UIPanel policyCard = AddOptionCard(page, "Scan & Linking", 0, 42, 700, 230);

            AddCheckBox(
                policyCard,
                "Link To Other Transit",
                "Bias bus planning toward metro, rail, ferry, tram and other public-transport hubs.",
                ConfigManager.Config.LinkToOtherTransit,
                value =>
            {
                if (_updatingSettingControls)
                    return;

                ConfigManager.Config.LinkToOtherTransit = value;
                SaveBusPlanningSettingChange();
            },
                out _linkToOtherTransitCheckbox).relativePosition = new Vector3(16, 48);

            UILabel linkDescription = AddFixedLabel(policyCard, "Prioritises transport hubs, tourist anchors, and high-demand areas.", 44, 78, 620, 24, 0.62f);
            linkDescription.textColor = MutedTextTint;
            linkDescription.wordWrap = false;

            AddCheckBox(
                policyCard,
                "Sample Demand Pass",
                "Samples eligible demand buildings before route building.",
                ConfigManager.Config.QuickScanMode,
                value =>
            {
                if (_updatingSettingControls)
                    return;

                ConfigManager.Config.QuickScanMode = value;
                SaveBusPlanningSettingChange();
            },
                out _quickScanModeCheckbox).relativePosition = new Vector3(16, 124);

            UILabel quickDescription = AddFixedLabel(policyCard, "Limits demand sampling; route building can still dominate total scan time.", 44, 154, 620, 24, 0.62f);
            quickDescription.textColor = MutedTextTint;
            quickDescription.wordWrap = false;
            return page;
        }

        private UIPanel CreateModeOptionsPage(string title)
        {
            UIPanel page = CreateTabPage();
            AddSectionLabel(page, title, 0, 0);
            AddModeStatusCard(page, title);
            return page;
        }

        private void AddModeStatusCard(UIComponent parent, string title)
        {
            UIPanel card = AddOptionCard(parent, "Mode Status", 0, 42, 700, 116);

            UILabel status = AddFixedLabel(card, "In development", 16, 50, 660, 34, 0.78f);
            status.textColor = TextTint;
            status.wordWrap = false;

            string mode = title.Replace(" Options", "");
            UILabel note = AddFixedLabel(card, mode + " automation is under development and not active yet.", 16, 84, 660, 28, 0.68f);
            note.textColor = MutedTextTint;
        }

        private void SaveBusPlanningSettingChange()
        {
            ConfigManager.Save();
            UpdateBusSettingsSafetyStatus();
        }

        private void ResetBusPlanningDefaults()
        {
            ConfigManager.Config.ResetActiveBusPlanningSettings();
            ConfigManager.Save();
            RefreshBusPlanningControls();
            UpdateBusSettingsSafetyStatus();

            if (_scanStatus != null)
                _scanStatus.text = "Bus settings reset to defaults";

            TransitLogging.Log("Reset active bus planning settings to defaults.");
        }

        private void RefreshBusPlanningControls()
        {
            _updatingSettingControls = true;
            try
            {
                SetFieldText(_maxWalkingDistanceField, ConfigManager.Config.MaxWalkingDistance);
                SetFieldText(_maxRoadDistanceField, ConfigManager.Config.MaxRoadDistance);
                SetFieldText(_maxLineLengthField, ConfigManager.Config.MaxLineLengthKm);
                SetFieldText(_demandThresholdField, ConfigManager.Config.DemandThreshold);
                SetFieldText(_gridCellSizeField, ConfigManager.Config.GridCellSize);
                SetFieldText(_minStopsPerRouteField, ConfigManager.Config.MinStopsPerRoute);
                SetFieldText(_maxStopsPerRouteField, ConfigManager.Config.MaxStopsPerRoute);

                if (_linkToOtherTransitCheckbox != null)
                    _linkToOtherTransitCheckbox.isChecked = ConfigManager.Config.LinkToOtherTransit;

                if (_quickScanModeCheckbox != null)
                    _quickScanModeCheckbox.isChecked = ConfigManager.Config.QuickScanMode;
            }
            finally
            {
                _updatingSettingControls = false;
            }
        }

        private static void SetFieldText(UITextField field, float value)
        {
            if (field != null)
                field.text = value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void UpdateBusSettingsSafetyStatus()
        {
            if (_busSettingsSafetyStatus == null)
                return;

            string reason;
            if (ConfigManager.Config.HasRiskyBusPlanningSettings(out reason))
            {
                _busSettingsSafetyStatus.text = "Scan requires confirmation";
                _busSettingsSafetyStatus.textColor = new Color32(255, 218, 132, 255);
                _busSettingsSafetyStatus.tooltip = reason;
                return;
            }

            _busSettingsSafetyStatus.text = "Scan settings normal";
            _busSettingsSafetyStatus.textColor = MutedTextTint;
            _busSettingsSafetyStatus.tooltip = "";
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
            headerBand.height = 36;
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
            UILabel label = AddFixedLabel(parent, text, x, y, 500, 30, 0.92f);
            label.textColor = TextTint;
            return label;
        }

        private void AddSummaryHeader(UIComponent parent, float y)
        {
            AddFixedLabel(parent, "Area", 0, y, 92, 22, 0.58f);
            AddFixedLabel(parent, "Result", 100, y, 170, 22, 0.58f);
            AddFixedLabel(parent, "Changed", 278, y, 200, 22, 0.58f);
            AddFixedLabel(parent, "Notes", 486, y, 292, 22, 0.58f);
        }

        private SummaryRow AddSummaryRow(UIComponent parent, string area, float y)
        {
            var row = new SummaryRow();
            row.Area = AddFixedLabel(parent, area, 0, y, 92, 30, 0.56f);
            row.Result = AddFixedLabel(parent, "", 100, y, 170, 30, 0.56f);
            row.Changed = AddFixedLabel(parent, "", 278, y, 200, 30, 0.56f);
            row.Notes = AddFixedLabel(parent, "", 486, y, 292, 30, 0.56f);
            return row;
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
            row.MaxResultLength = wide ? 70 : 30;
            row.MaxChangedLength = wide ? 70 : 32;
            row.MaxNotesLength = wide ? 108 : 72;
            row.Area = AddFixedLabel(card, area, 12, 7, cardWidth - 24, 20, wide ? 0.7f : 0.68f);
            row.Area.textColor = TextTint;
            row.Result = AddFixedLabel(card, "", 12, 30, (cardWidth - 32) * 0.5f, 22, wide ? 0.68f : 0.66f);
            row.Result.textColor = TextTint;
            row.Changed = AddFixedLabel(card, "", 20 + (cardWidth - 32) * 0.5f, 30, (cardWidth - 32) * 0.5f, 22, wide ? 0.68f : 0.66f);
            row.Changed.textColor = TextTint;
            row.Notes = AddFixedLabel(card, "", 12, 54, cardWidth - 24, cardHeight - 58, wide ? 0.62f : 0.6f);
            row.Notes.textColor = MutedTextTint;
            return row;
        }

        private UILabel AddFixedLabel(UIComponent parent, string text, float x, float y, float labelWidth, float labelHeight, float textScale)
        {
            UILabel label = parent.AddUIComponent<UILabel>();
            label.text = text;
            label.width = labelWidth;
            label.height = labelHeight;
            label.textScale = textScale;
            label.textColor = TextTint;
            label.autoSize = false;
            label.autoHeight = false;
            label.wordWrap = true;
            label.relativePosition = new Vector3(x, y);
            return label;
        }

        private UIPanel AddNumberField(UIComponent parent, string label, string tooltip, float initial, Action<float> onChanged)
        {
            UITextField ignored;
            return AddNumberField(parent, label, tooltip, initial, onChanged, out ignored);
        }

        private UIPanel AddNumberField(UIComponent parent, string label, string tooltip, float initial, Action<float> onChanged, out UITextField field)
        {
            UIPanel row = parent.AddUIComponent<UIPanel>();
            row.width = 304;
            row.height = 28;
            row.tooltip = tooltip;

            UILabel lbl = row.AddUIComponent<UILabel>();
            lbl.text = label;
            lbl.textScale = 0.72f;
            lbl.width = 208;
            lbl.relativePosition = new Vector3(0, 5);
            lbl.tooltip = tooltip;
            lbl.wordWrap = false;
            lbl.autoHeight = false;

            UITextField textField = row.AddUIComponent<UITextField>();
            field = textField;
            textField.width = 84;
            textField.height = 22;
            textField.numericalOnly = true;
            textField.allowFloats = true;
            textField.readOnly = false;
            textField.submitOnFocusLost = true;
            textField.builtinKeyNavigation = true;
            textField.selectionSprite = "EmptySprite";
            textField.canFocus = true;
            textField.isInteractive = true;
            textField.text = initial.ToString("0.##", CultureInfo.InvariantCulture);
            textField.textScale = 0.8f;
            textField.padding = new RectOffset(4, 4, 4, 4);
            textField.normalBgSprite = "TextFieldPanel";
            textField.hoveredBgSprite = "TextFieldPanelHovered";
            textField.focusedBgSprite = "TextFieldPanel";
            textField.tooltip = tooltip;
            Action<string> applyValue = value =>
            {
                float v;
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    if (!float.TryParse(value, out v))
                        return;
                }

                onChanged(v);
                textField.text = v.ToString("0.##", CultureInfo.InvariantCulture);
            };

            textField.eventTextSubmitted += (c, value) => applyValue(value);
            textField.eventLostFocus += (c, p) => applyValue(textField.text);

            textField.relativePosition = new Vector3(216, 2);

            return row;
        }

        private UIPanel AddCheckBox(UIComponent parent, string label, string tooltip, bool initial, Action<bool> onChanged)
        {
            UICheckBox ignored;
            return AddCheckBox(parent, label, tooltip, initial, onChanged, out ignored);
        }

        private UIPanel AddCheckBox(UIComponent parent, string label, string tooltip, bool initial, Action<bool> onChanged, out UICheckBox checkbox)
        {
            UIPanel row = parent.AddUIComponent<UIPanel>();
            row.width = 304;
            row.height = 28;
            row.tooltip = tooltip;

            checkbox = row.AddUIComponent<UICheckBox>();
            checkbox.width = 22;
            checkbox.height = 22;
            checkbox.relativePosition = new Vector3(0, 2);
            checkbox.tooltip = tooltip;

            UISprite uncheckedSprite = checkbox.AddUIComponent<UISprite>();
            uncheckedSprite.spriteName = "check-unchecked";
            uncheckedSprite.size = new Vector2(16f, 16f);
            uncheckedSprite.relativePosition = new Vector3(2, 3);

            UISprite checkedSprite = uncheckedSprite.AddUIComponent<UISprite>();
            checkedSprite.spriteName = "check-checked";
            checkedSprite.size = uncheckedSprite.size;
            checkedSprite.relativePosition = Vector3.zero;
            checkbox.checkedBoxObject = checkedSprite;
            checkbox.isChecked = initial;

            UILabel lbl = row.AddUIComponent<UILabel>();
            lbl.text = label;
            lbl.textScale = 0.72f;
            lbl.width = 272;
            lbl.relativePosition = new Vector3(28, 4);
            lbl.tooltip = tooltip;
            lbl.wordWrap = false;

            checkbox.eventCheckChanged += (c, value) => onChanged(value);
            return row;
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

        private void OnResetBusDefaultsClicked(UIComponent c, UIMouseEventParameter p)
        {
            ResetBusPlanningDefaults();
        }

        private void OnRoadUpgradesClicked(UIComponent c, UIMouseEventParameter p)
        {
            if (!AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled)
            {
                TransitLogging.Log("Road upgrades button ignored because bus-lane road upgrades are in development.");
                UpdateRoadUpgradeStatus("Road upgrades: in development");
                return;
            }

            TransitLogging.Log("Apply Road Upgrades button pressed.");

            if (Manager.Instance == null)
            {
                TransitLogging.Error("Manager instance was not ready when road upgrades were pressed.");
                return;
            }

            if (_roadUpgradeStatus != null)
                _roadUpgradeStatus.text = "Road upgrades: queued";
            AutoPublicTransitThreading.RunRoadUpgrades = true;
        }

        private void OnCloseClicked(UIComponent c, UIMouseEventParameter p)
        {
            Hide();
        }
    }
}
