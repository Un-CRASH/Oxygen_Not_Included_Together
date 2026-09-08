using KSerialization;
using ONI_Together.Misc;
using Shared.OxySync;
using ONI_Together.Networking.OxySync;
using Shared.OxySync.Attributes;
using UnityEngine;

namespace ONI_Together.Networking.OxySync.Components
{
    [SkipSaveFileSerialization]
    // Group -1: the blob goes to every player, not only to those looking at the chunk.
    // Contents changed out of view were never sent (no snapshot for a chunk nobody
    // subscribed to, deltas only to subscribers), so a client's fridges, lockers and
    // feeders drifted from the host's until the camera passed over them - visible as
    // a colony calorie count and resource totals that differed between the windows.
    [FixedInterestGroup]
    public class StorageSyncer : NetworkBehaviour
    {
        private Storage _storage;
        private bool _storageDirty;
        private float _syncTimer;
        private const float STORAGE_SYNC_DELAY = 0.2f;
        // A container nobody is looking at (a generator burning fuel changes its storage
        // every sim tick) is coalesced harder; the total still reaches everyone.
        private const float STORAGE_SYNC_DELAY_UNWATCHED = 2f;

        [SyncVar(Hook = nameof(OnStorageChanged), SendMode = (int) PacketSendMode.ReliableImmediate)]
        private byte[] _storageBlob;

        public override void OnSpawn()
        {
            base.OnSpawn();
            _storage = GetComponent<Storage>();

            if (_storage != null)
			{
                _storage.OnStorageChange += OnLocalStorageChanged;
				// Populate the first authoritative snapshot even when the storage was
				// already filled before the multiplayer session became active.
				_storageDirty = true;
			}
        }

        public override void OnCleanUp()
        {
            if (_storage != null)
                _storage.OnStorageChange -= OnLocalStorageChanged;

            base.OnCleanUp();
        }

        private void OnLocalStorageChanged(GameObject _)
        {
            _storageDirty = true;
        }

        private void Update()
        {
            if (isClient)
                return;

            if (!isServer || !inSession || _storage == null)
                return;

            if (!_storageDirty)
                return;

            _syncTimer += Time.unscaledDeltaTime;
            if (_syncTimer < (AnyPlayerWatching() ? STORAGE_SYNC_DELAY : STORAGE_SYNC_DELAY_UNWATCHED))
                return;

            _syncTimer = 0f;
            _storageDirty = false;
            _storageBlob = BuildingUtils.EncodeStorageToBytes(_storage);
        }

        private bool AnyPlayerWatching()
        {
            int worldId = this.GetMyWorldId();
            if (worldId < 0) return false;
            int group = WorldChunkHelper.GetGroupId(worldId, Grid.PosToCell(transform.position));
            foreach (var _ in InterestGroupManager.GetGroupMemberIds(group))
                return true;
            return false;
        }

        private void OnStorageChanged(byte[] oldValue, byte[] newValue)
        {
            if (_storage == null || newValue == null)
                return;

            BuildingUtils.RebuildStorageFromBytes(_storage, newValue);
        }
    }
}
