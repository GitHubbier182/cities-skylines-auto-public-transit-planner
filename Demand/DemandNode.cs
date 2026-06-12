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
    public struct DemandNode
    {
        public Vector3 Centroid;
        public Vector3 StopPosition;
        public ushort StopSegmentId;
        public int Demand;
        public int CellX;
        public int CellZ;
        public int Purpose;
        public int PurposeMask;
    }

    public static class DemandNodePurpose
    {
        public const int Normal = 0;
        public const int TransitHub = 1;
        public const int TouristAnchor = 2;

        public const int TransitHubMask = 1;
        public const int TouristAnchorMask = 2;

        public static int ToMask(int purpose)
        {
            if (purpose == TransitHub)
                return TransitHubMask;

            if (purpose == TouristAnchor)
                return TouristAnchorMask;

            return 0;
        }

        public static bool HasPurpose(DemandNode node, int purpose)
        {
            int mask = node.PurposeMask != 0 ? node.PurposeMask : ToMask(node.Purpose);
            return (mask & ToMask(purpose)) != 0;
        }

        public static bool IsStrategic(DemandNode node)
        {
            int mask = node.PurposeMask != 0 ? node.PurposeMask : ToMask(node.Purpose);
            return mask != 0;
        }
    }
}
