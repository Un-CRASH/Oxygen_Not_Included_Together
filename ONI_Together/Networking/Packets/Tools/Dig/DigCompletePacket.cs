using ONI_Together.DebugTools;
using ONI_Together.Networking;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools;
using System.IO;
using Shared.Profiling;
using UnityEngine;

public class DigCompletePacket : IPacket
{
	public int Cell;
	public float Mass;
	public float Temperature;
	public ushort ElementIdx;
	public byte DiseaseIdx;
	public int DiseaseCount;

	public void Serialize(BinaryWriter writer)
	{
		using var _ = Profiler.Scope();

		writer.Write(Cell);
		writer.Write(Mass);
		writer.Write(Temperature);
		writer.Write(ElementIdx);
		writer.Write(DiseaseIdx);
		writer.Write(DiseaseCount);
	}

	public void Deserialize(BinaryReader reader)
	{
		using var _ = Profiler.Scope();

		Cell = reader.ReadInt32();
		Mass = reader.ReadSingle();
		Temperature = reader.ReadSingle();
		ElementIdx = reader.ReadUInt16();
		DiseaseIdx = reader.ReadByte();
		DiseaseCount = reader.ReadInt32();
	}

	public void OnDispatched()
	{
		using var _ = Profiler.Scope();

		if (MultiplayerSession.IsHost)
			return;

		if (!Grid.IsValidCell(Cell))
		{
			DebugConsole.LogWarning($"[DigCompletePacket] Invalid cell {Cell}");
			return;
		}

		using var scope = OrderApplyScope.Enter();
		bool wasSolid = Grid.Solid[Cell];

		// Destroy the dig placer (Grid.Objects is indexed by ObjectLayer; the old loop
		// used the SceneLayer count as its bound).
		GameObject placer = Grid.Objects[Cell, (int)ObjectLayer.DigPlacer];
		if (placer != null && placer.HasTag(new Tag("DigPlacer")))
			Util.KDestroyGameObject(placer);

		// Spawn ore + FX from the dig
		//WorldDamage.Instance.OnDigComplete(Cell, Mass, Temperature, ElementIdx, DiseaseIdx, DiseaseCount);
		// Destroy cell via sim
		WorldDamage.Instance.DestroyCell(Cell);
		// Trigger on solid state changed
		WorldDamage.Instance.OnSolidStateChanged(Cell);
		DebugConsole.Log($"[DigCompletePacket] Destroyed cell {Cell}" + (wasSolid ? "" : " (was not solid here)"));
	}
}
