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
    public static class ConfigManager
    {
        private static readonly string ConfigPath =
            Path.Combine(Application.dataPath, "AutoPublicTransitConfig.json");

        public static AutoPublicTransitConfig Config { get; private set; } = new AutoPublicTransitConfig();

        public static void Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    var cfg = JsonUtility.FromJson<AutoPublicTransitConfig>(json);
                    if (cfg != null)
                        Config = cfg;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoPublicTransit] Failed to load config: " + e);
            }
        }

        public static void Save()
        {
            try
            {
                string json = JsonUtility.ToJson(Config, true);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoPublicTransit] Failed to save config: " + e);
            }
        }
    }
}
