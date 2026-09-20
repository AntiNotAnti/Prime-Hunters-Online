using System.Collections.Generic;
using MphRead.Mods.Network;

namespace MphRead.Mods.Chat
{
    public static class NetChat
    {
        private static readonly List<string> _history = new();
        public static IReadOnlyList<string> History => _history;
        private static readonly List<(string Text, bool System)> _entries = new();
        public static IReadOnlyList<(string Text, bool System)> Entries => _entries;
        public static int Revision { get; private set; }
        public static void Clear() { _history.Clear(); _entries.Clear(); Revision++; }
        public static void Receive(ChatPacket packet)
        {
            Remember(packet);
            ChatBox.Receive(packet);
        }
        public static void Remember(ChatPacket packet)
        {
            if (_history.Count == 64) { _history.RemoveAt(0); _entries.RemoveAt(0); }
            _history.Add(packet.Kind == ChatPacket.KindSystem ? packet.Text : $"{(packet.Kind == ChatPacket.KindTeam ? "[Team] " : "")}{packet.Name}: {packet.Text}");
            _entries.Add((_history[^1], packet.Kind == ChatPacket.KindSystem));
            Revision++;
        }
        public static void Send(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            NetSession.SendChat(text);
        }
    }
}
