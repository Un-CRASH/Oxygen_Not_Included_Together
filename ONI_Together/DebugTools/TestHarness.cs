using HarmonyLib;
using Newtonsoft.Json;
using ONI_Together.Misc;
using ONI_Together.Misc.World;
using ONI_Together.Networking;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Tools.Build;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ONI_Together.DebugTools
{
	/// <summary>
	/// Scripted test rig, inactive unless the process was started with the environment
	/// variable ONI_TOGETHER_TEST_DIR. It exists so that a host and a client can run on
	/// one machine, be driven from a script and compared without anyone clicking:
	///  - ONI_TOGETHER_KLEI_ROOT: replaces Documents\Klei for save files, prefs and mods,
	///    so a second game instance never touches the real profile.
	///  - ONI_TOGETHER_AUTO: "host:<save path>[:<ip>:<port>]" loads that save and starts a
	///    LAN server from the main menu; "join:<ip>:<port>" connects as a client.
	///  - <TEST_DIR>\commands.txt is polled twice a second; every new line is a command
	///    (see Execute) and its result is appended to <TEST_DIR>\results.txt. Dumps of a
	///    world region go to <TEST_DIR>\dump_<name>.json so the two sides can be diffed.
	/// Nothing here runs for ordinary players.
	/// </summary>
	internal static class TestHarness
	{
		internal static readonly string Dir = Environment.GetEnvironmentVariable("ONI_TOGETHER_TEST_DIR");
		internal static readonly string KleiRoot = Environment.GetEnvironmentVariable("ONI_TOGETHER_KLEI_ROOT");
		internal static readonly string Auto = Environment.GetEnvironmentVariable("ONI_TOGETHER_AUTO");
		internal static bool Enabled => !string.IsNullOrEmpty(Dir);

		/// <summary>ONI_TOGETHER_TEST_LOSS: share (0..1) of outgoing unreliable datagrams to drop; simulates a lossy link.</summary>
		internal static readonly float DropUnreliableChance = ParseLoss(Environment.GetEnvironmentVariable("ONI_TOGETHER_TEST_LOSS"));
		private static int _droppedUnreliable;
		internal static void CountDroppedUnreliable() => _droppedUnreliable++;

		private static float ParseLoss(string text)
		{
			return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? Mathf.Clamp01(value) : 0f;
		}

		private static long _consumed;
		private static float _nextPoll;
		private static bool _autoDone;

		/// <summary>
		/// The profile redirect starts at the main menu, not at mod load: KMod.Manager
		/// clears mods.json's mod_load_in_progress flag right after the mods loaded, and
		/// with the redirect already active that write landed in the test profile while
		/// the real file kept "true", so the next instance booted in mod safe mode.
		/// </summary>
		internal static bool RedirectActive;
		private static string CommandsPath => Path.Combine(Dir, "commands.txt");
		private static string ResultsPath => Path.Combine(Dir, "results.txt");

		internal static void Init(GameObject modules)
		{
			if (!Enabled) return;
			try
			{
				Directory.CreateDirectory(Dir);
				modules.AddComponent<TestHarnessBehaviour>();
				Result($"harness ready; kleiRoot={KleiRoot}; auto={Auto}");
			}
			catch (Exception ex)
			{
				DebugConsole.LogError($"[TestHarness] Init failed: {ex}");
			}
		}

		internal static void OnMainMenu()
		{
			if (!Enabled) return;
			RedirectActive = !string.IsNullOrEmpty(KleiRoot);
			if (_autoDone || string.IsNullOrEmpty(Auto)) return;
			_autoDone = true;
			try
			{
				// One frame later: MainMenu.OnSpawn is still on the stack.
				CoroutineRunner.RunOne(AutoFlow());
			}
			catch (Exception ex)
			{
				Result($"auto failed: {ex}");
			}
		}

		private static System.Collections.IEnumerator AutoFlow()
		{
			yield return new WaitForSecondsRealtime(2f);
			// host|<save path>|<ip>|<port>   or   join|<ip>|<port>
			var parts = Auto.Split('|');
			if (parts[0] == "host" && parts.Length >= 2)
			{
				string save = parts[1];
				string ip = parts.Length >= 3 ? parts[2] : "127.0.0.1";
				int port = parts.Length >= 4 ? int.Parse(parts[3]) : 8090;
				Configuration.Instance.Host.LanSettings.Ip = ip;
				Configuration.Instance.Host.LanSettings.Port = port;
				Configuration.Instance.Host.LanSettings.Transport = LanTransportType.LiteNetLib;
				NetworkConfig.UpdateLanTransport();
				MultiplayerSession.ShouldHostAfterLoad = true;
				KCrashReporter.MOST_RECENT_SAVEFILE = save;
				SaveLoader.SetActiveSaveFilePath(save);
				Result($"auto host: loading {save}, will listen on {ip}:{port}");
				App.LoadScene("backend");
			}
			else if (parts[0] == "join" && parts.Length >= 3)
			{
				string ip = parts[1];
				int port = int.Parse(parts[2]);
				Configuration.Instance.Host.LanSettings.Transport = LanTransportType.LiteNetLib;
				NetworkConfig.UpdateLanTransport();
				Configuration.Instance.Client.LanSettings.Ip = ip;
				Configuration.Instance.Client.LanSettings.Port = port;
				Result($"auto join: {ip}:{port}");
				GameClient.ConnectToHost(ip: ip, port: port);
			}
			else
			{
				Result($"auto: unrecognised '{Auto}'");
			}
		}

		internal static void Tick()
		{
			if (Time.realtimeSinceStartup < _nextPoll) return;
			_nextPoll = Time.realtimeSinceStartup + 0.5f;
			try
			{
				if (!File.Exists(CommandsPath)) return;
				string[] lines;
				using (var fs = new FileStream(CommandsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					if (fs.Length <= _consumed) return;
					fs.Seek(_consumed, SeekOrigin.Begin);
					using var reader = new StreamReader(fs, Encoding.UTF8);
					string text = reader.ReadToEnd();
					_consumed = fs.Length;
					lines = text.Split('\n');
				}
				foreach (var raw in lines)
				{
					string line = raw.Trim();
					if (line.Length == 0 || line.StartsWith("#")) continue;
					string result;
					try { result = Execute(line); }
					catch (Exception ex) { result = "EXCEPTION " + ex; }
					Result($"{line} => {result}");
				}
			}
			catch (Exception ex)
			{
				DebugConsole.LogError($"[TestHarness] Tick failed: {ex}");
			}
		}

		private static void Result(string text)
		{
			string line = $"[{System.DateTime.UtcNow:HH:mm:ss.fff}] {text}";
			DebugConsole.Log("[TestHarness] " + text);
			try { File.AppendAllText(ResultsPath, line + "\n"); } catch { }
		}

		/// <summary>
		/// Commands (cells are Grid cell indices; "x,y" is accepted too):
		///  dig CELL | cancel CELL | deconstruct CELL | sweep CELL | sweepitem CELL | unsweepitem CELL
		///  build PREFAB CELL ELEMENT [ORIENTATION 0-3] | prio CELL VALUE | speed 0-3 | pause | unpause
		///  camera CELL | dump NAME X0 Y0 X1 Y1 | dupes NAME | hardsync | fps N
		///  log TEXT | status | xy CELL | dumpc NAME CELL RADIUS | items CELL | quit
		/// </summary>
		private static string Execute(string line)
		{
			var a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			string cmd = a[0].ToLowerInvariant();
			switch (cmd)
			{
				case "status":
					return $"host={MultiplayerSession.IsHost} loss={DropUnreliableChance} dropped={_droppedUnreliable} inSession={MultiplayerSession.InActiveSession} client={GameClient.State} players={MultiplayerSession.PlayerCount} inGame={Utils.IsInGame()} speed={SpeedControlScreen.Instance?.GetSpeed()} paused={SpeedControlScreen.Instance?.IsPaused} cycle={GameClock.Instance?.GetCycle()} time={GameClock.Instance?.GetTime():F1} ids={NetworkIdentityRegistry.Count} grid={Grid.WidthInCells}x{Grid.HeightInCells}";
				case "xy":
				{
					int cell = ParseCell(a[1]);
					Grid.CellToXY(cell, out int x, out int y);
					return $"cell {cell} = {x},{y}";
				}
				case "find":
				{
					string want = a[1];
					int max = a.Length > 2 ? int.Parse(a[2]) : 300;
					var list = new List<object>();
					int total = 0, inStorage = 0, unassigned = 0;
					foreach (var identity in NetworkIdentityRegistry.AllIdentities)
					{
						if (identity == null) continue;
						GameObject go;
						string prefab;
						try { go = identity.gameObject; prefab = go.PrefabID().ToString(); }
						catch { continue; }
						if (!string.Equals(prefab, want, StringComparison.OrdinalIgnoreCase)) continue;
						total++;
						if (!identity.HostAssigned) unassigned++;
						var pickupable = go.GetComponent<Pickupable>();
						string storage = null;
						if (pickupable != null && pickupable.storage != null)
						{
							inStorage++;
							storage = pickupable.storage.gameObject.PrefabID().ToString() + "@" + Grid.PosToCell(pickupable.storage.gameObject);
						}
						if (list.Count >= max) continue;
						var obj = new Dictionary<string, object> { ["netId"] = identity.NetId, ["cell"] = Grid.PosToCell(go), ["hostAssigned"] = identity.HostAssigned, ["active"] = go.activeInHierarchy };
						if (storage != null) obj["storage"] = storage;
						var primary = go.GetComponent<PrimaryElement>();
						if (primary != null) obj["mass"] = Math.Round(primary.Mass, 2);
						list.Add(obj);
					}
					return WriteJson("find_" + want, new Dictionary<string, object> { ["host"] = MultiplayerSession.IsHost, ["prefab"] = want, ["total"] = total, ["inStorage"] = inStorage, ["local"] = unassigned, ["objects"] = list });
				}
				case "verbose":
					DebugConsole.Verbosity = a.Length > 1 && a[1] == "on" ? LogVerbosity.Verbose : LogVerbosity.Normal;
					return "verbosity=" + DebugConsole.Verbosity;
				case "localdig":
				{
					int cell = ParseCell(a[1]);
					bool wasSolid = Grid.Solid[cell];
					WorldDamage.Instance.DestroyCell(cell);
					WorldDamage.Instance.OnSolidStateChanged(cell);
					return $"local dig at {cell} (was solid={wasSolid}); no packet sent";
				}
				case "localfill":
				{
					int cell = ParseCell(a[1]);
					var element = ElementLoader.FindElementByName(a[2]);
					if (element == null) return "unknown element " + a[2];
					SimMessages.ModifyCell(cell, element.idx, 293.15f, 400f, byte.MaxValue, 0, SimMessages.ReplaceType.Replace);
					return $"local fill at {cell} with {element.id}; no packet sent";
				}
				case "terrain":
					return ONI_Together.Networking.Synchronization.TerrainReconcile.Status + $"; solid={Grid.Solid[ParseCell(a.Length > 1 ? a[1] : "0")]}";
				case "food":
				{
					var byPrefab = new SortedDictionary<string, double[]>();
					double total = 0; int count = 0, stored = 0;
					foreach (var edible in global::Components.Edibles.Items)
					{
						if (edible == null || edible.IsNullOrDestroyed()) continue;
						string prefab = edible.gameObject.PrefabID().ToString();
						if (!byPrefab.TryGetValue(prefab, out var row)) byPrefab[prefab] = row = new double[2];
						row[0] += 1; row[1] += edible.Calories;
						total += edible.Calories; count++;
						var pickupable = edible.GetComponent<Pickupable>();
						if (pickupable != null && ONI_Together.Patches.World.PickupablePatches.IsInStorage(pickupable)) stored++;
					}
					if (a.Length > 1) WriteJson("food_" + a[1], new Dictionary<string, object> { ["host"] = MultiplayerSession.IsHost, ["kcal"] = Math.Round(total / 1000.0, 1), ["items"] = count, ["stored"] = stored, ["prefabs"] = byPrefab });
					return $"edibles={count} stored={stored} kcal={total / 1000.0:F1}";
				}
				case "anim":
				{
					int cell = ParseCell(a[1]);
					var go = Grid.Objects[cell, (int)ObjectLayer.Building];
					if (go == null) return "no building at " + cell;
					var op = go.GetComponent<Operational>();
					var anim = go.GetComponent<KBatchedAnimController>();
					var receiver = go.GetComponent<ONI_Together.Scripts.Buildings.ClientReceiver_Operational>();
					return $"{go.PrefabID()} operational={(op != null ? op.IsOperational.ToString() : "-")} active={(op != null ? op.IsActive.ToString() : "-")} hostState={(receiver != null ? receiver.HasHostState.ToString() : "-")} anim={(anim != null ? anim.currentAnim.ToString() : "-")} playing={(anim != null ? (!anim.IsStopped()).ToString() : "-")}";
				}
				case "storage":
				{
					int cell = ParseCell(a[1]);
					var go = Grid.Objects[cell, (int)ObjectLayer.Building];
					if (go == null) return "no building at " + cell;
					var st = go.GetComponent<Storage>();
					if (st == null) return go.PrefabID() + " has no Storage";
					var sb = new StringBuilder();
					sb.Append($"{go.PrefabID()} items={st.items.Count} mass={st.MassStored():F2}");
					foreach (var item in st.items)
					{
						if (item == null) { sb.Append(" | <null>"); continue; }
						var pe = item.GetComponent<PrimaryElement>();
						var pick = item.GetComponent<Pickupable>();
						var id = item.GetComponent<ONI_Together.Networking.Components.NetworkIdentity>();
						sb.Append($" | {item.PrefabID()} {(pe != null ? pe.Mass : 0f):F2}kg id={(id != null ? id.NetId : 0)}{(id != null && id.HostAssigned ? "H" : "L")} st={(pick != null && pick.storage == st ? "y" : "n")}");
					}
					return sb.ToString();
				}
				case "census":
				{
					var byPrefab = new SortedDictionary<string, int[]>();
					int total = 0, local = 0;
					foreach (var identity in NetworkIdentityRegistry.AllIdentities)
					{
						if (identity == null) continue;
						string prefab;
						bool pickupable;
						try { prefab = identity.gameObject.PrefabID().ToString(); pickupable = identity.GetComponent<Pickupable>() != null; }
						catch { continue; }
						total++;
						if (!byPrefab.TryGetValue(prefab, out var counts)) byPrefab[prefab] = counts = new int[3];
						counts[0]++;
						if (!identity.HostAssigned) { counts[1]++; local++; }
						if (pickupable) counts[2]++;
					}
					return WriteJson("census_" + a[1], new Dictionary<string, object>
					{
						["host"] = MultiplayerSession.IsHost,
						["cycle"] = GameClock.Instance?.GetCycle(),
						["time"] = GameClock.Instance != null ? Math.Round(GameClock.Instance.GetTime(), 1) : 0,
						["ids"] = total,
						["local"] = local,
						["pending"] = ONI_Together.Networking.Components.PendingRemovals.Count,
						["prefabs"] = byPrefab,
					});
				}
				case "dumpc":
				{
					int cell = ParseCell(a[2]);
					int radius = int.Parse(a[3]);
					Grid.CellToXY(cell, out int x, out int y);
					return WriteJson("dump_" + a[1], DumpRegion(x - radius, y - radius, x + radius, y + radius));
				}
				case "items":
				{
					int cell = ParseCell(a[1]);
					var sb = new StringBuilder();
					foreach (var go in ItemsAt(cell))
					{
						var clearable = go.GetComponent<Clearable>();
						sb.Append(Describe(go)).Append(clearable != null && clearable.isMarkedForClear ? " [sweep]" : "").Append("; ");
					}
					return sb.Length > 0 ? sb.ToString() : "no items";
				}
				case "log":
					return "logged";
				case "quit":
					App.Quit();
					return "quitting";
				case "fps":
					if (a.Length > 1) Application.targetFrameRate = int.Parse(a[1]);
					return $"fps={(Time.smoothDeltaTime > 0f ? 1f / Time.smoothDeltaTime : 0f):F1} unscaledDelta={Time.unscaledDeltaTime:F3} timeScale={Time.timeScale:F2} target={Application.targetFrameRate}";
				case "speed":
					SpeedControlScreen.Instance.SetSpeed(int.Parse(a[1]));
					return $"speed={SpeedControlScreen.Instance.GetSpeed()}";
				case "pause":
					SpeedControlScreen.Instance.Pause(false);
					return "paused";
				case "unpause":
					SpeedControlScreen.Instance.Unpause(false);
					return "unpaused";
				case "camera":
				{
					int cell = ParseCell(a[1]);
					float size = a.Length > 2 ? float.Parse(a[2]) : 12f;
					CameraController.Instance.SetTargetPos(Grid.CellToPosCCC(cell, Grid.SceneLayer.Move), size, false);
					return $"camera -> {cell}";
				}
				case "dig":
				{
					int cell = ParseCell(a[1]);
					var go = DigTool.PlaceDig(cell, 0);
					return go != null ? $"diggable {Describe(go)}" : "nothing placed";
				}
				case "cancel":
					CancelTool.Instance.OnDragTool(ParseCell(a[1]), 0);
					return "cancel sent";
				case "deconstruct":
					DeconstructTool.Instance.OnDragTool(ParseCell(a[1]), 0);
					return "deconstruct sent";
				case "sweep":
					ClearTool.Instance.OnDragTool(ParseCell(a[1]), 0);
					return "sweep sent";
				case "sweepitem":
				case "unsweepitem":
				{
					int cell = ParseCell(a[1]);
					var items = ItemsAt(cell);
					if (items.Count == 0) return "no item at cell";
					var clearable = items[0].GetComponent<Clearable>();
					if (clearable == null) return "first item has no Clearable";
					if (cmd == "sweepitem") clearable.MarkForClear(false, false); else clearable.CancelClearing();
					return $"{cmd} {Describe(items[0])}";
				}
				case "prio":
				{
					int cell = ParseCell(a[1]);
					int value = int.Parse(a[2]);
					var go = Grid.Objects[cell, (int)ObjectLayer.Building] ?? Grid.Objects[cell, (int)ObjectLayer.DigPlacer] ?? Grid.Objects[cell, (int)ObjectLayer.FoundationTile];
					var prioritizable = go != null ? go.GetComponent<Prioritizable>() : null;
					if (prioritizable == null) return "nothing prioritizable at cell";
					prioritizable.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic, value));
					return $"priority {value} on {Describe(go)}";
				}
				case "build":
				{
					string prefab = a[1];
					int cell = ParseCell(a[2]);
					var def = Assets.GetBuildingDef(prefab);
					if (def == null) return $"unknown def {prefab}";
					var elements = new List<Tag> { TagManager.Create(a[3]) };
					var orientation = a.Length > 4 ? (Orientation)int.Parse(a[4]) : Orientation.Neutral;
					Vector3 pos = Grid.CellToPosCBC(cell, Grid.SceneLayer.Building);
					GameObject built;
					if (def.ReplacementLayer != ObjectLayer.NumLayers && def.GetReplacementCandidate(cell) != null && !def.IsReplacementLayerOccupied(cell))
					{
						var visualizer = Util.KInstantiate(def.BuildingPreview, pos);
						built = def.TryReplaceTile(visualizer, pos, orientation, elements, "DEFAULT_FACADE");
						if (built != null) Grid.Objects[cell, (int)def.ReplacementLayer] = built;
					}
					else
					{
						var visualizer = Util.KInstantiate(def.BuildingPreview, pos);
						built = def.TryPlace(visualizer, pos, orientation, elements, "DEFAULT_FACADE");
					}
					if (built == null) return "TryPlace returned null (invalid location?)";
					// What BuildToolPatch does after BuildTool.TryBuild placed something.
					DebugConsole.Log($"[BuildTool] Placed {def.PrefabID} at cell {cell}");
					PacketSender.SendToAllOtherPeers(new BuildPacket(def.PrefabID, cell, orientation, elements, def.ObjectLayer, false));
					return $"placed {Describe(built)}";
				}
				case "hardsync":
					GameServerHardSync.PerformHardSync();
					return "hard sync requested";
				case "dupes":
					return WriteJson("dupes_" + a[1], DumpDupes());
				case "dump":
					return WriteJson("dump_" + a[1], DumpRegion(int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), int.Parse(a[5])));
				default:
					return "unknown command";
			}
		}

		private static int ParseCell(string s)
		{
			if (s.Contains(","))
			{
				var xy = s.Split(',');
				return Grid.XYToCell(int.Parse(xy[0]), int.Parse(xy[1]));
			}
			return int.Parse(s);
		}

		private static string Describe(GameObject go)
		{
			if (go == null) return "null";
			var identity = go.GetExistingNetIdentity();
			return $"{go.name} cell={Grid.PosToCell(go)} netId={(identity != null ? identity.NetId : 0)}";
		}

		private static List<GameObject> ItemsAt(int cell)
		{
			var result = new List<GameObject>();
			var head = Grid.Objects[cell, (int)ObjectLayer.Pickupables];
			var item = head != null ? head.GetComponent<Pickupable>()?.objectLayerListItem : null;
			int guard = 0;
			while (item != null && guard++ < 10000)
			{
				if (item.gameObject != null) result.Add(item.gameObject);
				item = item.nextItem;
			}
			return result;
		}

		private static string WriteJson(string name, object data)
		{
			string path = Path.Combine(Dir, name + ".json");
			File.WriteAllText(path, JsonConvert.SerializeObject(data, Formatting.Indented));
			return path;
		}

		private static readonly ObjectLayer[] DumpLayers =
		{
			ObjectLayer.Building, ObjectLayer.FoundationTile, ObjectLayer.ReplacementTile, ObjectLayer.DigPlacer,
			ObjectLayer.Wire, ObjectLayer.LiquidConduit, ObjectLayer.GasConduit, ObjectLayer.LogicWire,
			ObjectLayer.Backwall, ObjectLayer.Plants,
		};

		/// <summary>Per cell: element, mass, the object on each structural layer, the items lying there.</summary>
		private static object DumpRegion(int x0, int y0, int x1, int y1)
		{
			var cells = new List<object>();
			for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
			for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
			{
				int cell = Grid.XYToCell(x, y);
				if (!Grid.IsValidCell(cell)) continue;
				var entry = new Dictionary<string, object>
				{
					["cell"] = cell,
					["el"] = Grid.Element[cell].id.ToString(),
					["mass"] = Math.Round(Grid.Mass[cell], 1),
					["solid"] = Grid.Solid[cell],
				};
				foreach (var layer in DumpLayers)
				{
					var go = Grid.Objects[cell, (int)layer];
					if (go == null) continue;
					var obj = new Dictionary<string, object> { ["prefab"] = go.PrefabID().ToString() };
					var identity = go.GetExistingNetIdentity();
					if (identity != null) obj["netId"] = identity.NetId;
					if (go.GetComponent<Constructable>() != null) obj["state"] = "constructable";
					else if (go.GetComponent<BuildingComplete>() != null) obj["state"] = "complete";
					var prioritizable = go.GetComponent<Prioritizable>();
					if (prioritizable != null) obj["prio"] = prioritizable.GetMasterPriority().priority_value;
					var deconstructable = go.GetComponent<Deconstructable>();
					if (deconstructable != null && deconstructable.IsMarkedForDeconstruction()) obj["deconstruct"] = true;
					var primary = go.GetComponent<PrimaryElement>();
					if (primary != null) { obj["pel"] = primary.ElementID.ToString(); obj["pmass"] = Math.Round(primary.Mass, 1); }
					entry[layer.ToString()] = obj;
				}
				var items = ItemsAt(cell);
				if (items.Count > 0)
				{
					var list = new List<object>();
					foreach (var go in items)
					{
						var obj = new Dictionary<string, object> { ["prefab"] = go.PrefabID().ToString() };
						var identity = go.GetExistingNetIdentity();
						if (identity != null) obj["netId"] = identity.NetId;
						var primary = go.GetComponent<PrimaryElement>();
						if (primary != null) obj["mass"] = Math.Round(primary.Mass, 2);
						var clearable = go.GetComponent<Clearable>();
						if (clearable != null && clearable.isMarkedForClear) obj["sweep"] = true;
						list.Add(obj);
					}
					entry["items"] = list;
				}
				cells.Add(entry);
			}
			return new Dictionary<string, object>
			{
				["host"] = MultiplayerSession.IsHost,
				["cycle"] = GameClock.Instance?.GetCycle(),
				["time"] = GameClock.Instance != null ? Math.Round(GameClock.Instance.GetTime(), 1) : 0,
				["ids"] = NetworkIdentityRegistry.Count,
				["cells"] = cells,
			};
		}

		private static object DumpDupes()
		{
			var list = new List<object>();
			foreach (var identity in global::Components.MinionIdentities.Items)
			{
				if (identity == null) continue;
				var go = identity.gameObject;
				var netIdentity = go.GetExistingNetIdentity();
				var chore = identity.GetComponent<ChoreDriver>()?.GetCurrentChore();
				list.Add(new Dictionary<string, object>
				{
					["name"] = identity.GetProperName(),
					["netId"] = netIdentity != null ? netIdentity.NetId : 0,
					["cell"] = Grid.PosToCell(go),
					["chore"] = chore != null ? chore.GetType().Name : "none",
					["target"] = chore?.target != null ? Grid.PosToCell(chore.target.gameObject) : -1,
				});
			}
			return list;
		}
	}

	internal class TestHarnessBehaviour : MonoBehaviour
	{
		private void Update()
		{
			TestHarness.Tick();
		}
	}

	[HarmonyPatch(typeof(Util), nameof(Util.GetKleiRootPath))]
	internal static class TestHarness_KleiRootPatch
	{
		private static bool Prefix(ref string __result)
		{
			if (!TestHarness.RedirectActive || string.IsNullOrEmpty(TestHarness.KleiRoot)) return true;
			__result = TestHarness.KleiRoot;
			return false;
		}
	}

	[HarmonyPatch(typeof(MainMenu), "OnSpawn")]
	internal static class TestHarness_MainMenuPatch
	{
		private static void Postfix()
		{
			TestHarness.OnMainMenu();
		}
	}
}
