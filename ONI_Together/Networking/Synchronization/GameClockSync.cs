using HarmonyLib;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.World;
using UnityEngine;

namespace ONI_Together.Networking.Synchronization
{
	/// <summary>
	/// Keeps a client's game clock - and its simulation rate - on the host's.
	///
	/// Why the previous mechanism did nothing: GameTimeSyncer, an OxySync behaviour
	/// added to the GameClock object, never registered on either side (no log ever
	/// shows its NetId), and even if it had, GameClock.SetTime is
	/// AddTime(max(0, t - now)) - it can only push a clock forward. A client whose
	/// simulation runs faster than the host's (the host loses frame time to its own
	/// load: 12% behind real time against a client's 2.5% in one session, plus every
	/// pause that reached the client late) drifted a full cycle ahead and nothing
	/// could pull it back.
	///
	/// Host: GameServer.Update calls HostTick, which sends a WorldCyclePacket once a
	/// second of real time - unreliable, paused or not, with the pause state in it.
	/// Client: OnHostTime compares the two clocks. While the host is paused nothing is
	/// corrected: its clock stands still and the gap says nothing about drift (the
	/// host's pauses now reach the clients through GameSpeedSyncer, so the gap stays
	/// small). Otherwise a gap over SnapSeconds is snapped: forward through SetTime,
	/// so the game runs its own new-cycle handling; backward by writing the clock's
	/// fields, for which the game offers no call. A smaller gap is closed by scaling
	/// Time.timeScale up or down by up to MaxDeviation, so the simulation itself, not
	/// just the number on the calendar, keeps the host's pace. The factor is re-applied
	/// whenever SpeedControlScreen sets the speed (pause, play, 1x/2x/3x), dropped when
	/// no host time has arrived for StaleSeconds (ClientTick), and cleared on teardown.
	/// </summary>
	public static class GameClockSync
	{
		public const float SendInterval = 1f;
		public const float SnapSeconds = 20f;    // a gap this large is set, not chased, whatever its shape
		public const float StepSeconds = 6f;     // the gap moved this much between two host ticks: a step (autosave, reload), not drift - snap at once; a slow host moves it 2-4 s a tick
		public const float DeadBandSeconds = 0.5f;
		public const float Gain = 0.05f;        // one second of gap -> five percent of speed
		public const float BiasStep = 0.004f;   // slow integral term: removes the steady gap
		public const float BiasLeakPerTick = 0.999f;   // ~17 min time constant: a one-off stall is forgotten, a real rate offset is kept
		public const float BiasInputClamp = 2f;        // one host stall must not swing the integral on its own
		public const float PhaseStepSeconds = 5f;      // a jump this size is a phase step; the integral is held after one
		public const float IntegratorHoldSeconds = 10f;
		public const float MaxFactorSlewPerTick = 0.05f; // one tick may not slam the client's speed from 1.0 to 0.75
		public const float MaxDeviation = 0.25f;
		public const float StaleSeconds = 10f;  // no host time for this long -> plain speed
		public const float LogInterval = 60f;

		private static float _lastSendAt = -1f;
		private static float _lastHostTimeAt = -1f;
		private static float _lastLogAt = -1f;
		private static float _factor = 1f;
		private static float _bias;
		private static float _holdIntegratorUntil;
		private static float _lastDiff;
		private static bool _hasLastDiff;
		private static int _snapCount;

		/// <summary>Current multiplier on the client's game speed (1 = host and client agree).</summary>
		public static float Factor => _factor;

		// ---------------------------------------------------------------- host

		public static void HostTick()
		{
			if (!MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
			var clock = GameClock.Instance;
			if (clock == null) return;
			if (_lastSendAt >= 0f && Time.unscaledTime - _lastSendAt < SendInterval) return;
			_lastSendAt = Time.unscaledTime;

			var screen = SpeedControlScreen.Instance;
			PacketSender.SendToAllClients(new WorldCyclePacket
			{
				Cycle = clock.GetCycle(),
				CycleTime = clock.GetTimeSinceStartOfCycle(),
				HostPaused = screen != null && screen.IsPaused
			}, PacketSendMode.Unreliable | PacketSendMode.Priority);
		}

		// -------------------------------------------------------------- client

		public static void OnHostTime(int cycle, float cycleTime, bool hostPaused)
		{
			if (MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
			if (GameClient.IsHardSyncInProgress) return;
			var clock = GameClock.Instance;
			if (clock == null) return;

			_lastHostTimeAt = Time.unscaledTime;

			if (hostPaused)
			{
				// A standing host clock is not drift. Run plainly and wait for it to move again.
				_bias = 0f;
				_factor = 1f;
				ApplySpeedFactor();
				return;
			}

			float hostTime = cycle * 600f + cycleTime;
			float diff = hostTime - clock.GetTime(); // > 0: this client is behind the host

			// A step - the gap jumped between two consecutive host ticks - is a host
			// stall or a reload and is set straight away; a ramp of the same size is a
			// host that runs slowly (an overloaded PC) and is followed by the controller
			// rather than snapped every few seconds.
			bool step = _hasLastDiff && Mathf.Abs(diff - _lastDiff) > StepSeconds && Mathf.Abs(diff) > PhaseStepSeconds;
			_lastDiff = diff;
			_hasLastDiff = true;

			if (Mathf.Abs(diff) > SnapSeconds || step)
			{
				Snap(clock, cycle, cycleTime, diff);
				_lastDiff = 0f;
				_bias = 0f;
				_factor = 1f;
				_holdIntegratorUntil = Time.unscaledTime + IntegratorHoldSeconds;
			}
			else
			{
				// A PI loop. The proportional term follows the gap; the integral cancels a
				// real rate difference (a loaded host runs 6-8 % slower than a light
				// client - the steady 0.92-0.96 factor in the logs is that correction,
				// not a defect). The integral leaks a little every tick, so a one-off
				// stall is forgotten while a steady offset is kept; a fixed unwind inside
				// the dead band erased the offset in half a minute and left a persistent
				// gap. A phase step (a host autosave, our own reload) is not a rate
				// error, so the integral is held for a while after one, and the factor
				// may only slew so fast.
				if (Mathf.Abs(diff) > PhaseStepSeconds)
					_holdIntegratorUntil = Time.unscaledTime + IntegratorHoldSeconds;
				bool holdIntegrator = Time.unscaledTime < _holdIntegratorUntil;
				if (!holdIntegrator && Mathf.Abs(diff) > DeadBandSeconds)
					_bias = Mathf.Clamp(_bias + Mathf.Clamp(diff, -BiasInputClamp, BiasInputClamp) * BiasStep, -MaxDeviation, MaxDeviation);
				_bias *= BiasLeakPerTick;
				float target = Mathf.Clamp(1f + _bias + diff * Gain, 1f - MaxDeviation, 1f + MaxDeviation);
				_factor = Mathf.MoveTowards(_factor, target, MaxFactorSlewPerTick);
			}

			ApplySpeedFactor();

			if (_lastLogAt < 0f || Time.unscaledTime - _lastLogAt >= LogInterval)
			{
				_lastLogAt = Time.unscaledTime;
				DebugConsole.Log($"[GameClockSync] Gap to host {diff:+0.00;-0.00} s, speed factor {_factor:F3} (bias {_bias:+0.000;-0.000}), snaps {_snapCount}");
			}
		}

		/// <summary>
		/// Client, once a frame from NetworkingComponent: drop the correction when the
		/// host's clock has stopped arriving, so a vanished host does not leave the
		/// client running at a scaled speed until the next speed change.
		/// </summary>
		public static void ClientTick()
		{
			if (_factor == 1f && _bias == 0f) return;
			if (MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
			if (_lastHostTimeAt >= 0f && Time.unscaledTime - _lastHostTimeAt <= StaleSeconds) return;

			_factor = 1f;
			_bias = 0f;
			ApplySpeedFactor();
		}

		private static void Snap(GameClock clock, int cycle, float cycleTime, float diff)
		{
			_snapCount++;
			if (diff > 0f)
			{
				// Behind: AddTime crosses the cycle boundaries the way the host did (autosave, new-day events).
				clock.SetTime(cycle * 600f + cycleTime);
			}
			else
			{
				// Ahead: no game call moves the clock back, so set the fields directly.
				clock.cycle = cycle;
				clock.timeSinceStartOfCycle = cycleTime;
			}

			DebugConsole.Log($"[GameClockSync] Clock snapped {(diff > 0f ? "forward" : "back")} by {Mathf.Abs(diff):F1} s to cycle index {cycle}, {cycleTime:F1} s into it (snap #{_snapCount})");
		}

		/// <summary>
		/// Sets Time.timeScale to the game's speed for the current speed button times the
		/// correction factor. Called after every host time, from ClientTick, and from the
		/// OnChanged patch below, because SpeedControlScreen overwrites timeScale on every
		/// speed change. A paused screen is left at zero.
		/// </summary>
		public static void ApplySpeedFactor()
		{
			if (MultiplayerSession.IsHost || !MultiplayerSession.InActiveSession) return;
			WriteTimeScale();
		}

		private static void WriteTimeScale()
		{
			var screen = SpeedControlScreen.Instance;
			if (screen == null || screen.IsPaused) return;

			float baseScale = screen.speed == 0 ? screen.normalSpeed : (screen.speed == 1 ? screen.fastSpeed : screen.ultraSpeed);
			Time.timeScale = baseScale * _factor;
		}

		/// <summary>
		/// Forget the host's clock: session ended, or a world is about to be unloaded. The
		/// plain speed is written back at once so no scaled speed survives the session.
		/// </summary>
		public static void Reset()
		{
			bool wasScaled = _factor != 1f;
			_factor = 1f;
			_bias = 0f;
			_holdIntegratorUntil = 0f;
			_hasLastDiff = false;
			_lastSendAt = -1f;
			_lastHostTimeAt = -1f;
			_lastLogAt = -1f;
			if (wasScaled)
				WriteTimeScale();
		}
	}

	/// <summary>
	/// SpeedControlScreen.OnChanged is where the game writes Time.timeScale on a
	/// speed change (pause, unpause, 1x/2x/3x); put the correction factor back on top.
	/// </summary>
	[HarmonyPatch(typeof(SpeedControlScreen), "OnChanged")]
	public static class SpeedControlScreen_OnChanged_ClockSyncPatch
	{
		public static void Postfix()
		{
			GameClockSync.ApplySpeedFactor();
		}
	}
}
