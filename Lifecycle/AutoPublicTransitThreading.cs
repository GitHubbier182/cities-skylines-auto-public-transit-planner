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
    public class AutoPublicTransitThreading : ThreadingExtensionBase
    {
        public static bool RunScan;
        public static bool RunRoadUpgrades;

        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            var mgr = Manager.Instance;
            if (mgr == null)
                return;

            if (RunScan)
            {
                RunScan = false;
                mgr.RunScan();
            }

            if (RunRoadUpgrades)
            {
                RunRoadUpgrades = false;
                mgr.ApplyLatestBusLaneUpgrades();
            }
        }
    }
}
