using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;
using UnityEngine;
using static RancherChore;

namespace ONI_Together.Networking.Packets.Animation
{
	internal class StandardWorker_WorkingState_Packet : IPacket
	{
		public StandardWorker_WorkingState_Packet() { }

		public StandardWorker_WorkingState_Packet(StandardWorker worker, Workable workable, bool startedWorking)
		{
			using var _ = Profiler.Scope();

			WorkerNetId = worker.GetNetId();
			StartingToWork = startedWorking;
			if (startedWorking)
			{
				WorkableNetId = workable.GetNetId();
				WorkableType = workable.GetType().AssemblyQualifiedName;
			}
		}

		int WorkerNetId, WorkableNetId;
		string WorkableType;
		bool StartingToWork;

		/// <summary>
		/// A start that could not bind yet (the workable's identity has not registered,
		/// the placer is a frame away) waits up to this long, retried a few times a second.
		/// The old retry gave up after ten frames (~0.17 s), which is why so many of the
		/// host's "started working" packets ended as "Could not resolve workable". One
		/// entry per worker, newest wins: any later packet for the same duplicant - a
		/// stop, or a start on something else - drops the parked one, so a stale start
		/// can never re-bind a duplicant that has moved on.
		/// </summary>
		private const float PendingSeconds = 5f;
		private const float PendingInterval = 0.25f;
		private static readonly Dictionary<int, StandardWorker_WorkingState_Packet> _pendingByWorker = new();
		private float _deadline;

		internal static void ResetState() => _pendingByWorker.Clear();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(WorkerNetId);
			writer.Write(StartingToWork);
			if (StartingToWork)
			{
				writer.Write(WorkableNetId);
				writer.Write(WorkableType);
			}
		}
		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			WorkerNetId = reader.ReadInt32();
			StartingToWork = reader.ReadBoolean();
			if (StartingToWork)
			{
				WorkableNetId = reader.ReadInt32();
				WorkableType = reader.ReadString();
			}
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			// Any packet for this worker supersedes whatever is parked for it.
			_pendingByWorker.Remove(WorkerNetId);

			if (TryApply())
				return;

			if (StartingToWork && Game.Instance != null)
			{
				var pending = Clone();
				pending._deadline = Time.unscaledTime + PendingSeconds;
				_pendingByWorker[WorkerNetId] = pending;
				Game.Instance.StartCoroutine(RetryStartWork(pending));
			}
		}

		private bool TryApply(bool logFailure = false)
		{
			using var _ = Profiler.Scope();

			if (!NetworkIdentityRegistry.TryGetComponent<StandardWorker>(WorkerNetId, out var worker))
			{
				if (logFailure)
				{
					DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] Could not find worker {WorkerNetId}");
				}
				return false;
			}

			GameObject workableGO = null;
			if (!StartingToWork)
			{
				worker.StopWork();
				if (DebugConsole.IsVerbose) DebugConsole.LogVerbose("[StandardWorker_WorkingState_Packet] workable change triggered for " + worker.name + ": stopped working");
				return true;
			}

			if (!NetworkIdentityRegistry.TryGetComponent<Workable>(WorkableNetId, out var protoWorkable))
			{
				if (logFailure)
				{
					DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] Could not resolve workable {WorkableNetId} for worker {worker.name}");
				}
				return false;
			}

			workableGO = protoWorkable.gameObject;

			var workableType = AccessTools.TypeByName(WorkableType);
			if (workableType == null)
			{
				if (logFailure)
				{
					DebugConsole.LogWarning("Could not find workable type " + WorkableType);
				}
				return false;
			}

			var targetWorkableCmp = workableGO.GetComponent(workableType);
			if (targetWorkableCmp == null || targetWorkableCmp is not Workable workable)
			{
				if (logFailure)
				{
					DebugConsole.LogWarning("Could not find workable of type " + WorkableType + " on " + workableGO.GetProperName());
				}
				return false;
			}

						if (workable is Pickupable)
						{
							DebugConsole.Log($"[StandardWorker_WorkingState_Packet] Ignoring Pickupable start-work for {worker.name} (fetch sync is separate)");
							return true;
						}

						// StandardWorker.StartWork subscribes to the workable's events; on a
						// workable that never spawned that is KMonoBehaviour.Subscribe on a null
						// KObject, and the game logs the exception as an error - the crash screen.
						// Handled, not retried: the object will not spawn in the next ten frames.
						if (!workable.isSpawned)
						{
							DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] {worker.name} cannot start on {workableGO.name} (NetId {WorkableNetId}): the object never spawned");
							return true;
						}

			try
			{
				if (!worker.state.Equals(StandardWorker.State.Idle))
				{
					worker.StopWork();
				}
				worker.StartWork(new(workable));
			}
			catch (System.Exception ex)
			{
				if (logFailure)
				{
					DebugConsole.LogWarning($"[StandardWorker_WorkingState_Packet] StartWork failed for {worker.name} on {workableGO.name}: {ex.GetType().Name}");
				}
				return false;
			}

			if (DebugConsole.IsVerbose) DebugConsole.LogVerbose("[StandardWorker_WorkingState_Packet] workable change triggered for " + worker.name + ": Started working on " + workableGO.name);
			return true;
		}

		private StandardWorker_WorkingState_Packet Clone()
		{
			return new StandardWorker_WorkingState_Packet
			{
				WorkerNetId = WorkerNetId,
				WorkableNetId = WorkableNetId,
				WorkableType = WorkableType,
				StartingToWork = StartingToWork
			};
		}

		private static IEnumerator RetryStartWork(StandardWorker_WorkingState_Packet packet)
		{
			while (Time.unscaledTime < packet._deadline)
			{
				yield return new WaitForSecondsRealtime(PendingInterval);

				if (!MultiplayerSession.InActiveSession || MultiplayerSession.IsHost)
					yield break;
				if (!_pendingByWorker.TryGetValue(packet.WorkerNetId, out var current) || !ReferenceEquals(current, packet))
					yield break; // superseded by a later packet for this worker

				if (packet.TryApply())
				{
					_pendingByWorker.Remove(packet.WorkerNetId);
					yield break;
				}
			}

			if (_pendingByWorker.TryGetValue(packet.WorkerNetId, out var last) && ReferenceEquals(last, packet))
			{
				_pendingByWorker.Remove(packet.WorkerNetId);
				packet.TryApply(logFailure: true);
			}
		}
	}
}
