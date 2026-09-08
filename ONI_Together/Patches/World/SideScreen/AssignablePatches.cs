using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.World;
using Shared.Profiling;

namespace ONI_Together.Patches.World.SideScreen
{
    /// <summary>
    /// Patches for Assignable synchronization (Outhouse, Lavatory, Triage Cot, etc.)
    /// </summary>
    [HarmonyPatch(typeof(Assignable), nameof(Assignable.OnSpawn))]
    public static class Assignable_OnSpawn_Patch
    {
        public static void Postfix(Assignable __instance)
        {
	        using var _ = Profiler.Scope();

            var buildingIdentity = __instance.gameObject.AddOrGet<NetworkIdentity>();
            buildingIdentity.RegisterIdentity();
        }
    }

    [HarmonyPatch(typeof(Assignable), nameof(Assignable.Assign), typeof(IAssignableIdentity))]
	public static class Assignable_Assign_Patch
	{
		/// <summary>The assignee before the call: only a change is an order worth sending.</summary>
		public static void Prefix(Assignable __instance, out IAssignableIdentity __state)
		{
			__state = __instance != null ? __instance.assignee : null;
		}

		public static void Postfix(Assignable __instance, IAssignableIdentity new_assignee, IAssignableIdentity __state)
		{
			using var _ = Profiler.Scope();

			if (AssignmentPacket.IsApplying) return;
			if (!MultiplayerSession.InActiveSession) return;
			if (__instance == null || __instance.gameObject == null) return;
            if (__instance.IsNullOrDestroyed()) return;

			// The game re-applies assignments it already has (the suit locker and the
			// ownables side screen both do), and the reaction to an applied packet
			// lands a few frames later, outside IsApplying. Neither changed anything.
			if (ReferenceEquals(__state, new_assignee)) return;

            var buildingIdentity = __instance.gameObject.GetComponent<NetworkIdentity>();
			if (!buildingIdentity || buildingIdentity.NetId == 0)
                return;

            int assigneeNetId = -1;
			string groupId = "";

			if (new_assignee == null)
			{
				assigneeNetId = -1;
            }
            else if (new_assignee is AssignmentGroup group)
			{
				groupId = group.id;
            }
            else if (new_assignee is MinionAssignablesProxy proxy)
			{
                var targetGO = proxy.GetTargetGameObject();
				if (targetGO != null)
				{
                    var minionNetId = targetGO.GetComponent<NetworkIdentity>();
					if (minionNetId != null)
					{
                        assigneeNetId = minionNetId.NetId;
					}
				}
			}
			else if (new_assignee is KMonoBehaviour mb)
			{
                var minionNetId = mb.gameObject.GetComponent<NetworkIdentity>();
				if (minionNetId != null)
				{
                    minionNetId.RegisterIdentity();
					assigneeNetId = minionNetId.NetId;
                }
            }

            var packet = new AssignmentPacket
			{
				BuildingNetId = buildingIdentity.NetId,
				Cell = Grid.PosToCell(__instance.gameObject),
				AssigneeNetId = assigneeNetId,
				GroupId = groupId,
				Sender = NetworkConfig.GetLocalID()
			};

            if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(packet);
			else PacketSender.SendToHost(packet);
		}
	}

    [HarmonyPatch(typeof(Assignable), nameof(Assignable.Unassign))]
	public static class Assignable_Unassign_Patch
	{
		public static void Prefix(Assignable __instance, out bool __state)
		{
			__state = __instance != null && __instance.assignee != null;
		}

		public static void Postfix(Assignable __instance, bool __state)
		{
			using var _ = Profiler.Scope();

			if (AssignmentPacket.IsApplying) return;
			if (!MultiplayerSession.InActiveSession) return;
			if (__instance.IsNullOrDestroyed()) return;

			// Nothing was assigned: Unassign is called on every locker refresh and as
			// the game's delayed reaction to an unassign we applied from a packet.
			if (!__state) return;

			var buildingIdentity = __instance.gameObject.GetComponent<NetworkIdentity>();
			if (!buildingIdentity || buildingIdentity.NetId == 0)
				return;

			var packet = new AssignmentPacket
			{
				BuildingNetId = buildingIdentity.NetId,
				Cell = Grid.PosToCell(__instance.gameObject),
				AssigneeNetId = -1,
				GroupId = "",
				Sender = NetworkConfig.GetLocalID()
			};

			if (MultiplayerSession.IsHost) PacketSender.SendToAllClients(packet);
			else PacketSender.SendToHost(packet);

			DebugConsole.Log($"[Assignable_Unassign_Patch] Unassigned {__instance.name}");
		}
	}
}
