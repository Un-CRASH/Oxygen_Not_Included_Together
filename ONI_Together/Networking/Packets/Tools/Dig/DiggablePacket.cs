using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System.IO;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Tools.Dig
{
    public class DiggablePacket : IPacket
    {
        /// <summary>
        /// Gets a value indicating whether incoming messages are currently being processed.
        /// Use in patches to prevent recursion when applying tool changes.
        /// </summary>
        public static bool ProcessingIncoming { get; private set; }

        internal static void ResetState() => ProcessingIncoming = false;

        private int             Cell;
        private int             AnimationDelay;
        private PrioritySetting Priority;

        public DiggablePacket()
        {
        }

        public DiggablePacket(int cell, int animationDelay)
        {
            using var _ = Profiler.Scope();

            Cell           = cell;
            AnimationDelay = animationDelay;
        }

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

            if (ToolMenu.Instance?.PriorityScreen != null)
                Priority = ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority();

            writer.Write(Cell);
            writer.Write(AnimationDelay);
            writer.Write((int)Priority.priority_class);
            writer.Write(Priority.priority_value);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            Cell           = reader.ReadInt32();
            AnimationDelay = reader.ReadInt32();
            Priority       = new PrioritySetting((PriorityScreen.PriorityClass)reader.ReadInt32(), reader.ReadInt32());
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

            if (!Grid.IsValidCell(Cell))
            {
                DebugConsole.LogWarning($"[DiggablePacket] Invalid cell {Cell}");
                return;
            }

            GameObject game_object;
            bool wasProcessing = ProcessingIncoming;
            ProcessingIncoming = true;
            // The priority below is part of applying the order: PrioritizablePatch must
            // not echo it back as a PrioritizeStatePacket.
            using var scope = OrderApplyScope.Enter();
            var filters = ForceAllDigFilters();
            try
            {
                game_object = DigTool.PlaceDig(Cell, AnimationDelay);
            }
            finally
            {
                RestoreDigFilters(filters);
                ProcessingIncoming = wasProcessing;
            }

            if (game_object == null)
            {
                if (Grid.Objects[Cell, (int)ObjectLayer.DigPlacer] == null)
                    DebugConsole.LogAggregated("DiggablePacket.Refused", $"[DiggablePacket] Nothing placed at cell {Cell} (solid={Grid.Solid[Cell]}, foundation={Grid.Foundation[Cell]})");
                return;
            }

            Prioritizable prioritizable = game_object.GetComponent<Prioritizable>();
            prioritizable?.SetMasterPriority(Priority);
        }

        /// <summary>
        /// DigTool.PlaceDig honours the LOCAL player's dig filter (tiles / natural
        /// backwall). The sender already decided the cell is diggable, so a receiver
        /// whose dig menu happens to have a layer switched off must not drop the order.
        /// </summary>
        private static ToolParameterMenu.ToggleState[] ForceAllDigFilters()
        {
            var tool = DigTool.Instance;
            if (tool == null || tool.currentFilters == null)
                return null;
            var saved = new ToolParameterMenu.ToggleState[tool.currentFilters.Length];
            for (int i = 0; i < saved.Length; i++)
            {
                saved[i] = tool.currentFilters[i].state;
                tool.currentFilters[i].state = ToolParameterMenu.ToggleState.On;
            }
            return saved;
        }

        private static void RestoreDigFilters(ToolParameterMenu.ToggleState[] saved)
        {
            var tool = DigTool.Instance;
            if (saved == null || tool == null || tool.currentFilters == null || tool.currentFilters.Length != saved.Length)
                return;
            for (int i = 0; i < saved.Length; i++)
                tool.currentFilters[i].state = saved[i];
        }
    }
}
