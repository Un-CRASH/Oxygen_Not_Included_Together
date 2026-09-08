using ONI_Together.DebugTools;
using System;

namespace ONI_Together.Networking.Packets.Tools
{
	/// <summary>
	/// One counter for "an order packet is being applied on this side" and one for "the
	/// local player's drag tool is running", instead of a flag per packet type.
	///
	/// Every order packet used to carry its own ProcessingIncoming, and the patches that
	/// echo game events into packets checked only the flags they knew about. So a dig
	/// order applied on the host set the placer's priority, PrioritizablePatch saw no
	/// flag it recognised and sent a PrioritizeStatePacket back, and the host relayed it
	/// to every client including the one that placed the dig. Cancel and deconstruct
	/// drags sent both the drag packet and a per-building packet, because the
	/// per-building patches only suppressed themselves while a REMOTE drag was applied.
	///
	/// A packet enters the apply scope for the whole of OnDispatched; a drag tool patch
	/// enters the local-drag scope around OnDragTool. The echo patches test both.
	/// </summary>
	internal static class OrderApplyScope
	{
		private static int _applyDepth;
		private static int _localDragDepth;

		public static bool IsApplying => _applyDepth > 0;
		public static bool LocalDragInProgress => _localDragDepth > 0;

		/// <summary>True while a remote order is applied or a local drag runs: the patches that turn game events into packets stay quiet.</summary>
		public static bool SuppressEchoes => _applyDepth > 0 || _localDragDepth > 0;

		public static Token Enter()
		{
			_applyDepth++;
			return new Token(false);
		}

		public static Token EnterLocalDrag()
		{
			_localDragDepth++;
			return new Token(true);
		}

		public struct Token : IDisposable
		{
			private readonly bool _localDrag;
			private bool _disposed;

			internal Token(bool localDrag)
			{
				_localDrag = localDrag;
				_disposed = false;
			}

			public void Dispose()
			{
				if (_disposed) return;
				_disposed = true;
				if (_localDrag)
				{
					if (_localDragDepth > 0) _localDragDepth--;
				}
				else if (_applyDepth > 0)
				{
					_applyDepth--;
				}
			}
		}

		/// <summary>
		/// Session end and the hard-sync reload. A counter left non-zero by an exception
		/// path would silence a whole family of orders for the rest of the process.
		/// </summary>
		public static void Reset()
		{
			if (_applyDepth != 0 || _localDragDepth != 0)
				DebugConsole.LogWarning($"[OrderApplyScope] Reset with apply depth {_applyDepth}, local drag depth {_localDragDepth}");
			_applyDepth = 0;
			_localDragDepth = 0;
		}
	}
}

namespace ONI_Together.Networking.Packets.Tools
{
	/// <summary>
	/// The static suppression flags of the order packets, reset together at session end
	/// and at the hard-sync reload. None of them used to be, so one exception on an
	/// apply path could leave a flag set and silence that order type for good.
	/// </summary>
	internal static class OrderSyncState
	{
		public static void ResetAll()
		{
			OrderApplyScope.Reset();
			DragToolPacket.ResetState();
			Clear.ClearableActionPacket.ProcessingIncoming = false;
			BuildingActionCellPacket.ProcessingIncoming = false;
			Dig.DiggablePacket.ResetState();
			Build.UtilityBuildPacket.ResetState();
			World.PrioritizeStatePacket.IsApplying = false;
			Patches.World.ClearablePatches.ResetState();
		}
	}
}
