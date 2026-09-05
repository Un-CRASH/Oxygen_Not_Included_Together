using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Patches.World.SideScreen
{
	/// <summary>
	/// Patches for filter side screens (FilterSideScreen, TreeFilterable)
	/// </summary>

	/// <summary>
	/// Sync filter element selection
	/// </summary>
	[HarmonyPatch(typeof(FilterSideScreen), nameof(FilterSideScreen.SetFilterTag))]
	public static class FilterSideScreen_SetFilterTag_Patch
	{
		public static void Postfix(FilterSideScreen __instance, Tag tag)
		{
			using var _ = Profiler.Scope();

			if (BuildingConfigPacket.IsApplyingPacket) return;
			if (!MultiplayerSession.InActiveSession) return;
			if (__instance.targetFilterable == null) return;

			var targetGO = (__instance.targetFilterable as Component)?.gameObject;
			if (targetGO == null) return;

			var identity = targetGO.AddOrGet<NetworkIdentity>();
			identity.RegisterIdentity();

			// Use string-based tag name instead of hash for proper reconstruction
			var packet = new BuildingConfigPacket
			{
				NetId = identity.NetId,
				Cell = Grid.PosToCell(targetGO),
				ConfigHash = "FilterTagString".GetHashCode(),
				Value = 0,
				ConfigType = BuildingConfigType.String,
				StringValue = tag.Name
			};

			if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(packet);
			else PacketSender.SendToHost(packet);
		}
	}

	/// <summary>
	/// Sync storage filter add / remove tag.
	///
	/// Only a call that changes the filter is sent. The game returns from
	/// AddTagToFilter at once when the tag is already accepted (and from
	/// RemoveTagFromFilter when it is not), but a postfix still runs - and the
	/// side screen re-adds every accepted tag whenever it is rebuilt, which
	/// BuildingConfigPacket does on each packet it applies (RefreshSideScreenIfOpen).
	/// With two players looking at the same Storage Bin or Critter Feeder that was a
	/// ping-pong of no-op packets: 42 800 on the host and 30 600 on one client in a
	/// single session, in bursts of 500 a second, and those bursts are what put the
	/// reliable channel minutes behind.
	/// </summary>
	[HarmonyPatch(typeof(TreeFilterable), nameof(TreeFilterable.AddTagToFilter))]
	public static class TreeFilterable_AddTagToFilter_Patch
	{
		public static void Prefix(TreeFilterable __instance, Tag t, out bool __state)
		{
			__state = __instance.ContainsTag(t);
		}

		public static void Postfix(TreeFilterable __instance, Tag t, bool __state)
		{
			using var _ = Profiler.Scope();

			if (__state) return; // already accepted: the game changed nothing
			if (BuildingConfigPacket.IsApplyingPacket) return;
			if (!MultiplayerSession.InActiveSession) return;

			TreeFilterableSync.SendFilterChange(__instance, "StorageFilterAdd", t);
		}
	}

	[HarmonyPatch(typeof(TreeFilterable), nameof(TreeFilterable.RemoveTagFromFilter))]
	public static class TreeFilterable_RemoveTagFromFilter_Patch
	{
		public static void Prefix(TreeFilterable __instance, Tag t, out bool __state)
		{
			__state = __instance.ContainsTag(t);
		}

		public static void Postfix(TreeFilterable __instance, Tag t, bool __state)
		{
			using var _ = Profiler.Scope();

			if (!__state) return; // was not accepted: the game changed nothing
			if (BuildingConfigPacket.IsApplyingPacket) return;
			if (!MultiplayerSession.InActiveSession) return;

			TreeFilterableSync.SendFilterChange(__instance, "StorageFilterRemove", t);
		}
	}

	internal static class TreeFilterableSync
	{
		internal static void SendFilterChange(TreeFilterable filterable, string configKey, Tag t)
		{
			using var _ = Profiler.Scope();

			var identity = filterable.gameObject.AddOrGet<NetworkIdentity>();
			identity.RegisterIdentity();

			var packet = new BuildingConfigPacket
			{
				NetId = identity.NetId,
				Cell = Grid.PosToCell(filterable.gameObject),
				ConfigHash = configKey.GetHashCode(),
				Value = 0,
				ConfigType = BuildingConfigType.String,
				StringValue = t.Name
			};

			if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(packet);
			else PacketSender.SendToHost(packet);
		}
	}
}
