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
    public static class UIHelpers
    {
        public static UIButton AddButton(UIComponent parent, string text, MouseEventHandler onClick)
        {
            UIButton btn = parent.AddUIComponent<UIButton>();
            btn.text = text;
            btn.width = 160;
            btn.height = 30;
            btn.textScale = 0.9f;

            btn.normalBgSprite = "ButtonMenu";
            btn.hoveredBgSprite = "ButtonMenuHovered";
            btn.pressedBgSprite = "ButtonMenuPressed";

            btn.eventClick += onClick;
            return btn;
        }
    }
}
