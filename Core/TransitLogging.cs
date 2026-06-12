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
    public static class TransitLogging
    {
        public static void Log(string msg)
        {
            Debug.Log("[AutoPublicTransit] " + msg);
        }

        public static void Verbose(string msg)
        {
            if (ConfigManager.Config.EnableDebugLogging)
                Debug.Log("[AutoPublicTransit] " + msg);
        }

        public static void Warn(string msg)
        {
            Debug.LogWarning("[AutoPublicTransit] " + msg);
        }

        public static void Error(string msg)
        {
            Debug.LogError("[AutoPublicTransit] " + msg);
        }
    }
}
