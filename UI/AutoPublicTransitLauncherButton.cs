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
        private const string IconSpriteName = "APT_BusRouteLauncherIcon";

        public static AutoPublicTransitLauncherButton Instance;

        private static UITextureAtlas _iconAtlas;

        private UISprite _iconSprite;

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

        private static UITextureAtlas GetOrCreateIconAtlas()
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
            const int size = 32;
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color32[] pixels = new Color32[size * size];
            Color32 clear = new Color32(0, 0, 0, 0);

            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = clear;

            Color32 route = new Color32(74, 188, 202, 255);
            Color32 stop = new Color32(245, 248, 250, 255);
            Color32 hub = new Color32(132, 222, 206, 255);
            Color32 shadow = new Color32(33, 39, 45, 255);

            FillRect(pixels, size, 9, 8, 4, 16, route);
            FillRect(pixels, size, 9, 20, 14, 4, route);
            FillRect(pixels, size, 19, 12, 4, 12, route);
            FillRect(pixels, size, 8, 7, 6, 4, shadow);
            FillRect(pixels, size, 18, 11, 6, 4, shadow);

            FillRect(pixels, size, 6, 5, 10, 10, stop);
            FillRect(pixels, size, 16, 9, 10, 10, stop);
            FillRect(pixels, size, 7, 19, 10, 10, stop);
            FillRect(pixels, size, 9, 8, 4, 4, hub);
            FillRect(pixels, size, 19, 12, 4, 4, hub);
            FillRect(pixels, size, 10, 22, 4, 4, hub);

            texture.SetPixels32(pixels);
            texture.Apply();
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
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
    }
}
