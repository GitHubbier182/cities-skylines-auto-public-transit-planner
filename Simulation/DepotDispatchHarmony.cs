using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AutoPublicTransit
{
    internal static class DepotDispatchHarmony
    {
        // Keep the original ID so hot reloads can reliably remove patches made by earlier builds.
        private const string HarmonyId = "ScratchyBald.AutoPublicTransit.DepotDispatchTest";

        private static Harmony _harmony;
        private static bool _patched;

        public static void Apply()
        {
            if (_patched)
                return;

            try
            {
                MethodInfo transferTarget = typeof(TransferManager).GetMethod(
                    "StartTransfer",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(TransferManager.TransferReason),
                        typeof(TransferManager.TransferOffer),
                        typeof(TransferManager.TransferOffer),
                        typeof(int)
                    },
                    null);
                MethodInfo transferPrefix = typeof(DepotDispatchHarmony).GetMethod(
                    "TransferManagerStartTransferPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo busLineTarget = typeof(BusAI).GetMethod(
                    "SetTransportLine",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[]
                    {
                        typeof(ushort),
                        typeof(Vehicle).MakeByRefType(),
                        typeof(ushort)
                    },
                    null);
                MethodInfo busLinePrefix = typeof(DepotDispatchHarmony).GetMethod(
                    "BusSetTransportLinePrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);

                if (transferTarget == null || transferPrefix == null || busLineTarget == null || busLinePrefix == null)
                {
                    TransitLogging.Warn(
                        "DEPOT_DISPATCH_PATCH_NOT_APPLIED: TransferManager.StartTransfer or BusAI.SetTransportLine target was not found.");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(transferTarget, prefix: new HarmonyMethod(transferPrefix));
                _harmony.Patch(busLineTarget, prefix: new HarmonyMethod(busLinePrefix));
                _patched = true;
                TransitLogging.Log("DEPOT_DISPATCH_PATCH_APPLIED: TransferManager.StartTransfer and BusAI.SetTransportLine depot steering active.");
            }
            catch (Exception e)
            {
                TryUnpatchAfterFailure();
                TransitLogging.Warn("DEPOT_DISPATCH_PATCH_FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Unpatch()
        {
            if (_harmony == null || !_patched)
                return;

            try
            {
                _harmony.UnpatchAll(HarmonyId);
                TransitLogging.Log("DEPOT_DISPATCH_PATCH_REMOVED.");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("DEPOT_DISPATCH_PATCH_REMOVE_FAILED: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _patched = false;
                _harmony = null;
            }
        }

        private static void TryUnpatchAfterFailure()
        {
            try
            {
                if (_harmony != null)
                    _harmony.UnpatchAll(HarmonyId);
            }
            catch
            {
            }
            finally
            {
                _patched = false;
                _harmony = null;
            }
        }

        private static void TransferManagerStartTransferPrefix(
            TransferManager.TransferReason material,
            ref TransferManager.TransferOffer offerOut,
            ref TransferManager.TransferOffer offerIn,
            int delta)
        {
            try
            {
                if (!IsBusVehicleTransferReason(material) || delta <= 0)
                    return;

                ushort lineId = offerIn.TransportLine;
                if (lineId != 0 && offerOut.Active && offerOut.Building != 0)
                {
                    Manager manager = Manager.Instance;
                    if (manager != null)
                        manager.TrySteerBusDepotDispatch(lineId, ref offerOut, "incoming-line");
                    return;
                }

                lineId = offerOut.TransportLine;
                if (lineId != 0 && offerIn.Active && offerIn.Building != 0)
                {
                    Manager manager = Manager.Instance;
                    if (manager != null)
                        manager.TrySteerBusDepotDispatch(lineId, ref offerIn, "outgoing-line");
                }
            }
            catch (Exception e)
            {
                TransitLogging.Warn("DEPOT_DISPATCH_PREFIX_FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void BusSetTransportLinePrefix(ushort vehicleID, ref Vehicle data, ushort transportLine)
        {
            try
            {
                if (transportLine != 0)
                    return;

                Manager manager = Manager.Instance;
                if (manager != null)
                    manager.TrySteerReturningBusToNearestDepot(vehicleID, ref data, "bus-line-clear");
            }
            catch (Exception e)
            {
                TransitLogging.Warn("DEPOT_RETURN_PREFIX_FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool IsBusVehicleTransferReason(TransferManager.TransferReason material)
        {
            return material == TransferManager.TransferReason.Bus ||
                   (int)material == 113;
        }
    }
}
