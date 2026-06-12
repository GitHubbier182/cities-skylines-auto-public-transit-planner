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
    public static class Geometry
    {
        public static float DistanceXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static Vector2 ToXZ(Vector3 v)
        {
            return new Vector2(v.x, v.z);
        }

        public static float SignedAngle(Vector2 from, Vector2 to)
        {
            float unsigned = Vector2.Angle(from, to);
            float sign = Mathf.Sign(from.x * to.y - from.y * to.x);
            return unsigned * sign;
        }

        public static Vector3 ClosestPointOnSegmentXZ(Vector3 a, Vector3 b, Vector3 point)
        {
            Vector3 ab = b - a;
            float denom = ab.x * ab.x + ab.z * ab.z;
            if (denom <= 0.001f)
                return a;

            float t = ((point.x - a.x) * ab.x + (point.z - a.z) * ab.z) / denom;
            t = Mathf.Clamp01(t);
            return new Vector3(a.x + ab.x * t, a.y + ab.y * t, a.z + ab.z * t);
        }
    }
}
