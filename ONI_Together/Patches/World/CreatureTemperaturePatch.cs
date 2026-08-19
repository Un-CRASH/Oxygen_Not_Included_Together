using System.Reflection;
using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.States;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World
{
    [HarmonyPatch(typeof(CreatureSimTemperatureTransfer), nameof(CreatureSimTemperatureTransfer.Update))]
    internal static class CreatureTemperaturePatch
    {
        private static readonly FieldInfo elementChunksField =
            typeof(SimData).GetField("elementChunks", BindingFlags.Public | BindingFlags.Instance);

        public static bool Prefix(CreatureSimTemperatureTransfer __instance)
        {
            using var _ = Profiler.Scope();

            if (MultiplayerSession.IsClient) return false;

            // The same window BatteryTrackerPatch guards against, reached the same way.
            //
            // Measured on a client three milliseconds after "Loaded <save>" during a hard
            // sync: a NullReferenceException inside unsafeUpdateAverageKiloWattsExchanged.
            // The IsClient check above does not cover it - the client disconnects for the
            // save transfer, so the session flag is false for the whole gap and the client
            // falls through into the host path, simulating against a half-built world.
            //
            // Skipped rather than caught: this patch never runs the original Update, so a
            // skipped frame only means creature temperature is not advanced once, and the
            // next frame runs a fraction of a second later against a finished world.
            if (GameClient.IsHardSyncInProgress) return false;
            if (Game.Instance == null || Grid.WidthInCells == 0) return false;

            // What identifies a client mid-reconnect once its session flag has gone: the
            // cached connection is what the reconnect path itself consults. A host has
            // none, so its behaviour is unchanged.
            if (!MultiplayerSession.IsHost
                && GameClient.HasCachedConnection()
                && GameClient.State != ClientState.InGame)
                return false;

            if (Game.Instance?.simData == null) return false;
            if (__instance.average_kilowatts_exchanged == null) return false;

            try
            {
                if (elementChunksField?.GetValue(Game.Instance.simData) == null)
                    return false;
            }
            catch
            {
                return false;
            }

            __instance.unsafeUpdateAverageKiloWattsExchanged(Time.deltaTime);
            return false;
        }
    }
}
