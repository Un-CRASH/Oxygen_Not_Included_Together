using KSerialization;
using Shared.OxySync;
using Shared.OxySync.Attributes;
using UnityEngine;

namespace ONI_Together.Networking.OxySync.StateMachines
{
    [SkipSaveFileSerialization]
    public class ClinicStateSyncer : StateMachineSyncer
    {
        private Clinic _clinic;
        private Clinic.ClinicSM.Instance _smi;

        protected override StateMachine.Instance GetStateMachineInstance() => _smi;

        public override void OnSpawn()
        {
            base.OnSpawn();
            _clinic = GetComponent<Clinic>();
            if (_clinic == null)
            {
                Debug.LogWarning("[ClinicStateSyncer] No Clinic component found");
                return;
            }
            _smi = _clinic.GetSMI<Clinic.ClinicSM.Instance>();
        }

        protected override int SampleCurrentStateId()
        {
            if (_smi == null || _smi.sm == null)
                return -1;

            var sm = _smi.sm;
            if (_smi.IsInsideState(sm.operational.healing.newlyDoctored)) return 4;
            if (_smi.IsInsideState(sm.operational.healing.doctored)) return 3;
            if (_smi.IsInsideState(sm.operational.healing.undoctored)) return 2;
            if (_smi.IsInsideState(sm.operational.idle)) return 1;
            if (_smi.IsInsideState(sm.operational.healing)) return 2;
            if (_smi.IsInsideState(sm.unoperational)) return 0;
            return 0;
        }

        protected override void ApplyState(int stateId)
        {
            if (_smi == null || _smi.sm == null)
                return;

            var sm = _smi.sm;
            switch (stateId)
            {
                // healing.* is about the patient. Every callback in doctored and
                // newlyDoctored reads master.worker and the effect that StartEffect adds
                // to it; a client never has a worker on the cot (WorkableSyncer skips
                // Clinic) and never adds effects (EffectsPatch), so worker is null and
                // StartEffect returns null. The Enter of doctored then throws at
                // worker.GetComponent<Effects>() (ClinicSM.<InitializeStates>b__4_20,
                // IL 0x0b) inside the state machine, which the game answers with its
                // crash screen; the Exit did the same at IL 0x70 before StartWork was
                // skipped. The heal itself is host-side. Here the cot only needs to be
                // operational, so every healing state maps to idle.
                case 4:
                case 3:
                case 2:
                case 1:
                    if (!_smi.IsInsideState(sm.operational.idle))
                        _smi.TryGoTo(sm.operational.idle);
                    break;
                default:
                    if (!_smi.IsInsideState(sm.unoperational))
                        _smi.TryGoTo(sm.unoperational);
                    break;
            }
        }
    }
}
