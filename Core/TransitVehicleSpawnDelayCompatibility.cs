using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace AutoPublicTransit
{
    public sealed class TransitVehicleSpawnDelayStatus
    {
        public bool IsActive;
        public bool HasBusDelay;
        public uint BusDelay;
        public string Version;
    }

    public static class TransitVehicleSpawnDelayCompatibility
    {
        private const string AssemblyName = "TransitVehicleSpawnDelay";
        private const string SettingsTypeName = "TransitVehicleSpawnDelay.ModSettings";

        public static bool TryGetActiveStatus(out TransitVehicleSpawnDelayStatus status)
        {
            status = null;

            try
            {
                Assembly assembly = FindLoadedAssembly();
                if (assembly == null)
                    return false;

                status = new TransitVehicleSpawnDelayStatus
                {
                    IsActive = true,
                    Version = assembly.GetName().Version != null ? assembly.GetName().Version.ToString() : "unknown"
                };

                Type settingsType = assembly.GetType(SettingsTypeName);
                uint busDelay;
                if (settingsType != null && TryReadUIntSetting(settingsType, "BusDelay", out busDelay))
                {
                    status.HasBusDelay = true;
                    status.BusDelay = busDelay;
                }

                return true;
            }
            catch (Exception e)
            {
                TransitLogging.Warn("Failed to inspect Transit Vehicle Spawn Delay compatibility state: " + e.Message);
                return false;
            }
        }

        private static Assembly FindLoadedAssembly()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null)
                    continue;

                AssemblyName name = assembly.GetName();
                if (name != null && string.Equals(name.Name, AssemblyName, StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }

            return null;
        }

        private static bool TryReadUIntSetting(Type type, string name, out uint value)
        {
            value = 0u;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && TryConvertToUInt(property.GetValue(null, null), out value))
                return true;

            FieldInfo field = type.GetField("<" + name + ">k__BackingField", flags);
            if (field != null && TryConvertToUInt(field.GetValue(null), out value))
                return true;

            return false;
        }

        private static bool TryConvertToUInt(object raw, out uint value)
        {
            value = 0u;
            if (raw == null)
                return false;

            if (raw is uint)
            {
                value = (uint)raw;
                return true;
            }

            if (raw is int)
            {
                int intValue = (int)raw;
                if (intValue < 0)
                    return false;

                value = (uint)intValue;
                return true;
            }

            if (raw is long)
            {
                long longValue = (long)raw;
                if (longValue < 0 || longValue > uint.MaxValue)
                    return false;

                value = (uint)longValue;
                return true;
            }

            return uint.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
