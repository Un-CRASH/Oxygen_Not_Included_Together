using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World.Buildings
{
	internal class ComplexFabricatorSpawnProductPacket : IPacket
	{
		public int NetId, CompletedRecipeIdx;
		public ComplexFabricatorSpawnProductPacket() { }
		public ComplexFabricatorSpawnProductPacket(ComplexFabricator cf)
		{
			using var _ = Profiler.Scope();

			NetId = cf.GetNetId();
			CompletedRecipeIdx = cf.CurrentOrderIdx;
		}
		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(NetId);
			writer.Write(CompletedRecipeIdx);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			NetId = reader.ReadInt32();
			CompletedRecipeIdx = reader.ReadInt32();
		}


		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if(!NetworkIdentityRegistry.TryGetComponent<ComplexFabricator>(NetId, out var fab))
			{
				DebugConsole.LogWarning("[ComplexFabricatorSpawnProductPacket] Could not find ComplexFabricator for netId " + NetId);
				return;
			}

			if (fab.recipe_list == null || CompletedRecipeIdx < 0 || CompletedRecipeIdx >= fab.recipe_list.Length)
			{
				DebugConsole.LogWarning($"[ComplexFabricatorSpawnProductPacket] {fab.name} (NetId {NetId}) has no recipe at index {CompletedRecipeIdx}");
				return;
			}

			ComplexRecipe complexRecipe = fab.recipe_list[CompletedRecipeIdx];
			DebugConsole.Log($"[ComplexFabricatorSpawnProductPacket] spawning product {complexRecipe.id} for {fab.name} with netId {NetId}");
			try
			{
				fab.SpawnOrderProduct(complexRecipe);
			}
			catch (Exception ex)
			{
				// The client's copy of the fabricator can be a step behind the host's order list.
				DebugConsole.LogWarning($"[ComplexFabricatorSpawnProductPacket] Could not spawn {complexRecipe.id} on {fab.name}: {ex.GetType().Name}: {ex.Message}");
			}
			RemoteProgressRegistry.Clear(NetId, RemoteProgressKind.ComplexFabricatorOrder);
		}
	}
}
