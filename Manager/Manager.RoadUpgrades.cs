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
    public partial class Manager : MonoBehaviour
    {
        public void ApplyLatestBusLaneUpgrades()
        {
            if (!AutoPublicTransitConfig.BusLaneRoadUpgradesPlayerEnabled)
            {
                TransitLogging.Log("Bus-lane road upgrades are in development and disabled for this release.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: in development");
                return;
            }

            if (_roadUpgradeRunning)
            {
                TransitLogging.Warn("Bus-lane road upgrades are already running.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: already running");
                return;
            }

            _roadUpgradeRunning = true;
            int applied = 0;
            int skipped = 0;

            try
            {
                List<BusLaneUpgradeRecommendation> recommendations = State.LastBusLaneRecommendations;
                if (recommendations == null || recommendations.Count == 0)
                {
                    TransitLogging.Log("No bus-lane recommendations to apply. Run Scan first.");
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus("No bus-lane recommendations to apply. Run Scan first.");
                    return;
                }

                int limit = Mathf.Min(recommendations.Count, Mathf.Max(0, ConfigManager.Config.MaxBusLaneRecommendations));
                if (limit == 0)
                {
                    TransitLogging.Log("No bus-lane recommendations to apply because Max Recommendations is 0.");
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: 0 applied, 0 skipped");
                    return;
                }

                TransitLogging.Log("Applying up to " + limit + " bus-lane road upgrades from latest recommendations.");
                for (int i = 0; i < limit; i++)
                {
                    BusLaneUpgradeRecommendation recommendation = recommendations[i];
                    string skipReason;
                    bool appliedRecommendation = false;
                    try
                    {
                        appliedRecommendation = TryApplyBusLaneUpgrade(recommendation, out skipReason);
                    }
                    catch (Exception e)
                    {
                        skipReason = "unexpected exception " + e.GetType().Name + ": " + e.Message;
                        TransitLogging.Warn("Skipped bus-lane upgrade for segment " + recommendation.SegmentId + " after exception: " + e + ".");
                    }

                    if (appliedRecommendation)
                    {
                        applied++;
                    }
                    else
                    {
                        skipped++;
                        TransitLogging.Log("Skipped bus-lane upgrade for segment " + recommendation.SegmentId + ": " + skipReason + ".");
                    }
                }

                string summary = "Road upgrades: " + applied + " applied, " + skipped + " skipped";
                TransitLogging.Log(summary + ".");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus(summary);

                if (applied > 0)
                {
                    State.StopCache.Clear();
                    TransitLogging.Log("Road upgrades changed road types; cleared cached bus-stop anchors and starting route integrity refresh.");
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus(summary + " - refreshing lines");
                    RunScan();
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus(summary + " - refresh complete");
                }
            }
            catch (Exception e)
            {
                TransitLogging.Error("Bus-lane road upgrades failed: " + e);
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: failed");
            }
            finally
            {
                _roadUpgradeRunning = false;
            }
        }

        private bool TryApplyBusLaneUpgrade(BusLaneUpgradeRecommendation recommendation, out string skipReason)
        {
            skipReason = null;
            if (recommendation == null)
            {
                skipReason = "missing recommendation";
                return false;
            }

            var cfg = ConfigManager.Config;
            NetManager nm = NetManager.instance;
            int routeThreshold = Mathf.Max(1, cfg.BusLaneRouteThreshold);
            if (recommendation.BusLineCount < routeThreshold)
            {
                skipReason = "recommendation has " + recommendation.BusLineCount + " bus routes, below threshold " + routeThreshold;
                return false;
            }

            ushort segmentId = recommendation.SegmentId;
            if (segmentId == 0 || segmentId >= nm.m_segments.m_size)
            {
                skipReason = "segment id is out of range";
                return false;
            }

            ref NetSegment segment = ref nm.m_segments.m_buffer[segmentId];
            if ((segment.m_flags & NetSegment.Flags.Created) == 0)
            {
                skipReason = "segment no longer exists";
                return false;
            }

            NetInfo currentInfo = segment.Info;
            if (currentInfo == null)
            {
                skipReason = "segment has no road info";
                return false;
            }

            string currentName = GetNetInfoName(currentInfo);
            if (!string.Equals(currentName, recommendation.CurrentRoadName, StringComparison.Ordinal))
            {
                skipReason = "road type changed from " + recommendation.CurrentRoadName + " to " + currentName;
                return false;
            }

            if (!IsBusLaneRecommendationCandidate(segmentId, ref segment, currentInfo, cfg))
            {
                skipReason = "segment is no longer an eligible bus-lane candidate";
                return false;
            }

            NetInfo targetInfo = FindBusLaneUpgradeTarget(currentInfo);
            if (targetInfo == null)
            {
                skipReason = "no compatible bus-lane road found";
                return false;
            }

            ushort resultingSegmentId;
            ToolBase.ToolErrors errors;
            try
            {
                errors = ApplyRoadUpgradeWithNetTool(segmentId, ref segment, targetInfo, out resultingSegmentId);
            }
            catch (Exception e)
            {
                skipReason = "upgrade API threw " + e.GetType().Name + ": " + e.Message;
                TransitLogging.Warn("Bus-lane upgrade attempt threw for segment " + segmentId + ": " + e + ".");
                return false;
            }

            ushort checkSegmentId = resultingSegmentId != 0 ? resultingSegmentId : segmentId;
            NetInfo afterInfo = null;
            if (checkSegmentId < nm.m_segments.m_size)
            {
                ref NetSegment afterSegment = ref nm.m_segments.m_buffer[checkSegmentId];
                if ((afterSegment.m_flags & NetSegment.Flags.Created) != 0)
                    afterInfo = afterSegment.Info;
            }

            string targetName = GetNetInfoName(targetInfo);
            string afterName = GetNetInfoName(afterInfo);
            TransitLogging.Log(
                "Bus-lane upgrade attempt: segment " + segmentId +
                ", current=" + currentName +
                ", target=" + targetName +
                ", result=" + errors +
                ", resultingSegment=" + resultingSegmentId +
                ", after=" + afterName + ".");

            if (errors != ToolBase.ToolErrors.None && errors != ToolBase.ToolErrors.AlreadyExists)
            {
                skipReason = "upgrade API returned " + errors;
                return false;
            }

            if (afterInfo == null)
            {
                skipReason = "segment missing after upgrade attempt";
                return false;
            }

            if (!ReferenceEquals(afterInfo, targetInfo) && !HasBusLane(afterInfo))
            {
                skipReason = "upgrade API did not change the road type; after=" + afterName;
                return false;
            }

            TransitLogging.Log(
                "Applied bus-lane upgrade: segment " + checkSegmentId +
                ", " + currentName +
                " -> " + afterName +
                ", busLines=" + recommendation.BusLineCount +
                ", traffic=" + recommendation.TrafficDensity +
                ", score=" + recommendation.Score +
                ", reason=" + recommendation.Reason + ".");

            return true;
        }

        private ToolBase.ToolErrors ApplyRoadUpgradeWithNetTool(ushort segmentId, ref NetSegment segment, NetInfo targetInfo, out ushort resultingSegmentId)
        {
            NetManager nm = NetManager.instance;
            NetTool.ControlPoint startPoint = new NetTool.ControlPoint();
            NetTool.ControlPoint middlePoint = new NetTool.ControlPoint();
            NetTool.ControlPoint endPoint = new NetTool.ControlPoint();
            resultingSegmentId = 0;

            startPoint.m_node = segment.m_startNode;
            startPoint.m_segment = segmentId;
            startPoint.m_position = nm.m_nodes.m_buffer[segment.m_startNode].m_position;
            startPoint.m_direction = segment.m_startDirection;
            startPoint.m_elevation = startPoint.m_position.y;

            middlePoint.m_node = 0;
            middlePoint.m_segment = segmentId;
            middlePoint.m_position = segment.m_middlePosition;
            middlePoint.m_direction = Vector3.zero;
            middlePoint.m_elevation = middlePoint.m_position.y;

            endPoint.m_node = segment.m_endNode;
            endPoint.m_segment = segmentId;
            endPoint.m_position = nm.m_nodes.m_buffer[segment.m_endNode].m_position;
            endPoint.m_direction = segment.m_endDirection;
            endPoint.m_elevation = endPoint.m_position.y;

            ushort firstNode;
            ushort lastNode;
            ushort newSegment;
            int cost;
            int productionRate;
            bool invert = (segment.m_flags & NetSegment.Flags.Invert) != 0;

            ToolBase.ToolErrors errors = NetTool.CreateNode(
                targetInfo,
                startPoint,
                middlePoint,
                endPoint,
                new FastList<NetTool.NodePosition>(),
                2,
                false,
                false,
                false,
                true,
                false,
                invert,
                false,
                0,
                out firstNode,
                out lastNode,
                out newSegment,
                out cost,
                out productionRate);
            resultingSegmentId = newSegment;
            return errors;
        }
    }
}
