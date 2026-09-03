using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	/// <summary>
	/// Host -> clients: one pickupable absorbed another.
	///
	/// Pickupable.Absorb adds the absorbed stack to the absorber and destroys it.
	/// Until now a client heard only the second half, as the GroundItemPickedUpPacket
	/// sent from OnCleanUp of the absorbed one: it deleted its copy, and the mass of
	/// the absorber stayed what it was. Every ore chunk that landed on a pile lost
	/// its mass on the client unless the own copy of the client happened to land and
	/// merge first.
	///
	/// This carries both halves. AbsorberOnGround is false when the absorber sits in
	/// a container: the client rebuilds container contents from the blobs the host
	/// sends and those carry no NetIds, so only the absorbed copy is acted on then.
	/// </summary>
	public class PickupableMergePacket : IPacket
	{
		public int AbsorberNetId;
		public int AbsorbedNetId;
		public float AbsorberUnits;
		public bool AbsorberOnGround;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(AbsorberNetId);
			writer.Write(AbsorbedNetId);
			writer.Write(AbsorberUnits);
			writer.Write(AbsorberOnGround);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			AbsorberNetId = reader.ReadInt32();
			AbsorbedNetId = reader.ReadInt32();
			AbsorberUnits = reader.ReadSingle();
			AbsorberOnGround = reader.ReadBoolean();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			Pickupable absorber = null;
			Pickupable absorbed = null;
			if (AbsorberNetId != 0)
				NetworkIdentityRegistry.TryGetComponent(AbsorberNetId, out absorber);
			if (AbsorbedNetId != 0)
				NetworkIdentityRegistry.TryGetComponent(AbsorbedNetId, out absorbed);

			if (!AbsorberOnGround)
			{
				// Merged into a container; the container is resynced from its blob.
				if (absorbed != null)
					Util.KDestroyGameObject(absorbed.gameObject);
				return;
			}

			if (absorber != null)
			{
				SetUnits(absorber, AbsorberUnits);
				if (absorbed != null && !ReferenceEquals(absorbed, absorber))
					Util.KDestroyGameObject(absorbed.gameObject);
				return;
			}

			if (absorbed != null)
			{
				// Our copy of the absorber is missing: it never spawned here, or the
				// merge went the other way on this side. The absorbed copy becomes the
				// absorber - it takes the merged amount and the NetId later packets
				// will use.
				SetUnits(absorbed, AbsorberUnits);
				var identity = absorbed.GetComponent<NetworkIdentity>();
				if (identity != null)
					identity.OverrideNetId(AbsorberNetId);
				return;
			}

			DebugConsole.Log($"[PickupableMergePacket] Neither {AbsorberNetId} nor {AbsorbedNetId} is known here; nothing to merge.");
		}

		private static void SetUnits(Pickupable pickupable, float units)
		{
			var element = pickupable.GetComponent<PrimaryElement>();
			if (element != null && units > 0f)
				element.Units = units;
		}
	}
}
