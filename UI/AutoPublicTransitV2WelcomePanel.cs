using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace AutoPublicTransit
{
    public class AutoPublicTransitV2WelcomePanel : UIPanel
    {
        private const string PanelName = "AutoPublicTransitV2WelcomePanel";
        private const string NoticeId = "apt-v2-welcome";
        private static AutoPublicTransitV2WelcomePanel Instance;

        private static readonly Color32 PanelTint = new Color32(48, 64, 70, 250);
        private static readonly Color32 TitleTint = new Color32(104, 144, 154, 255);
        private static readonly Color32 CardTint = new Color32(72, 94, 100, 250);
        private static readonly Color32 LogoFrameTint = new Color32(42, 54, 59, 255);
        private static readonly Color32 AccentTint = new Color32(132, 222, 206, 255);
        private static readonly Color32 TextTint = new Color32(235, 242, 242, 255);
        private static readonly Color32 MutedTextTint = new Color32(205, 221, 222, 255);
        private static readonly Color32 ScrollTrackTint = new Color32(36, 49, 54, 230);
        private static readonly Color32 ScrollThumbTint = new Color32(144, 184, 190, 255);
        private const float TitleTextScale = 1.22f;
        private const float SubtitleTextScale = 0.78f;
        private const float BodyTextScale = 0.92f;
        private const float ButtonTextScale = 0.94f;
        private const string ReleaseIntro = "APT v2 is a major bus-planning rewrite. The short version is:";
        private static readonly string[] ReleaseBullets =
        {
            "Rebuilt demand and coverage scanning for full-city bus planning.",
            "Quick Scan is gone because the whole-city scanner is more responsive, so a separate quick mode is no longer needed.",
            "Existing usable bus service is treated more conservatively during scans.",
            "DND protection: keep the line's normal name and add DND as a separate extra word, such as Heritage Loop DND. APT will leave that line alone while still counting its stops as coverage.",
            "School bus lines are protected when the SchoolBuses mod is used, and their stops can count as coverage.",
            "Generated bus lines avoid coach/intercity-style vehicle models where an ordinary city bus is available.",
            "The old Bus Options and Policy Options tweak pages are hidden for now. APT v2 uses locked internal planning values while the rewrite settles.",
            "Line Tools cleanup is safer, but older v1-created APT lines may need one manual cleanup.",
            "Bus-lane road upgrades and non-bus transit automation are still in development."
        };

        public static void ShowIfNeeded(UIView view)
        {
            if (view == null || Instance != null)
                return;

            ConfigManager.EnsureLoaded();
            if (ConfigManager.Config == null)
                return;

            if (string.Equals(ConfigManager.Config.ShownReleaseNoticeId, NoticeId, StringComparison.Ordinal))
                return;

            AutoPublicTransitV2WelcomePanel panel = view.AddUIComponent(typeof(AutoPublicTransitV2WelcomePanel)) as AutoPublicTransitV2WelcomePanel;
            if (panel == null)
                return;

            ConfigManager.Config.ShownReleaseNoticeId = NoticeId;
            ConfigManager.Save();
            TransitLogging.Log("Displayed one-time APT v2 welcome notice.");
        }

        public override void Start()
        {
            base.Start();

            Instance = this;
            name = PanelName;
            ApplyResponsiveSize();
            backgroundSprite = "MenuPanel2";
            color = PanelTint;
            canFocus = true;
            isInteractive = true;
            CenterInView();

            UIPanel titleBar = AddUIComponent<UIPanel>();
            titleBar.width = width - 12;
            titleBar.height = 78;
            titleBar.relativePosition = new Vector3(6, 6);
            titleBar.backgroundSprite = "GenericPanel";
            titleBar.color = TitleTint;

            UIDragHandle dragHandle = titleBar.AddUIComponent<UIDragHandle>();
            dragHandle.width = titleBar.width;
            dragHandle.height = titleBar.height;
            dragHandle.relativePosition = Vector3.zero;
            dragHandle.target = this;

            AddLogo(titleBar, 14, 10, 58);
            AddBoldLabel(titleBar, "Welcome to Version 2 of APT!", 88, 12, titleBar.width - 148, 30, TitleTextScale, TextTint, false);
            AddLabel(titleBar, "A quick tour of the bus-planning rewrite", 90, 46, titleBar.width - 150, 24, SubtitleTextScale, MutedTextTint, false);

            UIButton closeButton = UIHelpers.AddButton(titleBar, "X", (component, param) => Close(false));
            closeButton.width = 32;
            closeButton.height = 28;
            closeButton.textScale = 0.9f;
            closeButton.relativePosition = new Vector3(titleBar.width - 42, 12);

            UIPanel body = AddUIComponent<UIPanel>();
            body.width = width - 32;
            body.height = height - 174;
            body.relativePosition = new Vector3(16, 104);
            body.backgroundSprite = "GenericPanel";
            body.color = CardTint;

            float scrollTop = 18f;
            float scrollHeight = Mathf.Max(120f, body.height - 36f);
            float scrollbarWidth = 14f;
            UIScrollablePanel scrollPanel = body.AddUIComponent<UIScrollablePanel>();
            scrollPanel.width = body.width - 58f;
            scrollPanel.height = scrollHeight;
            scrollPanel.relativePosition = new Vector3(20f, scrollTop);
            scrollPanel.clipChildren = true;
            scrollPanel.autoLayout = false;
            scrollPanel.canFocus = true;
            scrollPanel.scrollWheelAmount = 48;
            scrollPanel.scrollWheelDirection = UIOrientation.Vertical;
            scrollPanel.backgroundSprite = null;

            PopulateScrollContent(scrollPanel, scrollPanel.width - 8f);

            UIScrollbar scrollbar = AddVerticalScrollbar(body, body.width - scrollbarWidth - 14f, scrollTop, scrollHeight, scrollbarWidth);
            scrollPanel.verticalScrollbar = scrollbar;

            UIButton openButton = UIHelpers.AddButton(this, "Open APT", (component, param) => Close(true));
            openButton.width = 160;
            openButton.height = 36;
            openButton.textScale = ButtonTextScale;
            openButton.relativePosition = new Vector3(width - 338, height - 58);

            UIButton gotItButton = UIHelpers.AddButton(this, "Got it", (component, param) => Close(false));
            gotItButton.width = 150;
            gotItButton.height = 36;
            gotItButton.textScale = ButtonTextScale;
            gotItButton.relativePosition = new Vector3(width - 168, height - 58);

            BringToFront();
        }

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            base.OnDestroy();
        }

        public static void DestroyInstance()
        {
            if (Instance == null)
                return;

            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        private void Close(bool openApt)
        {
            if (openApt && AutoPublicTransitUI.Instance != null)
            {
                if (!AutoPublicTransitUI.Instance.isVisible)
                    AutoPublicTransitUI.Toggle();
                else
                    AutoPublicTransitUI.Instance.BringToFront();
            }

            UnityEngine.Object.Destroy(gameObject);
        }

        private void CenterInView()
        {
            UIView view = UIView.GetAView();
            if (view == null)
                return;

            relativePosition = new Vector3(
                Mathf.Max(0f, (view.fixedWidth - width) * 0.5f),
                Mathf.Max(0f, (view.fixedHeight - height) * 0.5f),
                0f);
        }

        private void ApplyResponsiveSize()
        {
            UIView view = UIView.GetAView();
            if (view == null)
            {
                width = 700;
                height = 560;
                return;
            }

            float availableWidth = Mathf.Max(360f, view.fixedWidth - 56f);
            float availableHeight = Mathf.Max(360f, view.fixedHeight - 72f);
            width = Mathf.Min(800f, availableWidth);
            height = Mathf.Min(620f, availableHeight);
        }

        private void PopulateScrollContent(UIScrollablePanel scrollPanel, float contentWidth)
        {
            string releaseNotesText = BuildReleaseNotesText(contentWidth);
            float textHeight = EstimateTextBlockHeight(releaseNotesText, contentWidth, BodyTextScale);
            UILabel releaseNotes = AddLabel(scrollPanel, releaseNotesText, 0f, 0f, contentWidth, textHeight, BodyTextScale, TextTint, false);
            releaseNotes.textColor = TextTint;

            UIPanel spacer = scrollPanel.AddUIComponent<UIPanel>();
            spacer.width = 1f;
            spacer.height = 1f;
            spacer.relativePosition = new Vector3(0f, textHeight + 16f);
        }

        private string BuildReleaseNotesText(float contentWidth)
        {
            int maxChars = EstimateCharsPerLine(contentWidth, BodyTextScale);
            var lines = new List<string>();
            AppendWrapped(lines, ReleaseIntro, maxChars, string.Empty, string.Empty);
            lines.Add(string.Empty);

            for (int i = 0; i < ReleaseBullets.Length; i++)
            {
                AppendWrapped(lines, ReleaseBullets[i], maxChars, "- ", "  ");
                if (i < ReleaseBullets.Length - 1)
                    lines.Add(string.Empty);
            }

            return string.Join("\n", lines.ToArray());
        }

        private void AppendWrapped(List<string> lines, string text, int maxChars, string firstPrefix, string continuationPrefix)
        {
            if (lines == null)
                return;

            string remaining = text ?? string.Empty;
            string prefix = firstPrefix ?? string.Empty;
            string nextPrefix = continuationPrefix ?? string.Empty;

            while (remaining.Length > 0)
            {
                int available = Mathf.Max(16, maxChars - prefix.Length);
                if (remaining.Length <= available)
                {
                    lines.Add(prefix + remaining);
                    return;
                }

                int breakIndex = FindWrapBreak(remaining, available);
                lines.Add(prefix + remaining.Substring(0, breakIndex).TrimEnd());
                remaining = remaining.Substring(breakIndex).TrimStart();
                prefix = nextPrefix;
            }

            lines.Add(prefix);
        }

        private int FindWrapBreak(string text, int maxChars)
        {
            int limit = Mathf.Min(maxChars, text.Length - 1);
            for (int i = limit; i > 0; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                    return i;
            }

            return limit;
        }

        private float EstimateTextBlockHeight(string text, float labelWidth, float textScale)
        {
            int lines = 0;
            string[] parts = (text ?? string.Empty).Split('\n');
            for (int i = 0; i < parts.Length; i++)
                lines += Mathf.Max(1, Mathf.CeilToInt(parts[i].Length / (float)EstimateCharsPerLine(labelWidth, textScale)));

            return Mathf.Max(40f, lines * Mathf.Max(24f, textScale * 29f) + 12f);
        }

        private int EstimateCharsPerLine(float labelWidth, float textScale)
        {
            return Mathf.Max(24, Mathf.FloorToInt(labelWidth / Mathf.Max(7.5f, textScale * 11.5f)));
        }

        private void AddLogo(UIComponent parent, float x, float y, float size)
        {
            UIPanel logoFrame = parent.AddUIComponent<UIPanel>();
            logoFrame.width = size;
            logoFrame.height = size;
            logoFrame.relativePosition = new Vector3(x, y);
            logoFrame.backgroundSprite = "GenericPanel";
            logoFrame.color = LogoFrameTint;

            UITextureAtlas atlas = AutoPublicTransitLauncherButton.GetOrCreateIconAtlas();
            if (atlas == null)
            {
                UILabel fallback = AddLabel(logoFrame, "APT", 6, 12, size - 12, 20, 0.62f, AccentTint, false);
                fallback.textAlignment = UIHorizontalAlignment.Center;
                return;
            }

            UISprite logo = logoFrame.AddUIComponent<UISprite>();
            logo.atlas = atlas;
            logo.spriteName = AutoPublicTransitLauncherButton.IconSpriteName;
            logo.width = size - 10f;
            logo.height = size - 10f;
            logo.relativePosition = new Vector3(5f, 5f);
            logo.isInteractive = false;
        }

        private UIScrollbar AddVerticalScrollbar(UIComponent parent, float x, float y, float scrollbarHeight, float scrollbarWidth)
        {
            UIScrollbar scrollbar = parent.AddUIComponent<UIScrollbar>();
            scrollbar.width = scrollbarWidth;
            scrollbar.height = scrollbarHeight;
            scrollbar.relativePosition = new Vector3(x, y);
            scrollbar.orientation = UIOrientation.Vertical;
            scrollbar.minValue = 0f;
            scrollbar.maxValue = 100f;
            scrollbar.value = 0f;
            scrollbar.stepSize = 1f;
            scrollbar.incrementAmount = 36f;
            scrollbar.scrollSize = 0.35f;
            scrollbar.autoHide = false;

            UISlicedSprite track = scrollbar.AddUIComponent<UISlicedSprite>();
            track.width = scrollbarWidth;
            track.height = scrollbarHeight;
            track.relativePosition = Vector3.zero;
            track.spriteName = "GenericPanel";
            track.color = ScrollTrackTint;

            UISlicedSprite thumb = track.AddUIComponent<UISlicedSprite>();
            thumb.width = scrollbarWidth;
            thumb.height = Mathf.Max(42f, scrollbarHeight * 0.32f);
            thumb.relativePosition = Vector3.zero;
            thumb.spriteName = "GenericPanel";
            thumb.color = ScrollThumbTint;

            scrollbar.trackObject = track;
            scrollbar.thumbObject = thumb;
            return scrollbar;
        }

        private UILabel AddLabel(
            UIComponent parent,
            string text,
            float x,
            float y,
            float labelWidth,
            float labelHeight,
            float textScale,
            Color32 color,
            bool wordWrap)
        {
            UILabel label = parent.AddUIComponent<UILabel>();
            label.text = text;
            label.width = labelWidth;
            label.height = labelHeight;
            label.textScale = textScale;
            label.textColor = color;
            label.autoSize = false;
            label.autoHeight = false;
            label.wordWrap = wordWrap;
            label.relativePosition = new Vector3(x, y);
            return label;
        }

        private UILabel AddBoldLabel(
            UIComponent parent,
            string text,
            float x,
            float y,
            float labelWidth,
            float labelHeight,
            float textScale,
            Color32 color,
            bool wordWrap)
        {
            UILabel weight = AddLabel(parent, text, x + 1f, y, labelWidth, labelHeight, textScale, color, wordWrap);
            weight.isInteractive = false;
            return AddLabel(parent, text, x, y, labelWidth, labelHeight, textScale, color, wordWrap);
        }
    }
}
