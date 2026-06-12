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
    public static class RouteShapeAnalyzer
    {
        private const int MaxTwoOptPasses = 24;

        public static List<Vector3> OptimizeClosedLoop(List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            List<Vector3> source = NormalizeStops(stops);
            if (source.Count < 4)
                return source;

            List<Vector3> best = ImproveWithTwoOpt(source);
            float bestScore = ScoreRouteShape(best, cfg);

            List<Vector3> angular = SortByAngle(source);
            angular = ImproveWithTwoOpt(angular);
            float angularScore = ScoreRouteShape(angular, cfg);
            if (angularScore < bestScore)
            {
                best = angular;
                bestScore = angularScore;
            }

            int[] starts = GetNearestStartIndexes(source);
            for (int i = 0; i < starts.Length; i++)
            {
                List<Vector3> nearest = BuildNearestNeighborLoop(source, starts[i], cfg);
                nearest = ImproveWithTwoOpt(nearest);
                float nearestScore = ScoreRouteShape(nearest, cfg);
                if (nearestScore < bestScore)
                {
                    best = nearest;
                    bestScore = nearestScore;
                }
            }

            return best;
        }

        public static List<Vector3> SimplifyForBuild(List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            List<Vector3> current = OptimizeClosedLoop(stops, cfg);
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            int safety = 0;
            string reason;

            while (current.Count > minimumStops && TryGetShapeProblem(current, cfg, out reason) && safety < 16)
            {
                List<Vector3> trimmed = TrimWeakestStop(current, cfg);
                if (trimmed.Count >= current.Count)
                    break;

                current = OptimizeClosedLoop(trimmed, cfg);
                safety++;
            }

            return current;
        }

        public static List<Vector3> SimplifyForBuildPreservingOrder(List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            return SimplifyForBuildPreservingOrder(stops, cfg, Mathf.Max(2, cfg.MinStopsPerRoute));
        }

        public static List<Vector3> SimplifyForBuildPreservingOrder(List<Vector3> stops, AutoPublicTransitConfig cfg, int minimumRetainedStops)
        {
            List<Vector3> current = NormalizeStops(stops);
            int minimumStops = Mathf.Max(Mathf.Max(2, cfg.MinStopsPerRoute), minimumRetainedStops);
            int safety = 0;
            string reason;

            while (current.Count > minimumStops && TryGetShapeProblem(current, cfg, out reason) && safety < 16)
            {
                List<Vector3> trimmed = TrimWeakestStopPreservingOrder(current, cfg);
                if (trimmed.Count >= current.Count)
                    break;

                current = trimmed;
                safety++;
            }

            return current;
        }

        public static List<Vector3> TrimWeakestStop(List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            List<Vector3> source = NormalizeStops(stops);
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            if (source.Count <= minimumStops)
                return source;

            int bestRemove = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < source.Count; i++)
            {
                var candidate = new List<Vector3>(source);
                candidate.RemoveAt(i);
                candidate = OptimizeClosedLoop(candidate, cfg);
                float score = ScoreRouteShape(candidate, cfg);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestRemove = i;
            }

            if (bestRemove == -1)
                return source;

            source.RemoveAt(bestRemove);
            return source;
        }

        public static List<Vector3> TrimWeakestStopPreservingOrder(List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            List<Vector3> source = NormalizeStops(stops);
            int minimumStops = Mathf.Max(2, cfg.MinStopsPerRoute);
            if (source.Count <= minimumStops)
                return source;

            int bestRemove = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < source.Count; i++)
            {
                var candidate = new List<Vector3>(source);
                candidate.RemoveAt(i);
                float score = ScoreRouteShape(candidate, cfg);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestRemove = i;
            }

            if (bestRemove == -1)
                return source;

            source.RemoveAt(bestRemove);
            return source;
        }

        public static bool TryGetShapeProblem(List<Vector3> stops, AutoPublicTransitConfig cfg, out string reason)
        {
            List<Vector3> normalized = NormalizeStops(stops);
            if (normalized.Count < Mathf.Max(2, cfg.MinStopsPerRoute))
            {
                reason = "too-few-stops";
                return true;
            }

            float length = ComputeClosedLength(normalized);
            if (length > cfg.MaxLineLengthKm * 1000f)
            {
                reason = "too-long";
                return true;
            }

            float span = ComputeSpan(normalized);
            if (span <= 0.1f)
            {
                reason = "zero-span";
                return true;
            }

            float circuity = length / span;
            if (circuity > 2.35f)
            {
                reason = "too-circuitous";
                return true;
            }

            int crossings = CountSegmentCrossings(normalized);
            if (crossings > 0)
            {
                reason = "self-crossing";
                return true;
            }

            int reversals = CountSharpReversals(normalized);
            if (reversals > 1)
            {
                reason = "sharp-reversals";
                return true;
            }

            int nearRepeats = CountNearRepeats(normalized, Mathf.Max(60f, cfg.GridCellSize * 0.5f));
            if (nearRepeats > Mathf.Max(1, normalized.Count / 5))
            {
                reason = "repeated-stops";
                return true;
            }

            reason = null;
            return false;
        }

        public static float ComputeClosedLength(List<Vector3> stops)
        {
            if (stops == null || stops.Count < 2)
                return 0f;

            float total = 0f;
            for (int i = 1; i < stops.Count; i++)
                total += Geometry.DistanceXZ(stops[i - 1], stops[i]);

            total += Geometry.DistanceXZ(stops[stops.Count - 1], stops[0]);
            return total;
        }

        private static List<Vector3> NormalizeStops(List<Vector3> stops)
        {
            var normalized = new List<Vector3>();
            if (stops == null)
                return normalized;

            for (int i = 0; i < stops.Count; i++)
            {
                Vector3 stop = stops[i];
                if (normalized.Count > 0 && Geometry.DistanceXZ(normalized[normalized.Count - 1], stop) < 1f)
                    continue;

                normalized.Add(stop);
            }

            if (normalized.Count > 1 && Geometry.DistanceXZ(normalized[0], normalized[normalized.Count - 1]) < 1f)
                normalized.RemoveAt(normalized.Count - 1);

            return normalized;
        }

        private static List<Vector3> SortByAngle(List<Vector3> stops)
        {
            var sorted = new List<Vector3>(stops);
            Vector3 center = ComputeCentroid(sorted);
            sorted.Sort((a, b) =>
            {
                float angleA = Mathf.Atan2(a.z - center.z, a.x - center.x);
                float angleB = Mathf.Atan2(b.z - center.z, b.x - center.x);
                return angleA.CompareTo(angleB);
            });

            return RotateToBestStart(sorted);
        }

        private static List<Vector3> BuildNearestNeighborLoop(List<Vector3> stops, int startIndex, AutoPublicTransitConfig cfg)
        {
            var ordered = new List<Vector3>();
            if (stops == null || stops.Count == 0)
                return ordered;

            bool[] used = new bool[stops.Count];
            int current = Mathf.Clamp(startIndex, 0, stops.Count - 1);
            ordered.Add(stops[current]);
            used[current] = true;

            while (ordered.Count < stops.Count)
            {
                int best = -1;
                float bestScore = float.MaxValue;
                Vector3 currentStop = stops[current];

                for (int i = 0; i < stops.Count; i++)
                {
                    if (used[i])
                        continue;

                    float distance = Geometry.DistanceXZ(currentStop, stops[i]);
                    float score = distance + ComputeTurnPenalty(ordered, stops[i]) + ComputeCrossingPenalty(ordered, stops[i]);
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    best = i;
                }

                if (best == -1)
                    break;

                current = best;
                used[current] = true;
                ordered.Add(stops[current]);
            }

            return ordered;
        }

        private static List<Vector3> ImproveWithTwoOpt(List<Vector3> stops)
        {
            var improved = new List<Vector3>(stops);
            if (improved.Count < 4)
                return improved;

            bool changed = true;
            int pass = 0;
            while (changed && pass < MaxTwoOptPasses)
            {
                changed = false;
                pass++;

                for (int i = 0; i < improved.Count - 1; i++)
                {
                    for (int k = i + 2; k < improved.Count; k++)
                    {
                        if (i == 0 && k == improved.Count - 1)
                            continue;

                        Vector3 a = improved[i];
                        Vector3 b = improved[(i + 1) % improved.Count];
                        Vector3 c = improved[k];
                        Vector3 d = improved[(k + 1) % improved.Count];
                        float current = Geometry.DistanceXZ(a, b) + Geometry.DistanceXZ(c, d);
                        float swapped = Geometry.DistanceXZ(a, c) + Geometry.DistanceXZ(b, d);
                        bool crossing = SegmentsIntersect(a, b, c, d);

                        if (!crossing && swapped >= current - 1f)
                            continue;

                        ReverseRange(improved, i + 1, k);
                        changed = true;
                    }
                }
            }

            return RotateToBestStart(improved);
        }

        private static void ReverseRange(List<Vector3> stops, int start, int end)
        {
            while (start < end)
            {
                Vector3 tmp = stops[start];
                stops[start] = stops[end];
                stops[end] = tmp;
                start++;
                end--;
            }
        }

        private static List<Vector3> RotateToBestStart(List<Vector3> stops)
        {
            if (stops == null || stops.Count == 0)
                return new List<Vector3>();

            int bestIndex = 0;
            for (int i = 1; i < stops.Count; i++)
            {
                if (stops[i].x < stops[bestIndex].x || (Mathf.Abs(stops[i].x - stops[bestIndex].x) < 1f && stops[i].z < stops[bestIndex].z))
                    bestIndex = i;
            }

            var rotated = new List<Vector3>();
            for (int i = 0; i < stops.Count; i++)
                rotated.Add(stops[(bestIndex + i) % stops.Count]);

            return rotated;
        }

        private static int[] GetNearestStartIndexes(List<Vector3> stops)
        {
            int west = 0;
            int east = 0;
            int north = 0;
            int south = 0;
            for (int i = 1; i < stops.Count; i++)
            {
                if (stops[i].x < stops[west].x)
                    west = i;
                if (stops[i].x > stops[east].x)
                    east = i;
                if (stops[i].z > stops[north].z)
                    north = i;
                if (stops[i].z < stops[south].z)
                    south = i;
            }

            return new[] { west, east, north, south };
        }

        private static float ScoreRouteShape(List<Vector3> stops, AutoPublicTransitConfig cfg)
        {
            if (stops == null || stops.Count < 2)
                return float.MaxValue;

            float length = ComputeClosedLength(stops);
            float span = Mathf.Max(1f, ComputeSpan(stops));
            int crossings = CountSegmentCrossings(stops);
            int reversals = CountSharpReversals(stops);
            int nearRepeats = CountNearRepeats(stops, Mathf.Max(60f, cfg.GridCellSize * 0.5f));
            return length
                + crossings * Mathf.Max(600f, cfg.MaxRoadDistance * 3f)
                + reversals * Mathf.Max(220f, cfg.MaxRoadDistance)
                + nearRepeats * Mathf.Max(120f, cfg.GridCellSize)
                + (length / span) * 90f;
        }

        private static float ComputeTurnPenalty(List<Vector3> ordered, Vector3 candidate)
        {
            if (ordered == null || ordered.Count < 2)
                return 0f;

            Vector2 previous = Geometry.ToXZ(ordered[ordered.Count - 1] - ordered[ordered.Count - 2]);
            Vector2 next = Geometry.ToXZ(candidate - ordered[ordered.Count - 1]);
            if (previous.sqrMagnitude < 1f || next.sqrMagnitude < 1f)
                return 0f;

            float angle = Mathf.Abs(Geometry.SignedAngle(previous.normalized, next.normalized));
            if (angle > 165f)
                return 360f;
            if (angle > 135f)
                return 190f;
            if (angle > 105f)
                return 70f;

            return 0f;
        }

        private static float ComputeCrossingPenalty(List<Vector3> ordered, Vector3 candidate)
        {
            if (ordered == null || ordered.Count < 3)
                return 0f;

            Vector3 from = ordered[ordered.Count - 1];
            for (int i = 0; i < ordered.Count - 2; i++)
            {
                if (SegmentsIntersect(ordered[i], ordered[i + 1], from, candidate))
                    return 900f;
            }

            return 0f;
        }

        private static Vector3 ComputeCentroid(List<Vector3> stops)
        {
            Vector3 total = Vector3.zero;
            for (int i = 0; i < stops.Count; i++)
                total += stops[i];

            return total / Mathf.Max(1, stops.Count);
        }

        private static float ComputeSpan(List<Vector3> stops)
        {
            float span = 0f;
            for (int i = 0; i < stops.Count; i++)
            {
                for (int j = i + 1; j < stops.Count; j++)
                    span = Mathf.Max(span, Geometry.DistanceXZ(stops[i], stops[j]));
            }

            return span;
        }

        private static int CountSharpReversals(List<Vector3> stops)
        {
            if (stops == null || stops.Count < 3)
                return 0;

            int reversals = 0;
            for (int i = 0; i < stops.Count; i++)
            {
                Vector3 previous = stops[(i - 1 + stops.Count) % stops.Count];
                Vector3 current = stops[i];
                Vector3 next = stops[(i + 1) % stops.Count];
                Vector2 a = Geometry.ToXZ(current - previous);
                Vector2 b = Geometry.ToXZ(next - current);
                if (a.sqrMagnitude < 1f || b.sqrMagnitude < 1f)
                    continue;

                float angle = Mathf.Abs(Geometry.SignedAngle(a.normalized, b.normalized));
                if (angle > 150f)
                    reversals++;
            }

            return reversals;
        }

        private static int CountNearRepeats(List<Vector3> stops, float maxDistance)
        {
            int repeats = 0;
            if (stops == null)
                return repeats;

            for (int i = 0; i < stops.Count; i++)
            {
                for (int j = i + 2; j < stops.Count; j++)
                {
                    if (i == 0 && j == stops.Count - 1)
                        continue;

                    if (Geometry.DistanceXZ(stops[i], stops[j]) <= maxDistance)
                        repeats++;
                }
            }

            return repeats;
        }

        private static int CountSegmentCrossings(List<Vector3> stops)
        {
            int crossings = 0;
            if (stops == null || stops.Count < 4)
                return crossings;

            for (int i = 0; i < stops.Count; i++)
            {
                int iNext = (i + 1) % stops.Count;
                for (int j = i + 1; j < stops.Count; j++)
                {
                    int jNext = (j + 1) % stops.Count;
                    if (i == j || iNext == j || jNext == i)
                        continue;
                    if (i == 0 && jNext == 0)
                        continue;

                    if (SegmentsIntersect(stops[i], stops[iNext], stops[j], stops[jNext]))
                        crossings++;
                }
            }

            return crossings;
        }

        private static bool SegmentsIntersect(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (ShareEndpoint(a, b, c, d))
                return false;

            Vector2 p1 = Geometry.ToXZ(a);
            Vector2 p2 = Geometry.ToXZ(b);
            Vector2 q1 = Geometry.ToXZ(c);
            Vector2 q2 = Geometry.ToXZ(d);
            float o1 = Orientation(p1, p2, q1);
            float o2 = Orientation(p1, p2, q2);
            float o3 = Orientation(q1, q2, p1);
            float o4 = Orientation(q1, q2, p2);

            return o1 * o2 < -0.01f && o3 * o4 < -0.01f;
        }

        private static bool ShareEndpoint(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            return Geometry.DistanceXZ(a, c) < 1f
                || Geometry.DistanceXZ(a, d) < 1f
                || Geometry.DistanceXZ(b, c) < 1f
                || Geometry.DistanceXZ(b, d) < 1f;
        }

        private static float Orientation(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }
    }
}
