using System;

namespace AutoPublicTransit
{
    public partial class Manager
    {
        private const int RouteRoadUpgradeSegmentsPerUpdate = 20;

        public void PreviewRouteRoadUpgrades()
        {
            if (!AutoPublicTransitConfig.RouteRoadUpgradesPlayerEnabled)
            {
                TransitLogging.Warn(AutoPublicTransitConfig.RouteRoadUpgradesDisabledLog);
                AutoPublicTransitUI.UpdateRoadUpgradeStatus(AutoPublicTransitConfig.RouteRoadUpgradesDisabledStatus);
                return;
            }

            if (_roadUpgradeRunning)
            {
                TransitLogging.Warn("Route road upgrades are already running.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: already running");
                return;
            }

            if (IsRouteRoadUpgradeApplyActive())
            {
                TransitLogging.Warn("Route road upgrades are already applying.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: applying");
                return;
            }

            _roadUpgradeRunning = true;
            AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: scanning routes");

            try
            {
                var service = new BusRouteRoadUpgradeService();
                _pendingRouteRoadUpgradePlan = service.BuildRouteRoadUpgradePlan();
                AutoPublicTransitUI.ShowRoadUpgradePlan(_pendingRouteRoadUpgradePlan);
            }
            catch (Exception e)
            {
                _pendingRouteRoadUpgradePlan = null;
                TransitLogging.Error("Route road-upgrade preview failed: " + e);
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: preview failed");
            }
            finally
            {
                _roadUpgradeRunning = false;
            }
        }

        public bool QueuePendingRouteRoadUpgradePlan(int planId)
        {
            if (!AutoPublicTransitConfig.RouteRoadUpgradesPlayerEnabled)
            {
                _pendingRouteRoadUpgradePlan = null;
                TransitLogging.Warn(AutoPublicTransitConfig.RouteRoadUpgradesDisabledLog);
                AutoPublicTransitUI.UpdateRoadUpgradeStatus(AutoPublicTransitConfig.RouteRoadUpgradesDisabledStatus);
                return false;
            }

            if (IsRouteRoadUpgradeApplyActive())
            {
                TransitLogging.Warn("Route road upgrades are already applying.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: applying");
                return false;
            }

            if (_pendingRouteRoadUpgradePlan == null || _pendingRouteRoadUpgradePlan.PlanId != planId)
            {
                TransitLogging.Warn("Route road-upgrade plan was missing or stale when apply was requested.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: preview expired");
                return false;
            }

            ScheduleRouteRoadUpgradeApply();
            return true;
        }

        public void ClearPendingRouteRoadUpgradePlan(int planId)
        {
            if (_pendingRouteRoadUpgradePlan != null && _pendingRouteRoadUpgradePlan.PlanId == planId)
                _pendingRouteRoadUpgradePlan = null;
        }

        public void ApplyRouteRoadUpgrades()
        {
            if (!AutoPublicTransitConfig.RouteRoadUpgradesPlayerEnabled)
            {
                TransitLogging.Warn(AutoPublicTransitConfig.RouteRoadUpgradesDisabledLog);
                AutoPublicTransitUI.UpdateRoadUpgradeStatus(AutoPublicTransitConfig.RouteRoadUpgradesDisabledStatus);
                return;
            }

            if (_roadUpgradeRunning)
            {
                TransitLogging.Warn("Route road upgrades are already running.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: already running");
                return;
            }

            _roadUpgradeRunning = true;

            try
            {
                var service = new BusRouteRoadUpgradeService();

                if (_activeRouteRoadUpgradeApplyState == null)
                {
                    if (_pendingRouteRoadUpgradePlan == null)
                    {
                        TransitLogging.Warn("Route road upgrades require a preview plan before applying.");
                        AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: preview first");
                        return;
                    }

                    BusRouteRoadUpgradePlan plan = _pendingRouteRoadUpgradePlan;
                    _pendingRouteRoadUpgradePlan = null;
                    _activeRouteRoadUpgradeApplyState = service.BeginRouteRoadUpgradeApply(plan);
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: applying RON");
                }

                service.ApplyNextRouteRoadUpgradeChunk(_activeRouteRoadUpgradeApplyState, RouteRoadUpgradeSegmentsPerUpdate);

                if (_activeRouteRoadUpgradeApplyState.Completed)
                {
                    BusRouteRoadUpgradeResult result = _activeRouteRoadUpgradeApplyState.Result;
                    _activeRouteRoadUpgradeApplyState = null;
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus(result.ToStatusText());
                }
                else
                {
                    AutoPublicTransitUI.UpdateRoadUpgradeStatus(_activeRouteRoadUpgradeApplyState.ToProgressStatusText());
                    ScheduleRouteRoadUpgradeApply();
                }
            }
            catch (Exception e)
            {
                _activeRouteRoadUpgradeApplyState = null;
                TransitLogging.Error("Route road upgrades failed: " + e);
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: failed");
            }
            finally
            {
                _roadUpgradeRunning = false;
            }
        }

        private bool IsRouteRoadUpgradeApplyActive()
        {
            return _activeRouteRoadUpgradeApplyState != null && !_activeRouteRoadUpgradeApplyState.Completed;
        }

        private void ScheduleRouteRoadUpgradeApply()
        {
            if (_routeRoadUpgradeApplyScheduled)
                return;

            SimulationManager simulationManager = SimulationManager.instance;
            if (simulationManager == null)
            {
                TransitLogging.Warn("Route road upgrades could not schedule RON apply: SimulationManager is unavailable.");
                AutoPublicTransitUI.UpdateRoadUpgradeStatus("Road upgrades: failed to schedule");
                return;
            }

            _routeRoadUpgradeApplyScheduled = true;
            simulationManager.AddAction(() =>
            {
                _routeRoadUpgradeApplyScheduled = false;
                ApplyRouteRoadUpgrades();
            });
        }
    }
}
