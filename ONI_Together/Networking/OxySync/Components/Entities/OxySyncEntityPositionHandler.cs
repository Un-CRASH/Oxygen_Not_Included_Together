using System.Collections.Generic;
using KSerialization;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using Shared.OxySync;
using Shared.OxySync.Attributes;
using UnityEngine;

namespace ONI_Together.Networking.OxySync.Components
{
    [SkipSaveFileSerialization]
    public class OxySyncEntityPositionHandler : NetworkTransform
    {
        [MyCmpGet]
        private KBatchedAnimController kbac;
        [MyCmpGet]
        private Navigator navigator;

        [SyncVar(SendMode = (int)PacketSendMode.UnreliableImmediate)]
        private bool _netFlipX;
        [SyncVar(SendMode = (int)PacketSendMode.UnreliableImmediate)]
        private bool _netFlipY;

        [SyncVar(Hook = nameof(OnNavTypeChanged), SendMode = (int)PacketSendMode.UnreliableImmediate)]
        private NavType _netNavType;

        private const int VIEWPORT_MARGIN = 2;
        private const float STALE_THRESHOLD = 2f;
        private const float HEARTBEAT_INTERVAL = 1f;
        private float _lastSyncReceivedTime;
        private float _lastHeartbeatTime;
        private Vector3 _lastPosition;

        // Client-side tally, written to the log every REPORT_INTERVAL seconds.
        // Position sync could fail with nothing in the log at all; this makes the
        // next log say whether updates are arriving, for how many entities, and
        // how far the clock of the host is from ours.
        private const float REPORT_INTERVAL = 30f;
        private static readonly HashSet<OxySyncEntityPositionHandler> _live = new HashSet<OxySyncEntityPositionHandler>();
        private static int _snapshotsApplied;
        private static int _fullStatesApplied;
        private static float _nextReportTime;

        public override void OnSpawn()
        {
            base.OnSpawn();
            syncRotation = false;
            syncScale = false;
            useSnapshotInterpolation = true;
            _lastHeartbeatTime = Time.unscaledTime;
            _lastPosition = transform.position;
            _live.Add(this);
        }

        public override void OnForcedCleanUp()
        {
            _live.Remove(this);
            base.OnForcedCleanUp();
        }

        [Server]
        protected override void ServerUpdate()
        {
            base.ServerUpdate();

            if (kbac != null)
            {
                _netFlipX = kbac.FlipX;
                _netFlipY = kbac.FlipY;
            }

            if (navigator != null && navigator.CurrentNavType != NavType.NumNavTypes)
                _netNavType = navigator.CurrentNavType;

            Vector3 currentPos = transform.position;
            if (Vector3.Distance(currentPos, _lastPosition) >= 0.01f)
            {
                _lastHeartbeatTime = Time.unscaledTime;
                _lastPosition = currentPos;
            }
            else if (Time.unscaledTime - _lastHeartbeatTime >= HEARTBEAT_INTERVAL)
            {
                //MarkAllDirty();
                MarkSyncVarAsDirty(NetPositionHash);
                _lastHeartbeatTime = Time.unscaledTime;
            }
        }

        public override void ApplySyncVar(int fieldHash, object value, long timestamp)
        {
            base.ApplySyncVar(fieldHash, value, timestamp);
            _lastSyncReceivedTime = Time.unscaledTime;
            if (fieldHash == NetPositionHash)
                _snapshotsApplied++;
        }

        [Client]
        protected override void ClientUpdate()
        {
            base.ClientUpdate();

            if (kbac != null)
            {
                kbac.FlipX = _netFlipX;
                kbac.FlipY = _netFlipY;
            }

            if (Time.unscaledTime >= _nextReportTime)
            {
                _nextReportTime = Time.unscaledTime + REPORT_INTERVAL;
                ReportClientStats();
            }
        }

        private static void ReportClientStats()
        {
            int inView = 0;
            int stale = 0;
            bool haveViewport = WorldStateSyncer.TryGetLocalViewport(out var viewport);
            int margin = WorldChunkHelper.ChunkSize * 2;

            foreach (var handler in _live)
            {
                if (handler == null || !haveViewport) continue;
                int cell = Grid.PosToCell(handler.transform.position);
                if (!WorldStateSyncer.IsCellInRect(cell, viewport, margin)) continue;
                inView++;
                if (Time.unscaledTime - handler._lastSyncReceivedTime > STALE_THRESHOLD)
                    stale++;
            }

            DebugConsole.Log($"[PositionSync] {_live.Count} entities tracked, {inView} in view, {stale} of those silent for over {STALE_THRESHOLD:0}s; last {REPORT_INTERVAL:0}s: {_snapshotsApplied} position updates, {_fullStatesApplied} full-state replies; host clock offset {HostClockOffsetMs:0} ms");
            _snapshotsApplied = 0;
            _fullStatesApplied = 0;
        }

        protected override bool ShouldRequestPosition()
        {
            if (!WorldStateSyncer.TryGetLocalViewport(out var viewport))
                return false;

            int cell = Grid.PosToCell(transform.position);
            int margin = WorldChunkHelper.ChunkSize * 2;
            if (!WorldStateSyncer.IsCellInRect(cell, viewport, margin)) return false;
            return Time.unscaledTime - _lastSyncReceivedTime > STALE_THRESHOLD;
        }

        protected override void OnServerPositionRequest(ulong requesterId)
        {
            bool flipX = kbac != null && kbac.FlipX;
            bool flipY = kbac != null && kbac.FlipY;
            NavType navType = navigator != null && navigator.CurrentNavType != NavType.NumNavTypes ? navigator.CurrentNavType : NavType.Floor;
            CallTargetRpc(requesterId, nameof(TargetRpcReceiveFullState), transform.position, flipX, flipY, navType);
        }

        [TargetRpc]
        private void TargetRpcReceiveFullState(Vector3 position, bool flipX, bool flipY, NavType navType)
        {
            // Clients can not set sync vars, just teleport the dupe
            transform.SetPosition(position);
            if (kbac != null)
            {
                kbac.FlipX = flipX;
                kbac.FlipY = flipY;
            }

            if (navigator != null)
                navigator.SetCurrentNavType(navType);

            _lastSyncReceivedTime = Time.unscaledTime;
            _lastRequestTime = Time.unscaledTime;
            _fullStatesApplied++;
        }

        private void OnNavTypeChanged(NavType old, NavType current)
        {
            if (navigator != null)
                navigator.SetCurrentNavType(current);
        }
    }
}
