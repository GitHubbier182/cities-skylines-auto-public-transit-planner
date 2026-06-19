using System;
using System.Reflection;

namespace AutoPublicTransit
{
    public partial class Manager
    {
        private static bool _schoolBusApiInitialized;
        private static MethodInfo _schoolBusBridgeIsSchoolOwnedLineMethod;
        private static MethodInfo _schoolBusBridgeIsSchoolLineMethod;
        private static MethodInfo _schoolLineRegistryIsSchoolLineMethod;
        private static MethodInfo _schoolLineRegistrySchoolOfLineFastMethod;

        private bool IsProtectedSchoolBusRoute(ushort lineId, ref TransportLine line)
        {
            TransportInfo info = line.Info;
            if (info == null || info.m_transportType != TransportInfo.TransportType.Bus)
                return false;

            if (IsSchoolBusModOwnedLine(lineId))
                return true;

            if (ContainsSchoolBusMarker(SafeGetTransportLineName(lineId)))
                return true;

            return false;
        }

        private bool IsSchoolBusModOwnedLine(ushort lineId)
        {
            EnsureSchoolBusApiInitialized();

            bool result;
            if (TryInvokeSchoolBusBoolMethod(_schoolBusBridgeIsSchoolOwnedLineMethod, lineId, out result) && result)
                return true;

            if (TryInvokeSchoolBusBoolMethod(_schoolBusBridgeIsSchoolLineMethod, lineId, out result) && result)
                return true;

            if (TryInvokeSchoolBusBoolMethod(_schoolLineRegistryIsSchoolLineMethod, lineId, out result) && result)
                return true;

            ushort schoolId;
            return TryInvokeSchoolBusSchoolIdMethod(_schoolLineRegistrySchoolOfLineFastMethod, lineId, out schoolId) && schoolId != 0;
        }

        private static void EnsureSchoolBusApiInitialized()
        {
            if (_schoolBusApiInitialized)
                return;

            _schoolBusApiInitialized = true;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;

            Type bridgeType = Type.GetType("SchoolBuses.Integration.SchoolBusBridge, SchoolBuses", false);
            if (bridgeType != null)
            {
                _schoolBusBridgeIsSchoolOwnedLineMethod = bridgeType.GetMethod("IsSchoolOwnedLine", flags);
                _schoolBusBridgeIsSchoolLineMethod = bridgeType.GetMethod("IsSchoolLine", flags);
            }

            Type registryType = Type.GetType("SchoolBuses.Data.SchoolLineRegistry, SchoolBuses", false);
            if (registryType != null)
            {
                _schoolLineRegistryIsSchoolLineMethod = registryType.GetMethod("IsSchoolLine", flags);
                _schoolLineRegistrySchoolOfLineFastMethod = registryType.GetMethod("SchoolOfLineFast", flags);
            }
        }

        private bool TryInvokeSchoolBusBoolMethod(MethodInfo method, ushort lineId, out bool result)
        {
            result = false;
            if (method == null)
                return false;

            try
            {
                object value = method.Invoke(null, new object[] { lineId });
                if (value is bool)
                {
                    result = (bool)value;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool TryInvokeSchoolBusSchoolIdMethod(MethodInfo method, ushort lineId, out ushort schoolId)
        {
            schoolId = 0;
            if (method == null)
                return false;

            try
            {
                object value = method.Invoke(null, new object[] { lineId });
                if (value is ushort)
                {
                    schoolId = (ushort)value;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private string SafeGetTransportLineName(ushort lineId)
        {
            try
            {
                TransportManager tm = TransportManager.instance;
                return tm != null ? tm.GetLineName(lineId) : null;
            }
            catch
            {
                return null;
            }
        }

        private bool ContainsSchoolBusMarker(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string lower = value.ToLowerInvariant();
            string compact = lower.Replace(" ", "").Replace("-", "").Replace("_", "");
            return lower.Contains("school bus") ||
                   lower.Contains("school route") ||
                   lower.Contains("school shuttle") ||
                   lower.Contains("high school") ||
                   lower.Contains("elementary school") ||
                   compact.Contains("schoolbus") ||
                   compact.Contains("schoolroute") ||
                   compact.Contains("schoolshuttle") ||
                   compact.Contains("highschool") ||
                   compact.Contains("elementaryschool");
        }
    }
}
