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
    public class ExistingLineSnapshot
    {
        public ushort LineId;
        public List<Vector3> Stops = new List<Vector3>();
        public float TotalLength;
        public int DemandScore;
        public int AverageRidership;
        public int VehicleCount;
        public int StrategicStopCount;
    }
}
