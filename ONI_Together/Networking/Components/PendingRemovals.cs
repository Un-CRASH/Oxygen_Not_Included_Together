using System.Collections.Generic;
using ONI_Together.DebugTools;
using UnityEngine;

namespace ONI_Together.Networking.Components
{
	/// <summary>
	/// Client side: NetIds the host has already removed (picked up, stored, destroyed)
	/// that resolved to nothing when the removal arrived.
	///
	/// The removal is applied the moment an object registers under that id, whatever
	/// path registers it. Until now the two pending sets lived in
	/// GroundItemPickedUpPacket and StorageItemPacket and were consulted in exactly two
	/// places, the two substance spawn packets; an id that arrived any other way - a
	/// prefab spawn, a container drop, a save reload - was never checked, and the
	/// entries sat there for the whole session: 7 600 queued, 2 consumed, in one log.
	/// Every such entry was a ghost item on the client's floor that the host no longer
	/// had, and each ghost fed unresolvable sweep packets back to the host.
	///
	/// Entries expire: most of them are ids the client will never see (items that were
	/// never announced to it), and a stale entry must not kill an unrelated object that
	/// hashes to the same id much later.
	/// </summary>
	public static class PendingRemovals
	{
		public const float TtlSeconds = 300f;

		private static readonly Dictionary<int, float> _queuedAt = new();
		private static int _expired;

		public static int Count => _queuedAt.Count;

		public static void Add(int netId)
		{
			if (netId == 0)
				return;
			_queuedAt[netId] = Time.unscaledTime;
			if ((_queuedAt.Count & 255) == 0)
				Expire();
		}

		public static bool TryConsume(int netId)
		{
			return netId != 0 && _queuedAt.Remove(netId);
		}

		public static void Clear()
		{
			int n = _queuedAt.Count;
			_queuedAt.Clear();
			if (n > 0)
				DebugConsole.Log($"[PendingRemovals] cleared {n} entries");
		}

		private static void Expire()
		{
			float now = Time.unscaledTime;
			List<int> old = null;
			foreach (var kv in _queuedAt)
			{
				if (now - kv.Value > TtlSeconds)
					(old ??= new List<int>()).Add(kv.Key);
			}
			if (old == null)
				return;
			foreach (var id in old)
				_queuedAt.Remove(id);
			_expired += old.Count;
			DebugConsole.LogAggregated("PendingRemoval.Expired", $"[PendingRemovals] {old.Count} removals expired unresolved after {TtlSeconds:F0} s ({_expired} total, {_queuedAt.Count} still pending)");
		}
	}
}
