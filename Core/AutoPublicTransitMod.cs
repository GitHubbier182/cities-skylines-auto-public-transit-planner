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
    public class AutoPublicTransitMod : IUserMod
    {
        public string Name => "Auto Public Transit Planner";
        public string Description => "Scans city demand, builds and maintains complete bus routes, and gives depot guidance.";
    }
}
