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
        public const int Residential = 3;
        public const int Commercial = 4;
        public const int Office = 5;
        public const int Industrial = 6;

        public const int TransitHubMask = 1;
        public const int TouristAnchorMask = 2;
        public const int ResidentialMask = 4;
        public const int CommercialMask = 8;
        public const int OfficeMask = 16;
        public const int IndustrialMask = 32;
        public const int WorkOrShoppingMask = CommercialMask | OfficeMask | IndustrialMask;
        public const int StrategicMask = TransitHubMask | TouristAnchorMask;

        public static int ToMask(int purpose)
        {
            if (purpose == TransitHub)
                return TransitHubMask;

            if (purpose == TouristAnchor)
                return TouristAnchorMask;

            if (purpose == Residential)
                return ResidentialMask;

            if (purpose == Commercial)
                return CommercialMask;

            if (purpose == Office)
                return OfficeMask;

            if (purpose == Industrial)
                return IndustrialMask;

            return 0;
        }

        public static int GetMask(DemandNode node)
        {
            return node.PurposeMask != 0 ? node.PurposeMask : ToMask(node.Purpose);
        }

        public static bool HasPurpose(DemandNode node, int purpose)
        {
            int mask = GetMask(node);
            return (mask & ToMask(purpose)) != 0;
        }

        public static bool HasAnyPurpose(DemandNode node, int purposeMask)
        {
            return (GetMask(node) & purposeMask) != 0;
        }

        public static bool IsStrategic(DemandNode node)
        {
            return (GetMask(node) & StrategicMask) != 0;
        }
    }
}
