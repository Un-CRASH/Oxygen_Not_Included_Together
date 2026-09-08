using HarmonyLib;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.World;
using ONI_Together.Networking.Packets.World.Buildings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Patches.World
{
	internal class MinionIdentity_Patches
	{
		static bool ApplyingPacket = false;
		public static void ApplyPacketName(MinionIdentity nameable, string name)
		{
			using var _ = Profiler.Scope();

			ApplyingPacket = true;
			nameable.SetName(name);
			ApplyingPacket = false;
		}

		[HarmonyPatch(typeof(MinionIdentity), nameof(MinionIdentity.SetName))]
		public class MinionIdentity_SetName_Patch
		{
			public static void Postfix(MinionIdentity __instance, string name)
			{
				using var _ = Profiler.Scope();

				if (MultiplayerSession.NotInSession)
					return;

				if (ApplyingPacket)
					return;
				// The printing pod names its candidate duplicants too; they exist only inside
				// the host's screen, have no identity and are unknown to every client.
				var identity = __instance.gameObject.GetExistingNetIdentity();
				if (identity == null || identity.NetId == 0 || !NetworkIdentityRegistry.Exists(identity.NetId))
					return;
				PacketSender.SendToAllOtherPeers(new MinionIdentitySetNamePacket(identity.NetId, name));
			}
		}
	}
}
