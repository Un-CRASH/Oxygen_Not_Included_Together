using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using ImGuiNET;
using System.Linq;
using Shared.Profiling;

namespace ONI_Together.DebugTools
{
    public class DebugConsole
    {
        private static DebugConsole _instance;
        private static readonly List<LogEntry> logEntries = new List<LogEntry>();
        private static readonly object _lock = new object();

        /// <summary>
        /// How much the mod writes to Player.log. Quiet keeps warnings and errors, Normal
        /// adds the session-level events (connections, sync, builds), Verbose adds the
        /// per-object chatter (every work call, every registered item, every relayed
        /// packet). Set from the mod options; read on every log call, so it stays a
        /// plain static rather than going through the options singleton.
        /// </summary>
        public static LogVerbosity Verbosity = LogVerbosity.Normal;

        public static bool IsVerbose => Verbosity >= LogVerbosity.Verbose;

        private Vector2 scrollPos;
        private bool autoScroll = true;
        private bool collapseDuplicates = false;
        private string filter = "";

        private const int MaxLines = 300;
        private bool showConsole = false;

        private class LogEntry
        {
            public string message;
            public string stack;
            public LogType type;
            public bool expanded;
            public int count = 1;
        }

        public enum LogType
        {
            Error,
            Assert,
            Warning,
            Log,
            Exception,
            Success,
            NonImportant
        }

        /// <summary>
        /// A message that repeats many times per session: how many were counted since the
        /// last summary, the most recent text, and when the current minute started.
        /// </summary>
        private sealed class Aggregate
        {
            public int Count;
            public string Last;
            public long WindowStartMs;
            public long LastSeenMs;
            public bool Warning;
        }

        public const int AggregateWindowMs = 60_000;
        private const int AggregateForgetMs = 5 * 60_000;
        private static readonly Dictionary<string, Aggregate> _aggregates = new Dictionary<string, Aggregate>();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static long _nextAggregateSweepMs;

        public static DebugConsole Init()
        {
            using var _ = Profiler.Scope();

            if (_instance != null)
                return _instance;

            _instance = new DebugConsole();
            return _instance;
        }

        /// <summary>
        /// Session-level event. Skipped in Player.log when the verbosity is Quiet; the
        /// in-game console keeps it either way.
        /// </summary>
        public static void Log(string message)
        {
            using var _ = Profiler.Scope();

            if (Verbosity > LogVerbosity.Quiet)
                Debug.Log($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Log);
        }

        public static void LogWarning(string message)
        {
            using var _ = Profiler.Scope();

            Debug.LogWarning($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Warning);
        }

        public static void LogError(string message, bool trigger_error_screen = false)
        {
            using var _ = Profiler.Scope();

            if (trigger_error_screen)
                Debug.LogError($"[ONI_Together] {message}");
            else //put it in the log file but don't trigger the error screen
                Debug.LogWarning($"-[ERROR] [ONI_Together] {message}");

            EnsureInstance();
            _instance.AddLog(message, "", LogType.Error);
        }

        public static void LogErrorTriggerInGameScreen(string message)
        {
            LogError(message, true);
        }

        public static void LogException(Exception ex)
        {
            using var _ = Profiler.Scope();

            Debug.LogException(ex);
            EnsureInstance();
            _instance.AddLog(ex.Message, ex.StackTrace, LogType.Exception);
        }

        public static void LogAssert(string message)
        {
            using var _ = Profiler.Scope();

            if (Verbosity > LogVerbosity.Quiet)
                Debug.Log($"[ONI_Together/Assert] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Assert);
        }

        public static void LogSuccess(string message)
        {
            using var _ = Profiler.Scope();

            if (Verbosity > LogVerbosity.Quiet)
                Debug.Log($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.Success);
        }

        /// <summary>
        /// Per-object chatter. Written only when the verbosity is Verbose and skipped
        /// entirely otherwise, so guard expensive message building with <see cref="IsVerbose"/>.
        /// </summary>
        public static void LogVerbose(string message)
        {
            if (!IsVerbose)
                return;

            Debug.Log($"[ONI_Together] {message}");
            EnsureInstance();
            _instance.AddLog(message, "", LogType.NonImportant);
        }

        public static void LogNonImportant(string message)
        {
            LogVerbose(message);
        }

        /// <summary>
        /// For messages that can repeat thousands of times per session. The first occurrence
        /// of a key is written at once; later ones are counted and written as one summary line
        /// per minute (see <see cref="FlushAggregates"/>), so the signal survives without the
        /// volume. Safe to call from any thread.
        /// </summary>
        public static void LogAggregated(string key, string message, bool warning = true)
        {
            long now = _clock.ElapsedMilliseconds;
            bool first = false;
            string summary = null;
            bool summaryWarning = false;

            lock (_lock)
            {
                if (!_aggregates.TryGetValue(key, out var aggregate))
                {
                    _aggregates[key] = new Aggregate { Last = message, WindowStartMs = now, LastSeenMs = now, Warning = warning };
                    first = true;
                }
                else
                {
                    aggregate.Count++;
                    aggregate.Last = message;
                    aggregate.LastSeenMs = now;
                    aggregate.Warning |= warning;
                    if (now - aggregate.WindowStartMs >= AggregateWindowMs)
                    {
                        summary = Summarize(aggregate, now);
                        summaryWarning = aggregate.Warning;
                        aggregate.Count = 0;
                        aggregate.WindowStartMs = now;
                    }
                }
            }

            if (first)
                Write($"{message} (repeats of this kind are summarised once a minute)", warning);
            if (summary != null)
                Write(summary, summaryWarning);
        }

        /// <summary>
        /// Writes the summaries whose minute has passed. Called every frame by the networking
        /// component and costs nothing when no summary is due; <paramref name="force"/> writes
        /// every pending count, which the session teardown uses so the last minute is not lost.
        /// </summary>
        public static void FlushAggregates(bool force = false)
        {
            long now = _clock.ElapsedMilliseconds;
            if (!force && now < _nextAggregateSweepMs)
                return;
            _nextAggregateSweepMs = now + 1000;

            List<(string text, bool warning)> due = null;
            lock (_lock)
            {
                if (_aggregates.Count == 0)
                    return;

                List<string> forget = null;
                foreach (var kvp in _aggregates)
                {
                    var aggregate = kvp.Value;
                    if (aggregate.Count > 0 && (force || now - aggregate.WindowStartMs >= AggregateWindowMs))
                    {
                        (due ??= new List<(string, bool)>()).Add((Summarize(aggregate, now), aggregate.Warning));
                        aggregate.Count = 0;
                        aggregate.WindowStartMs = now;
                    }
                    else if (aggregate.Count == 0 && now - aggregate.LastSeenMs >= AggregateForgetMs)
                    {
                        // Quiet for five minutes: the next occurrence is logged at once again.
                        (forget ??= new List<string>()).Add(kvp.Key);
                    }
                }

                if (forget != null)
                    foreach (var key in forget)
                        _aggregates.Remove(key);
            }

            if (due != null)
                foreach (var entry in due)
                    Write(entry.text, entry.warning);
        }

        private static string Summarize(Aggregate aggregate, long now)
        {
            int seconds = (int)Math.Max(1, (now - aggregate.WindowStartMs) / 1000);
            return $"{aggregate.Last} (+{aggregate.Count} similar in the last {seconds} s)";
        }

        private static void Write(string message, bool warning)
        {
            if (warning)
                LogWarning(message);
            else
                Log(message);
        }

        /// <summary>
        /// Mirrors a line the game already wrote to Player.log into the in-game console
        /// without writing it to the file a second time.
        /// </summary>
        public static void AddExternal(string message, string stack)
        {
            EnsureInstance();
            _instance.AddLog(message, stack ?? "", LogType.Error);
        }

        private static void EnsureInstance()
        {
            if (_instance == null)
                _instance = new DebugConsole();
        }

        private void AddLog(string message, string stack, LogType type)
        {
            using var _ = Profiler.Scope();

            lock (_lock)
            {
                if (collapseDuplicates && logEntries.Count > 0)
                {
                    var last = logEntries[logEntries.Count - 1];
                    if (last.message == message && last.type == type)
                    {
                        last.count++;
                        return;
                    }
                }

                logEntries.Add(new LogEntry
                {
                    message = message,
                    stack = stack,
                    type = type,
                    expanded = false
                });

                if (logEntries.Count > MaxLines)
                    logEntries.RemoveAt(0);
            }
        }

        /// <summary>
        /// Toggles visibility of the ImGui console window.
        /// </summary>
        public void Toggle()
        {
            using var _ = Profiler.Scope();

            showConsole = !showConsole;
        }

        /// <summary>
        /// Draws the ImGui window for the debug console.
        /// Call this from your DevTool.RenderTo() or ImGui render loop.
        /// </summary>
        public void ShowWindow()
        {
            using var _ = Profiler.Scope();

            if (!showConsole)
                return;

            if (ImGui.Begin("Multiplayer Console", ref showConsole, ImGuiWindowFlags.MenuBar))
            {
                ShowConsoleContent(true);
            }

            ImGui.End();
        }

        public void ShowInTab()
        {
            using var _ = Profiler.Scope();

            ShowConsoleContent(false);
        }

        private void ShowConsoleContent(bool usesMenuBar)
        {
            using var _ = Profiler.Scope();

            // Toolbar
            if (usesMenuBar)
            {
                if (ImGui.BeginMenuBar())
                {
                    if (ImGui.Button("Clear"))
                    {
                        lock (_lock) { logEntries.Clear(); }
                    }
                    ImGui.SameLine();
                    ImGui.InputText("Filter", ref filter, 128);

                    ImGui.EndMenuBar();
                }
            }
            else
            {
                if (ImGui.Button("Clear"))
                {
                    lock (_lock) { logEntries.Clear(); }
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200);
                ImGui.InputText("Filter", ref filter, 128);
            }

                ImGui.Separator();

                // Scroll region
                ImGui.BeginChild("ConsoleScroll", new Vector2(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar);

                lock (_lock)
                {
                    foreach (var entry in logEntries)
                    {
                        if (!string.IsNullOrEmpty(filter) && entry.message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        Vector4 color = new Vector4(1f, 1f, 1f, 1f);
                        switch (entry.type)
                        {
                            case LogType.Warning:
                                color = new Vector4(1f, 1f, 0.3f, 1f);
                                break;
                            case LogType.Error:
                                color = new Vector4(1f, 0.4f, 0.4f, 1f);
                                break;
                            case LogType.Assert:
                                color = new Vector4(0.8f, 0.5f, 1f, 1f);
                                break;
                            case LogType.Exception:
                                color = new Vector4(1f, 0.4f, 0.4f, 1f);
                                break;
                            case LogType.Success:
                                color = new Vector4(0f, 1f, 0f, 1f);
                                break;
                            case LogType.NonImportant:
                                color = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
                                break;
                            default:
                                break;
                        }

                        string displayMsg = entry.count > 1 ? $"{entry.message} (x{entry.count})" : entry.message;

                        ImGui.TextColored(color, displayMsg);
                    }
                }

                if (autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                    ImGui.SetScrollHereY(1.0f);

                ImGui.EndChild();
        }
    }
}