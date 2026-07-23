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
    public class AutoPublicTransitLauncherButton : UIButton
    {
        private const string ButtonName = "AutoPublicTransitLauncherButton";
        internal const string IconSpriteName = "APT_BusRouteLauncherIcon";
        private const float DepotIssuePulseSpeed = 1.8f;

        private static readonly Color32 NormalTint = new Color32(255, 255, 255, 255);
        private static readonly Color32 DepotIssueButtonDimTint = new Color32(255, 140, 38, 255);
        private static readonly Color32 DepotIssueButtonBrightTint = new Color32(255, 222, 74, 255);
        private static readonly Color32 DepotIssueIconDimTint = new Color32(255, 245, 206, 255);
        private static readonly Color32 DepotIssueIconBrightTint = new Color32(255, 84, 36, 255);

        public static AutoPublicTransitLauncherButton Instance;

        private static UITextureAtlas _iconAtlas;

        private UISprite _iconSprite;
        private bool _depotIssueVisualActive;

        public override void Start()
        {
            base.Start();

            Instance = this;
            name = ButtonName;
            width = 42;
            height = 42;
            text = string.Empty;
            tooltip = "Auto Public Transit";
            canFocus = true;
            isInteractive = true;
            isVisible = true;

            normalBgSprite = "ButtonMenu";
            hoveredBgSprite = "ButtonMenuHovered";
            pressedBgSprite = "ButtonMenuPressed";
            disabledBgSprite = "ButtonMenuDisabled";

            relativePosition = UnifiedTransitLauncherToolbar.GetButtonPosition(0);
            AddLauncherIcon();
            UnifiedTransitLauncherToolbar.RegisterDragSurface(this);
            UnifiedTransitLauncherToolbar.RefreshLayout(this);
            BringToFront();

            eventClick += OnLauncherClicked;
        }

        public override void Update()
        {
            base.Update();
            UnifiedTransitLauncherToolbar.RefreshLayoutIfOwned(this);
            ApplyDepotIssueAlertStyle();
        }

        public override void OnDestroy()
        {
            UIComponent toolbar = parent;
            eventClick -= OnLauncherClicked;

            if (Instance == this)
                Instance = null;

            base.OnDestroy();
            UnifiedTransitLauncherToolbar.RefreshLayout(toolbar);
        }

        public static void CreateIfNeeded(UIView view)
        {
            if (view == null || Instance != null)
                return;

            UIPanel toolbar = UnifiedTransitLauncherToolbar.GetOrCreate(view);
            if (toolbar == null)
                return;

            AutoPublicTransitLauncherButton existing = toolbar.Find<AutoPublicTransitLauncherButton>(ButtonName);
            if (existing != null)
            {
                Instance = existing;
                existing.isVisible = true;
                UnifiedTransitLauncherToolbar.RefreshLayout(toolbar);
                return;
            }

            UIComponent component = toolbar.AddUIComponent(typeof(AutoPublicTransitLauncherButton));
            if (component != null)
            {
                component.name = ButtonName;
                component.isVisible = true;
            }

            UnifiedTransitLauncherToolbar.RefreshLayout(toolbar);
        }

        public static void DestroyInstance()
        {
            if (Instance == null)
                return;

            UIPanel toolbar = UnifiedTransitLauncherToolbar.Current;
            Instance.isVisible = false;
            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
            UnifiedTransitLauncherToolbar.RefreshLayout(toolbar);
        }

        private void OnLauncherClicked(UIComponent component, UIMouseEventParameter p)
        {
            if (UnifiedTransitLauncherToolbar.ConsumeDragClick())
                return;

            AutoPublicTransitUI.Toggle();
        }

        private void AddLauncherIcon()
        {
            UITextureAtlas iconAtlas = GetOrCreateIconAtlas();
            if (iconAtlas == null)
            {
                text = "AT";
                textScale = 0.72f;
                return;
            }

            _iconSprite = AddUIComponent<UISprite>();
            _iconSprite.atlas = iconAtlas;
            _iconSprite.spriteName = IconSpriteName;
            _iconSprite.width = 30f;
            _iconSprite.height = 30f;
            _iconSprite.relativePosition = new Vector3(6f, 6f);
            _iconSprite.isInteractive = false;
        }

        private void ApplyDepotIssueAlertStyle()
        {
            bool hasDepotIssue = State.HasBusDepotIssue();
            if (!hasDepotIssue)
            {
                if (_depotIssueVisualActive)
                {
                    color = NormalTint;
                    textColor = NormalTint;
                    if (_iconSprite != null)
                        _iconSprite.color = NormalTint;
                    tooltip = "Auto Public Transit";
                    _depotIssueVisualActive = false;
                }

                return;
            }

            float pulse = Mathf.PingPong(Time.realtimeSinceStartup * DepotIssuePulseSpeed, 1f);
            color = LerpColor(DepotIssueButtonDimTint, DepotIssueButtonBrightTint, pulse);
            textColor = LerpColor(DepotIssueIconDimTint, DepotIssueIconBrightTint, pulse);
            if (_iconSprite != null)
                _iconSprite.color = LerpColor(DepotIssueIconDimTint, DepotIssueIconBrightTint, pulse);
            tooltip = "Auto Public Transit - depot issue needs attention";
            _depotIssueVisualActive = true;
        }

        internal static UITextureAtlas GetOrCreateIconAtlas()
        {
            if (_iconAtlas != null)
                return _iconAtlas;

            UIView view = UIView.GetAView();
            if (view == null || view.defaultAtlas == null || view.defaultAtlas.material == null)
                return null;

            Texture2D texture = CreateTransitIconTexture();
            Material material = new Material(view.defaultAtlas.material);
            material.mainTexture = texture;

            _iconAtlas = ScriptableObject.CreateInstance<UITextureAtlas>();
            _iconAtlas.name = "AutoPublicTransitLauncherAtlas";
            _iconAtlas.material = material;
            _iconAtlas.AddSprite(new UITextureAtlas.SpriteInfo
            {
                name = IconSpriteName,
                texture = texture,
                region = new Rect(0f, 0f, 1f, 1f),
                border = new RectOffset()
            });

            return _iconAtlas;
        }

        private static Texture2D CreateTransitIconTexture()
        {
            const int size = 96;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            Color32 clear = new Color32(0, 0, 0, 0);

            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = clear;

            Color32 route = new Color32(74, 188, 202, 255);
            Color32 stop = new Color32(245, 248, 250, 255);
            Color32 hub = new Color32(132, 222, 206, 255);
            Color32 shadow = new Color32(33, 39, 45, 255);

            DrawThickLine(pixels, size, 30f, 22f, 30f, 70f, 8f, shadow, 3f, 3f);
            DrawThickLine(pixels, size, 30f, 70f, 68f, 70f, 8f, shadow, 3f, 3f);
            DrawThickLine(pixels, size, 68f, 70f, 68f, 38f, 8f, shadow, 3f, 3f);

            DrawThickLine(pixels, size, 30f, 22f, 30f, 70f, 7f, route, 0f, 0f);
            DrawThickLine(pixels, size, 30f, 70f, 68f, 70f, 7f, route, 0f, 0f);
            DrawThickLine(pixels, size, 68f, 70f, 68f, 38f, 7f, route, 0f, 0f);

            DrawCircle(pixels, size, 30f, 22f, 16f, shadow, 3f, 3f);
            DrawCircle(pixels, size, 68f, 38f, 16f, shadow, 3f, 3f);
            DrawCircle(pixels, size, 30f, 70f, 16f, shadow, 3f, 3f);
            DrawCircle(pixels, size, 68f, 70f, 16f, shadow, 3f, 3f);

            DrawCircle(pixels, size, 30f, 22f, 15f, stop, 0f, 0f);
            DrawCircle(pixels, size, 68f, 38f, 15f, stop, 0f, 0f);
            DrawCircle(pixels, size, 30f, 70f, 15f, stop, 0f, 0f);
            DrawCircle(pixels, size, 68f, 70f, 15f, stop, 0f, 0f);

            DrawCircle(pixels, size, 30f, 22f, 6f, hub, 0f, 0f);
            DrawCircle(pixels, size, 68f, 38f, 6f, hub, 0f, 0f);
            DrawCircle(pixels, size, 30f, 70f, 6f, hub, 0f, 0f);
            DrawCircle(pixels, size, 68f, 70f, 6f, hub, 0f, 0f);

            texture.SetPixels32(pixels);
            texture.Apply();
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private static void DrawThickLine(Color32[] pixels, int textureSize, float x1, float y1, float x2, float y2, float radius, Color32 color, float offsetX, float offsetY)
        {
            x1 += offsetX;
            y1 += offsetY;
            x2 += offsetX;
            y2 += offsetY;
            float minX = Mathf.Min(x1, x2) - radius - 1f;
            float maxX = Mathf.Max(x1, x2) + radius + 1f;
            float minY = Mathf.Min(y1, y2) - radius - 1f;
            float maxY = Mathf.Max(y1, y2) + radius + 1f;

            float dx = x2 - x1;
            float dy = y2 - y1;
            float lengthSquared = Mathf.Max(0.0001f, (dx * dx) + (dy * dy));

            for (int row = Mathf.Max(0, Mathf.FloorToInt(minY)); row <= Mathf.Min(textureSize - 1, Mathf.CeilToInt(maxY)); row++)
            {
                for (int col = Mathf.Max(0, Mathf.FloorToInt(minX)); col <= Mathf.Min(textureSize - 1, Mathf.CeilToInt(maxX)); col++)
                {
                    float px = col + 0.5f;
                    float py = row + 0.5f;
                    float t = Mathf.Clamp01(((px - x1) * dx + (py - y1) * dy) / lengthSquared);
                    float nearestX = x1 + (dx * t);
                    float nearestY = y1 + (dy * t);
                    float distX = px - nearestX;
                    float distY = py - nearestY;
                    if ((distX * distX) + (distY * distY) <= radius * radius)
                        pixels[(row * textureSize) + col] = color;
                }
            }
        }

        private static void DrawCircle(Color32[] pixels, int textureSize, float cx, float cy, float radius, Color32 color, float offsetX, float offsetY)
        {
            cx += offsetX;
            cy += offsetY;
            float radiusSquared = radius * radius;
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius));
            int maxX = Mathf.Min(textureSize - 1, Mathf.CeilToInt(cx + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius));
            int maxY = Mathf.Min(textureSize - 1, Mathf.CeilToInt(cy + radius));

            for (int row = minY; row <= maxY; row++)
            {
                for (int col = minX; col <= maxX; col++)
                {
                    float dx = (col + 0.5f) - cx;
                    float dy = (row + 0.5f) - cy;
                    if ((dx * dx) + (dy * dy) <= radiusSquared)
                        pixels[(row * textureSize) + col] = color;
                }
            }
        }

        private static void FillRect(Color32[] pixels, int textureSize, int x, int y, int width, int height, Color32 color)
        {
            int maxX = Mathf.Min(textureSize, x + width);
            int maxY = Mathf.Min(textureSize, y + height);

            for (int row = Mathf.Max(0, y); row < maxY; row++)
            {
                for (int col = Mathf.Max(0, x); col < maxX; col++)
                    pixels[(row * textureSize) + col] = color;
            }
        }

        private static Color32 LerpColor(Color32 a, Color32 b, float t)
        {
            return new Color32(
                LerpByte(a.r, b.r, t),
                LerpByte(a.g, b.g, t),
                LerpByte(a.b, b.b, t),
                LerpByte(a.a, b.a, t));
        }

        private static byte LerpByte(byte a, byte b, float t)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(a, b, t)), 0, 255);
        }
    }
}
