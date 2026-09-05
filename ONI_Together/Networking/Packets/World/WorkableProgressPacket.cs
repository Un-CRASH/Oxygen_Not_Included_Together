using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System.Collections;
using System.IO;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	internal class WorkableProgressPacket : IPacket
	{
		private int TargetNetId;
		private string TargetTypeName;
		private RemoteProgressKind ProgressKind;
		private float PercentComplete;
		private bool ShowProgressBar;
		private float WorkTimeRemaining;
		private float WorkTimeTotal;

		public WorkableProgressPacket() { }

		public WorkableProgressPacket(Workable workable)
		{
			using var _ = Profiler.Scope();

			PopulateFromWorkable(workable, showProgressBar: true);
		}

		public static WorkableProgressPacket CreateHidden(Workable workable)
		{
			using var _ = Profiler.Scope();

			var packet = new WorkableProgressPacket();
			packet.PopulateFromWorkable(workable, showProgressBar: false);
			return packet;
		}

		public static WorkableProgressPacket CreateComplexFabricator(ComplexFabricator fabricator, bool showProgressBar)
		{
			using var _ = Profiler.Scope();

			var packet = new WorkableProgressPacket
			{
				TargetNetId = fabricator.GetNetId(),
				TargetTypeName = fabricator.GetType().AssemblyQualifiedName,
				ProgressKind = RemoteProgressKind.ComplexFabricatorOrder,
				PercentComplete = Mathf.Clamp01(fabricator.OrderProgress),
				ShowProgressBar = showProgressBar,
				WorkTimeRemaining = showProgressBar ? 1f - Mathf.Clamp01(fabricator.OrderProgress) : 0f,
				WorkTimeTotal = 1f
			};
			return packet;
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(TargetNetId);
			writer.Write(TargetTypeName ?? string.Empty);
			writer.Write((int)ProgressKind);
			writer.Write(PercentComplete);
			writer.Write(ShowProgressBar);
			writer.Write(WorkTimeRemaining);
			writer.Write(WorkTimeTotal);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			TargetNetId = reader.ReadInt32();
			TargetTypeName = reader.ReadString();
			ProgressKind = (RemoteProgressKind)reader.ReadInt32();
			PercentComplete = reader.ReadSingle();
			ShowProgressBar = reader.ReadBoolean();
			WorkTimeRemaining = reader.ReadSingle();
			WorkTimeTotal = reader.ReadSingle();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost)
				return;

			if (TryApply())
				return;

			if (Game.Instance != null)
			{
				Game.Instance.StartCoroutine(RetryApply(Clone()));
			}
		}

		private void PopulateFromWorkable(Workable workable, bool showProgressBar)
		{
			using var _ = Profiler.Scope();

			TargetNetId = workable.GetNetId();
			TargetTypeName = workable.GetType().AssemblyQualifiedName;
			ProgressKind = RemoteProgressKind.WorkablePercent;
			PercentComplete = Mathf.Clamp01(workable.GetPercentComplete());
			ShowProgressBar = showProgressBar;
			WorkTimeRemaining = showProgressBar ? workable.WorkTimeRemaining : 0f;
			WorkTimeTotal = workable.GetWorkTime();
		}

		private WorkableProgressPacket Clone()
		{
			return new WorkableProgressPacket
			{
				TargetNetId = TargetNetId,
				TargetTypeName = TargetTypeName,
				ProgressKind = ProgressKind,
				PercentComplete = PercentComplete,
				ShowProgressBar = ShowProgressBar,
				WorkTimeRemaining = WorkTimeRemaining,
				WorkTimeTotal = WorkTimeTotal
			};
		}

		private bool TryApply()
		{
			using var _ = Profiler.Scope();

			switch (ProgressKind)
			{
				case RemoteProgressKind.WorkablePercent:
					return TryApplyWorkableProgress();

				case RemoteProgressKind.ComplexFabricatorOrder:
					return TryApplyComplexFabricatorProgress();

				default:
					return true;
			}
		}

		private bool TryApplyWorkableProgress()
		{
			using var _ = Profiler.Scope();

			if (!NetworkIdentityRegistry.TryGet(TargetNetId, out var identity) || identity == null || identity.gameObject.IsNullOrDestroyed())
				return false;

			Workable workable = null;
			if (!string.IsNullOrEmpty(TargetTypeName))
			{
				var workableType = AccessTools.TypeByName(TargetTypeName);
				if (workableType == null)
					return false;

				workable = identity.gameObject.GetComponent(workableType) as Workable;
			}

			workable ??= identity.gameObject.GetComponent<Workable>();
			if (workable == null)
				return false;

			if (WorkTimeTotal > 0f && !float.IsInfinity(WorkTimeTotal) && !float.IsNaN(WorkTimeTotal))
			{
				workable.SetWorkTime(WorkTimeTotal);
				workable.WorkTimeRemaining = Mathf.Clamp(WorkTimeRemaining, 0f, WorkTimeTotal);
			}

			workable.ShowProgressBar(ShowProgressBar);
			if (ShowProgressBar)
			{
				RemoteProgressRegistry.SetProgress(TargetNetId, ProgressKind, PercentComplete, true, WorkTimeRemaining, WorkTimeTotal);
			}
			else
			{
				RemoteProgressRegistry.Clear(TargetNetId, ProgressKind, hideTarget: false);
			}

			return true;
		}

		private bool TryApplyComplexFabricatorProgress()
		{
			using var _ = Profiler.Scope();

			if (!NetworkIdentityRegistry.TryGetComponent<ComplexFabricator>(TargetNetId, out var fabricator) || fabricator == null || fabricator.gameObject.IsNullOrDestroyed())
				return false;

			fabricator.OrderProgress = Mathf.Clamp01(PercentComplete);
			fabricator.ShowProgressBar(ShowProgressBar);
			if (ShowProgressBar)
			{
				RemoteProgressRegistry.SetProgress(TargetNetId, ProgressKind, PercentComplete, true, WorkTimeRemaining, WorkTimeTotal);
			}
			else
			{
				RemoteProgressRegistry.Clear(TargetNetId, ProgressKind, hideTarget: false);
			}

			return true;
		}

		/// <summary>
		/// A pickupable inside a storage (food in an inventory or on a table, ore in a bin)
		/// never exists on clients, which destroy items when they are stored; progress for it
		/// would only be retried and reported as unresolved, so the host does not send it.
		/// </summary>
		internal static bool IsStoredPickupable(Workable workable)
		{
			return workable != null && workable.TryGetComponent<Pickupable>(out var pickupable) && pickupable.storage != null;
		}

		private static string ShortTypeName(string assemblyQualifiedName)
		{
			if (string.IsNullOrEmpty(assemblyQualifiedName))
				return "?";
			int comma = assemblyQualifiedName.IndexOf(',');
			return comma > 0 ? assemblyQualifiedName.Substring(0, comma) : assemblyQualifiedName;
		}

		private static IEnumerator RetryApply(WorkableProgressPacket packet)
		{
			for (int attempt = 0; attempt < 12; attempt++)
			{
				yield return null;

				if (!MultiplayerSession.InActiveSession || MultiplayerSession.IsHost)
					yield break;

				if (packet.TryApply())
					yield break;
			}

			string typeName = ShortTypeName(packet.TargetTypeName);
			DebugConsole.LogAggregated("WorkableProgress.Unresolved." + typeName, $"[WorkableProgressPacket] Failed to resolve target {packet.TargetNetId} ({typeName})");
		}
	}
}
