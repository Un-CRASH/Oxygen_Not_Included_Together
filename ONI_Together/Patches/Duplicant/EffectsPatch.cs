using HarmonyLib;
using Klei.AI;
using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.DuplicantActions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Patches.Duplicant
{
	internal class EffectsPatch
	{
		private static bool TogglingEffectFromPacket = false;

		public static void AddEffect(Effects minionEffects, string effectId, bool shouldSave)
		{
			using var _ = Profiler.Scope();

			TogglingEffectFromPacket = true;

			Effect newEffect = Db.Get().effects.TryGet(effectId);
			if (newEffect != null)
			{
				minionEffects.Add(newEffect, shouldSave);
			}
			else
				DebugConsole.LogWarning("Could not find effect with id " + effectId);

			TogglingEffectFromPacket = false;
		}
		public static void RemoveEffect(Effects minionEffects, HashedString effectId)
		{
			using var _ = Profiler.Scope();

			TogglingEffectFromPacket = true;

			minionEffects.Remove(effectId);

			TogglingEffectFromPacket = false;
		}



		/// <summary>
		/// The game re-adds a running effect every tick to refresh its timer (one host
		/// sent 219 ToggleEffectPackets a second for six duplicants). A refresh of an
		/// effect the clients already have is sent at most every few seconds; a new
		/// effect always.
		/// </summary>
		private static readonly Dictionary<(int, HashedString), float> _lastRefreshSent = new Dictionary<(int, HashedString), float>();
		private const float RefreshIntervalSeconds = 5f;

		private static bool ShouldSendAdd(Effects effects, Effect effect)
		{
			if (effect == null) return false;
			if (!effects.HasEffect(effect)) return true;
			var key = (effects.GetInstanceID(), (HashedString)effect.Id);
			float now = UnityEngine.Time.unscaledTime;
			if (_lastRefreshSent.TryGetValue(key, out var last) && now - last < RefreshIntervalSeconds)
				return false;
			_lastRefreshSent[key] = now;
			if (_lastRefreshSent.Count > 8192)
				_lastRefreshSent.Clear();
			return true;
		}

		[HarmonyPatch(typeof(Effects), nameof(Effects.Add), [typeof(Effect), typeof(bool), typeof(Func<string, object, string>)])]
		public class TargetType_TargetMethod_Patch
		{
			public static bool Prefix(Effects __instance, Effect newEffect, bool should_save)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.InActiveSession) return true;

				if (!__instance.HasTag(GameTags.BaseMinion))
					return true;

				if (MultiplayerSession.IsClient && !TogglingEffectFromPacket)
					return false;

				if (MultiplayerSession.IsHost && ShouldSendAdd(__instance, newEffect))
					PacketSender.SendToAllClients(new ToggleEffectPacket(__instance, newEffect, should_save));

				return true;
			}
		}

		[HarmonyPatch(typeof(Effects), nameof(Effects.Remove), [typeof(HashedString)])]
		public class Effects_Remove_Patch
		{
			public static bool Prefix(Effects __instance, HashedString effect_id)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.InActiveSession) return true;
				if (MultiplayerSession.IsClient && !TogglingEffectFromPacket)
					return false;

				if (!__instance.HasTag(GameTags.BaseMinion))
					return true;

				// Nothing to remove here means nothing to remove on the clients either.
				if (MultiplayerSession.IsHost && __instance.HasEffect(effect_id))
					PacketSender.SendToAllClients(new ToggleEffectPacket(__instance, effect_id));

				return true;
			}
		}
	}
}
