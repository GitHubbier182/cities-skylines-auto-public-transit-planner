using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace ScratchyBald.CitiesSkylines.UI
{
    internal sealed class ReleaseNoticeContent
    {
        public readonly string PlayerPrefsKey;
        public readonly string NoticeId;
        public readonly string Title;
        public readonly string Subtitle;
        public readonly string Intro;
        public readonly string BadgeText;
        public readonly string[] Bullets;
        public readonly string PrimaryButtonLabel;
        public readonly Action PrimaryAction;

        public ReleaseNoticeContent(
            string playerPrefsKey,
            string noticeId,
            string title,
            string subtitle,
            string intro,
            string badgeText,
            string[] bullets,
            string primaryButtonLabel,
            Action primaryAction)
        {
            PlayerPrefsKey = playerPrefsKey ?? string.Empty;
            NoticeId = noticeId ?? string.Empty;
            Title = title ?? "Update";
            Subtitle = subtitle ?? string.Empty;
            Intro = intro ?? string.Empty;
            BadgeText = badgeText ?? string.Empty;
            Bullets = bullets ?? new string[0];
            PrimaryButtonLabel = primaryButtonLabel ?? string.Empty;
            PrimaryAction = primaryAction;
        }

        public bool Enabled
        {
            get { return !IsBlank(PlayerPrefsKey) && !IsBlank(NoticeId); }
        }

        public bool HasPrimaryAction
        {
            get { return PrimaryAction != null && !IsBlank(PrimaryButtonLabel); }
        }

        private static bool IsBlank(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }

    internal sealed class OneTimeUpdateNoticePanel : UIPanel
    {
        private static OneTimeUpdateNoticePanel Instance;

        private static readonly Color32 PanelTint = new Color32(48, 64, 70, 250);
        private static readonly Color32 TitleTint = new Color32(104, 144, 154, 255);
        private static readonly Color32 CardTint = new Color32(72, 94, 100, 250);
        private static readonly Color32 LogoFrameTint = new Color32(42, 54, 59, 255);
        private static readonly Color32 AccentTint = new Color32(132, 222, 206, 255);
        private static readonly Color32 TextTint = new Color32(235, 242, 242, 255);
        private static readonly Color32 MutedTextTint = new Color32(205, 221, 222, 255);
        private static readonly Color32 ScrollTrackTint = new Color32(36, 49, 54, 230);
        private static readonly Color32 ScrollThumbTint = new Color32(144, 184, 190, 255);

        private const float TitleTextScale = 1.12f;
        private const float SubtitleTextScale = 0.76f;
        private const float BodyTextScale = 0.9f;
        private const float ButtonTextScale = 0.92f;

        private ReleaseNoticeContent _content;
        private bool _built;

        public static void ShowIfNeeded(UIView view, ReleaseNoticeContent content)
        {
            if (view == null || content == null || !content.Enabled || Instance != null)
                return;

            string shownNoticeId = PlayerPrefs.GetString(content.PlayerPrefsKey, string.Empty);
            if (string.Equals(shownNoticeId, content.NoticeId, StringComparison.Ordinal))
                return;

            OneTimeUpdateNoticePanel panel = view.AddUIComponent(typeof(OneTimeUpdateNoticePanel)) as OneTimeUpdateNoticePanel;
            if (panel == null)
                return;

            panel.Configure(content);
            PlayerPrefs.SetString(content.PlayerPrefsKey, content.NoticeId);
            PlayerPrefs.Save();
        }

        public static void DestroyInstance()
        {
            if (Instance == null)
                return;

            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        public override void Start()
        {
            base.Start();
            Instance = this;
        }

        public override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            base.OnDestroy();
        }

        private void Configure(ReleaseNoticeContent content)
        {
            _content = content;
            Instance = this;
            Build();
        }

        private void Build()
        {
            if (_built || _content == null)
                return;

            _built = true;
            name = "ScratchyBaldOneTimeUpdateNoticePanel";
            ApplyResponsiveSize();
            backgroundSprite = "MenuPanel2";
            color = PanelTint;
            canFocus = true;
            isInteractive = true;
            CenterInView();

            UIPanel titleBar = AddUIComponent<UIPanel>();
            titleBar.width = width - 12f;
            titleBar.height = 78f;
            titleBar.relativePosition = new Vector3(6f, 6f);
            titleBar.backgroundSprite = "GenericPanel";
            titleBar.color = TitleTint;

            UIDragHandle dragHandle = titleBar.AddUIComponent<UIDragHandle>();
            dragHandle.width = titleBar.width;
            dragHandle.height = titleBar.height;
            dragHandle.relativePosition = Vector3.zero;
            dragHandle.target = this;

            AddBadge(titleBar, 14f, 10f, 58f);
            AddBoldLabel(titleBar, _content.Title, 88f, 12f, titleBar.width - 148f, 30f, TitleTextScale, TextTint, false);
            AddLabel(titleBar, _content.Subtitle, 90f, 46f, titleBar.width - 150f, 24f, SubtitleTextScale, MutedTextTint, false);

            UIButton closeButton = AddButton(titleBar, "X", (component, param) => Close(false));
            closeButton.width = 32f;
            closeButton.height = 28f;
            closeButton.textScale = 0.9f;
            closeButton.relativePosition = new Vector3(titleBar.width - 42f, 12f);

            UIPanel body = AddUIComponent<UIPanel>();
            body.width = width - 32f;
            body.height = height - 174f;
            body.relativePosition = new Vector3(16f, 104f);
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

            float gotItWidth = 150f;
            UIButton gotItButton = AddButton(this, "Got it", (component, param) => Close(false));
            gotItButton.width = gotItWidth;
            gotItButton.height = 36f;
            gotItButton.textScale = ButtonTextScale;
            gotItButton.relativePosition = new Vector3(width - gotItWidth - 18f, height - 58f);

            if (_content.HasPrimaryAction)
            {
                UIButton primaryButton = AddButton(this, _content.PrimaryButtonLabel, (component, param) => Close(true));
                primaryButton.width = 170f;
                primaryButton.height = 36f;
                primaryButton.textScale = ButtonTextScale;
                primaryButton.relativePosition = new Vector3(width - gotItWidth - primaryButton.width - 28f, height - 58f);
            }

            BringToFront();
        }

        private void Close(bool runPrimaryAction)
        {
            if (runPrimaryAction && _content != null && _content.PrimaryAction != null)
                _content.PrimaryAction();

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
                width = 700f;
                height = 520f;
                return;
            }

            float availableWidth = Mathf.Max(360f, view.fixedWidth - 56f);
            float availableHeight = Mathf.Max(360f, view.fixedHeight - 72f);
            width = Mathf.Min(780f, availableWidth);
            height = Mathf.Min(580f, availableHeight);
        }

        private void PopulateScrollContent(UIScrollablePanel scrollPanel, float contentWidth)
        {
            string text = BuildNoticeText(contentWidth);
            float textHeight = EstimateTextBlockHeight(text, contentWidth, BodyTextScale);
            UILabel notes = AddLabel(scrollPanel, text, 0f, 0f, contentWidth, textHeight, BodyTextScale, TextTint, false);
            notes.textColor = TextTint;

            UIPanel spacer = scrollPanel.AddUIComponent<UIPanel>();
            spacer.width = 1f;
            spacer.height = 1f;
            spacer.relativePosition = new Vector3(0f, textHeight + 16f);
        }

        private string BuildNoticeText(float contentWidth)
        {
            int maxChars = EstimateCharsPerLine(contentWidth, BodyTextScale);
            var lines = new List<string>();
            AppendWrapped(lines, _content.Intro, maxChars, string.Empty, string.Empty);

            if (_content.Bullets.Length > 0)
                lines.Add(string.Empty);

            for (int i = 0; i < _content.Bullets.Length; i++)
            {
                AppendWrapped(lines, _content.Bullets[i], maxChars, "- ", "  ");
                if (i < _content.Bullets.Length - 1)
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

            if (remaining.Length == 0)
            {
                lines.Add(prefix);
                return;
            }

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

        private void AddBadge(UIComponent parent, float x, float y, float size)
        {
            UIPanel frame = parent.AddUIComponent<UIPanel>();
            frame.width = size;
            frame.height = size;
            frame.relativePosition = new Vector3(x, y);
            frame.backgroundSprite = "GenericPanel";
            frame.color = LogoFrameTint;

            UILabel badge = AddLabel(frame, BuildBadgeText(), 4f, 16f, size - 8f, 26f, 0.78f, AccentTint, false);
            badge.textAlignment = UIHorizontalAlignment.Center;
        }

        private string BuildBadgeText()
        {
            string text = _content != null ? _content.BadgeText : string.Empty;
            if (text == null)
                return string.Empty;

            text = text.Trim();
            if (text.Length <= 4)
                return text.ToUpperInvariant();

            return text.Substring(0, 4).ToUpperInvariant();
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

        private UIButton AddButton(UIComponent parent, string text, MouseEventHandler handler)
        {
            UIButton button = parent.AddUIComponent<UIButton>();
            button.text = text;
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.disabledBgSprite = "ButtonMenuDisabled";
            button.textColor = TextTint;
            button.hoveredTextColor = TextTint;
            button.pressedTextColor = TextTint;
            button.disabledTextColor = MutedTextTint;
            button.canFocus = true;
            button.isInteractive = true;
            if (handler != null)
                button.eventClick += handler;
            return button;
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
            label.text = text ?? string.Empty;
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
