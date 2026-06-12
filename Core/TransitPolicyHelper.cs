using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoPublicTransit
{
    public static class TransitPolicyHelper
    {
        public static bool IsFreeTransportPolicyActiveForStops(List<Vector3> stops)
        {
            if (IsCityFreeTransportPolicyActive())
                return true;

            if (stops == null || stops.Count == 0)
                return false;

            DistrictManager dm = DistrictManager.instance;
            if (dm == null)
                return false;

            var checkedDistricts = new HashSet<byte>();
            for (int i = 0; i < stops.Count; i++)
            {
                byte district = dm.GetDistrict(stops[i]);
                if (district == 0 || checkedDistricts.Contains(district))
                    continue;

                checkedDistricts.Add(district);
                if (dm.IsDistrictPolicySet(DistrictPolicies.Policies.FreeTransport, district))
                    return true;
            }

            return false;
        }

        public static bool IsCityFreeTransportPolicyActive()
        {
            DistrictManager dm = DistrictManager.instance;
            return dm != null && dm.IsCityPolicySet(DistrictPolicies.Policies.FreeTransport);
        }
    }
}
