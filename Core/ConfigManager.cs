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
        public static bool IsLoaded { get; private set; }

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

                ApplyLockedBusPlanningProfile();
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoPublicTransit] Failed to load config: " + e);
            }
            finally
            {
                IsLoaded = true;
            }
        }

        public static void Save()
        {
            try
            {
                ApplyLockedBusPlanningProfile();
                string json = JsonUtility.ToJson(Config, true);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception e)
            {
                Debug.LogError("[AutoPublicTransit] Failed to save config: " + e);
            }
        }

        public static void EnsureLoaded()
        {
            if (!IsLoaded)
                Load();
        }

        public static void ApplyLockedBusPlanningProfile()
        {
            if (Config == null)
                Config = new AutoPublicTransitConfig();

            if (!AutoPublicTransitConfig.PlayerBusPlanningOverridesEnabled)
                Config.ApplyLockedBusPlanningProfile();
        }
    }
}
