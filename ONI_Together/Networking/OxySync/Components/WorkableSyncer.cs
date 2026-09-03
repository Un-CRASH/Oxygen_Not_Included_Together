using System.Collections.Generic;
using System;
using System.Linq;
using ONI_Together.DebugTools;
using Shared.OxySync;
using Shared.OxySync.Attributes;
using UnityEngine;

namespace ONI_Together.Networking.OxySync.Components
{
    [SkipSaveFileSerialization]
    [FixedInterestGroup]
    public class WorkableSyncer : NetworkBehaviour
    {

        private string GetWorkableTypeId(Workable workable)
        {
            return workable?.GetType().AssemblyQualifiedName ?? string.Empty;
        }

        private string GetWorkableTypeId(string workableTypeId)
        {
            return workableTypeId ?? string.Empty;
        }

        private Type ResolveWorkableType(string workableTypeId)
        {
            string normalizedTypeId = GetWorkableTypeId(workableTypeId);
            if (string.IsNullOrEmpty(normalizedTypeId))
            {
                return null;
            }

            var type = Type.GetType(normalizedTypeId);
            if (type != null)
            {
                return type;
            }

            string fullName = normalizedTypeId.Split(',')[0].Trim();
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(asm => asm.GetType(fullName, throwOnError: false, ignoreCase: false))
                .FirstOrDefault(t => t != null);
        }

        private (int WorkableNetId, string WorkableTypeId, MethodType Method) BuildAuthKey(int workableNetId, string workableTypeId, MethodType method)
        {
            return (workableNetId, GetWorkableTypeId(workableTypeId), method);
        }

        private (int WorkableNetId, string WorkableTypeId, MethodType Method) BuildAuthKey(Workable workable, MethodType method)
        {
            return (workable.GetNetId(), GetWorkableTypeId(workable), method);
        }

        public static WorkableSyncer Instance { get; private set; }
        public enum MethodType: byte
        {
            StartWork,
            StopWork,
            CompleteWork,
            AbortWork
        }

        private Dictionary<(int WorkableNetId, string WorkableTypeId, MethodType Method), int> workableAuthorization = [];

        public override void OnSpawn()
        {
            base.OnSpawn();
            Instance = this;
            InterestGroup = -1;
        }

        public override void OnCleanUp()
        {
            if (Instance == this)
                Instance = null;
            base.OnCleanUp();
        }

        public static void RegisterNetId(GameObject parent = null) {
            var root = parent == null ? Game.Instance.gameObject : parent;
            if (Instance == null)
            {
                DebugConsole.LogWarning("[WorkableSyncer] Initializing WorkableSyncer instance.");
                var syncerRoot = new GameObject("WorkableSyncer");
                syncerRoot.transform.SetParent(root.transform);
                Instance = syncerRoot.AddComponent<WorkableSyncer>();
            }

            Instance.workableAuthorization.Clear();
            Instance.NetId = nameof(WorkableSyncer).GetHashCode();
        }

        public static bool IsAuthorized(int workableNetId, string workableTypeId, MethodType method)
        {
            if (Instance == null)
            {
                return false;
            }

            return Instance.workableAuthorization.ContainsKey(Instance.BuildAuthKey(workableNetId, workableTypeId, method));
        }

        public static bool IsAuthorized(int workableNetId, string workableTypeId, MethodType method, out int workerNetId)
        {
            var syncer = Instance;
            if (syncer != null && syncer.workableAuthorization.TryGetValue(syncer.BuildAuthKey(workableNetId, workableTypeId, method), out var workerId))
            {
                workerNetId = workerId;
                return true;
            }

            workerNetId = 0;
            return false;
        }

        public static bool IsAuthorized(Workable workable, MethodType method)
        {
            var syncer = Instance;
            if (syncer == null || workable == null || workable.IsNullOrDestroyed())
            {
                return false;
            }

            return syncer.workableAuthorization.ContainsKey(syncer.BuildAuthKey(workable, method));
        }

        public static bool IsAuthorized(Workable workable, MethodType method, out int workerNetId)
        {
            var syncer = Instance;
            if (syncer != null && workable != null && !workable.IsNullOrDestroyed() &&
                syncer.workableAuthorization.TryGetValue(syncer.BuildAuthKey(workable, method), out var workerId))
            {
                workerNetId = workerId;
                return true;
            }

            workerNetId = 0;
            return false;
        }

        public static void UnAuthorize(int workableNetId, string workableTypeId, MethodType method)
        {
            Instance?.workableAuthorization.Remove(Instance.BuildAuthKey(workableNetId, workableTypeId, method));
        }

        public static void UnAuthorize(Workable workable, MethodType method)
        {
            if (workable == null || workable.IsNullOrDestroyed())
            {
                return;
            }

            Instance?.workableAuthorization.Remove(Instance.BuildAuthKey(workable, method));
        }

        public void RequestUpdateWorkable(MethodType method, Workable workable, WorkerBase worker)
        {
            if (!MultiplayerSession.IsHostInSession || !MultiplayerSession.SessionHasPlayers)
            {
                return;
            }

            if (workable.IsNullOrDestroyed() || worker.IsNullOrDestroyed())
            {
                DebugConsole.LogWarning($"[WorkableSyncer] Skip sync for method {method}: WorkableNullOrDestroyed={workable.IsNullOrDestroyed()}, WorkerNullOrDestroyed={worker.IsNullOrDestroyed()}");
                return;
            }

            int workableNetId = workable.GetNetId();
            int workerNetId = worker.GetNetId();
            if (workableNetId == 0 || workerNetId == 0)
            {
                DebugConsole.LogWarning(
                    $"[WorkableSyncer] Skip sync for method {method}: invalid NetId(s). " +
                    $"Workable '{workable.GetProperName()}' has NetId '{workableNetId}', " +
                    $"Worker '{worker.GetProperName()}' has NetId '{workerNetId}'");

                return;
            }

            try
            {
                string workableTypeId = GetWorkableTypeId(workable);
                DebugConsole.Log($"[WorkableSyncer] Worker has NetId {workerNetId} '{method}' on workable {workableNetId} : {workableTypeId}");
                CallClientRpc(nameof(RpcUpdateWorkable), method, workableNetId, workableTypeId, workerNetId);
            }
            catch (System.Exception ex)
            {
                DebugConsole.LogWarning($"[WorkableSyncer] Failed to request update for method {method}. WorkableNetId={workableNetId}, WorkerNetId={workerNetId}. Error: {ex}");
            }
        }

        /// <summary>
        /// Workables the client must not run vanilla work callbacks for, even when the
        /// host has authorized the worker.
        ///
        /// A client does not own duplicant effects: EffectsPatch blanks Effects.Add and
        /// Effects.Remove on clients so that every effect arrives from the host instead.
        /// Blanked, Effects.Add returns null. Clinic's state machine is written against
        /// vanilla, where it never does - the Exit of its doctored state calls
        /// StartEffect(doctoredPlaceholderEffect) and reads .effect off the result with
        /// no null check (Assembly-CSharp, ClinicSM.<InitializeStates>b__4_22, IL 0x70).
        /// Letting the client StartWork on a Clinic therefore puts that machine into a
        /// state it cannot leave: the Exit throws before it removes the doctored effects,
        /// the host keeps replicating them, the machine re-enters doctored, and the Exit
        /// throws again - about 8,800 times a second in the log this was found in, until
        /// the game died 34 seconds after a duplicant lay down in a Triage Cot.
        ///
        /// Skipping the call restores what the client did for these before #185: nothing.
        /// The lying-down animation is not lost; it arrives through the anim override sync,
        /// which the log shows landing before the StartWork that crashed.
        /// </summary>
        private static readonly HashSet<Type> ClientSkippedWorkables = new HashSet<Type>
        {
            typeof(Clinic),
            // The doctor finishing a visit is what moves ClinicSM into newlyDoctored,
            // whose Enter walks straight into doctored - the state a client cannot
            // run (see ClinicStateSyncer). Clients do not doctor.
            typeof(DoctorChoreWorkable),
        };

        /// <summary>
        /// Workables whose CompleteWork the client must not run, while StartWork and
        /// StopWork stay (they only drive the animation).
        ///
        /// Pickupable.OnCompleteWork casts the StartWorkInfo of the worker to
        /// PickupableStartWorkInfo and reads amount off it. The client started that
        /// work from StandardWorker_WorkingState_Packet with a plain StartWorkInfo, so
        /// the cast yields null and every replicated pickup ends in a
        /// NullReferenceException - one per item picked up, 686 in one host session.
        /// Had it not thrown, the client would have taken the item locally on top of
        /// the PickupItemPacket the host sends for the same take.
        /// </summary>
        private static readonly HashSet<Type> ClientSkippedCompleteWork = new HashSet<Type>
        {
            typeof(Pickupable),
        };

        [ClientRpc]
        private void RpcUpdateWorkable(MethodType method, int workableNetId, string workableTypeId, int workerNetId)
        {
            if (workableNetId == 0 || !NetworkIdentityRegistry.TryGet(workableNetId, out var identity) || identity == null || identity.gameObject.IsNullOrDestroyed())
            {
                return;
            }

            Workable workable = null;
            string normalizedTypeId = GetWorkableTypeId(workableTypeId);
            if (!string.IsNullOrEmpty(normalizedTypeId))
            {
                var workableType = ResolveWorkableType(normalizedTypeId);
                if (workableType == null)
                {
                    DebugConsole.LogWarning($"[WorkableSyncer] Could not resolve workable type '{normalizedTypeId}' for netId {workableNetId}");
                    return;
                }

                workable = identity.gameObject.GetComponent(workableType) as Workable;
                if (workable == null)
                {
                    DebugConsole.LogWarning($"[WorkableSyncer] GameObject for netId {workableNetId} does not have workable type '{normalizedTypeId}'");
                    return;
                }
            }

            workable ??= identity.gameObject.GetComponent<Workable>();
            if (workable == null)
            {
                return;
            }

            if (workerNetId == 0 || !NetworkIdentityRegistry.TryGetComponent<WorkerBase>(workerNetId, out var worker) || worker == null || worker.gameObject.IsNullOrDestroyed())
            {
                return;
            }

            if (ClientSkippedWorkables.Contains(workable.GetType()))
            {
                DebugConsole.Log($"[WorkableSyncer] [Client] Skipping '{method}' on {workable.GetProperName()}: {workable.GetType().Name} owns duplicant effects, which clients do not run");
                return;
            }

            if (method == MethodType.CompleteWork && ClientSkippedCompleteWork.Contains(workable.GetType()))
            {
                DebugConsole.Log($"[WorkableSyncer] [Client] Skipping 'CompleteWork' on {workable.GetProperName()}: {workable.GetType().Name} completion is host-side");
                return;
            }

            workableAuthorization[BuildAuthKey(workableNetId, workableTypeId, method)] = workerNetId;

            DebugConsole.Log($"[WorkableSyncer] [Client] Worker has NetId {workerNetId} '{method}' on workable {workableNetId} : {workableTypeId}");

            switch (method)
            {
                case MethodType.StartWork:
                    workable.StartWork(worker);
                    break;
                case MethodType.StopWork:
                    workable.StopWork(worker, false);
                    break;
                case MethodType.CompleteWork:
                    workable.CompleteWork(worker);
                    break;
                case MethodType.AbortWork:
                    workable.StopWork(worker, true);
                    break;
                default:
                    DebugConsole.LogWarning($"[WorkableSyncer] Unknown method name: {method}");
                    break;
            }
        }
    }
}
