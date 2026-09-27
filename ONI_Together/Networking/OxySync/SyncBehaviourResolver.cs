using ONI_Together.Networking.Components;
using ONI_Together.Networking.OxySync.Components;

namespace ONI_Together.Networking.OxySync
{
    /// <summary>
    /// Resolves a behaviour for an inbound OxySync packet: the manager registry first
    /// (native + API-bridged), then the entity's own NetworkBehaviours matched by
    /// behaviour id, then the API bridge. See <see cref="OxySyncManager.ResolveBehaviour"/>.
    ///
    /// The native fallback must match by id. Taking the first NetworkBehaviour on the object
    /// hands a duplicant's packets to AnimSyncer (no sync vars), whatever they were for.
    /// </summary>
    internal static class SyncBehaviourResolver
    {
        public static bool TryResolve(int netId, int behaviourId, out ISyncBehaviour behaviour)
        {
            behaviour = OxySyncManager.ResolveBehaviour(netId, behaviourId);
            return behaviour != null;
        }
    }
}
