using System;
using System.Collections.Generic;
using System.Text;
using MphRead.Mods.Network;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace MphRead.Mods.Chat
{
    /// <summary>
    /// What somebody typed, and when it arrived.
    ///
    /// The name is kept apart from the text because the HUD draws them in
    /// different colours -- who said it, then what they said -- which is the
    /// one piece of formatting a chat log genuinely needs: a line with no
    /// visible speaker is indistinguishable from the game talking to you.
    /// </summary>
    internal readonly struct ChatLine
    {
        public readonly string Name;
        public readonly string Text;
        public readonly byte Kind;
        public readonly long ArrivedAt;

        public ChatLine(string name, string text, byte kind, long arrivedAt)
        {
            Name = name;
            Text = text;
            Kind = kind;
            ArrivedAt = arrivedAt;
        }
    }

    /// <summary>
    /// The chat line, and the log above it.
    ///
    /// T opens a prompt in the corner of the screen, Enter sends what is in
    /// it, Escape throws it away. Everything a player types goes to the
    /// server, which stamps it with who really sent it and passes it on --
    /// see <see cref="ChatPacket"/> for why the sender's own claim about that
    /// is not trusted.
    ///
    /// The log is deliberately not a window: three lines, small, top left,
    /// each one gone ten seconds after it arrived. A chat you have to dismiss
    /// is a chat that is covering the game while somebody is shooting at you,
    /// and the DS's screen is 256 units wide -- there is no room here for
    /// anything with a scrollbar.
    ///
    /// Wall clock rather than frames, because the log has to age at the same
    /// rate whatever the game is doing: a message that arrived while the room
    /// was loading, or while the pause menu was open, has still been on
    /// screen for as long as it has been on screen.
    /// </summary>
    public static class ChatBox
    {
        /// <summary>How many lines are on screen at once. Quake 3's number.</summary>
        public const int VisibleLines = 3;

        /// <summary>
        /// How much a player may type. Shorter than the packet's 96 bytes on
        /// purpose: the prompt draws the tail of a long line rather than
        /// truncating it, so the limit is what fits in the log afterwards
        /// rather than what fits on the wire.
        /// </summary>
        public const int MaxLength = 80;

        private const long HoldMilliseconds = 10_000;
        private const long FadeMilliseconds = 1_000;

        /// <summary>Lines kept, oldest first. Only the newest three are drawn.</summary>
        private static readonly List<ChatLine> _lines = new();
        private static readonly StringBuilder _compose = new();
        private static readonly object _lock = new();

        /// <summary>
        /// True while the prompt is up: the player is typing, not playing.
        /// Read by the renderer, which stops feeding their keys to the game
        /// for as long as it is set.
        /// </summary>
        public static bool Composing { get; private set; }

        /// <summary>
        /// Swallow the character event that the key which opened the prompt
        /// is about to produce.
        ///
        /// GLFW raises the key callback before the character callback for the
        /// same physical press, so opening on T and then accepting text meant
        /// every message began with a stray "t". One flag rather than a
        /// timestamp: the two events are consecutive by definition, so
        /// anything else typed afterwards is genuinely typed.
        /// </summary>
        private static bool _swallowNextChar;

        /// <summary>
        /// Set when the prompt closes, cleared by whoever acts on it.
        ///
        /// Aim is the difference between two mouse positions a frame apart,
        /// and the player's is not sampled at all while they are typing --
        /// so the frame the prompt closes on holds the whole of however far
        /// the mouse moved during the message, and applying it snapped the
        /// view round. The renderer takes this and throws that one frame's
        /// difference away.
        /// </summary>
        private static bool _justClosed;

        /// <summary>True exactly once after the prompt closes.</summary>
        public static bool ConsumeJustClosed()
        {
            if (!_justClosed)
            {
                return false;
            }
            _justClosed = false;
            return true;
        }

        /// <summary>Lines this client sent, and lines it received. For the test harness.</summary>
        public static int Sent { get; private set; }
        public static int Received { get; private set; }

        /// <summary>What is in the prompt right now, for the HUD to draw.</summary>
        internal static string ComposeText
        {
            get
            {
                lock (_lock)
                {
                    return _compose.ToString();
                }
            }
        }

        /// <summary>
        /// The newest lines that have not aged out, oldest first, with the
        /// alpha each should be drawn at. Allocates a small list per frame,
        /// which is what a HUD element drawn behind a `if (any)` check can
        /// afford and a per-frame counter cannot.
        /// </summary>
        internal static void CollectVisible(List<(ChatLine Line, float Alpha)> into)
        {
            into.Clear();
            long now = Environment.TickCount64;
            lock (_lock)
            {
                for (int i = _lines.Count - 1; i >= 0 && into.Count < VisibleLines; i--)
                {
                    long age = now - _lines[i].ArrivedAt;
                    if (age >= HoldMilliseconds)
                    {
                        // Everything before this one is older still.
                        break;
                    }
                    float alpha = age > HoldMilliseconds - FadeMilliseconds
                        ? (HoldMilliseconds - age) / (float)FadeMilliseconds
                        : 1;
                    into.Add((_lines[i], alpha));
                }
            }
            into.Reverse();
        }

        /// <summary>
        /// Whether there is anybody to talk to.
        ///
        /// The story is a match with one player in it: there is no server, no
        /// roster and nobody to read a line -- and T is a key the adventure's
        /// own controls may want. Not "is a session running", because an
        /// offline match against bots is still a match, and the log is where
        /// the game's own notices go.
        /// </summary>
        public static bool Available => !GameState.SinglePlayer;

        /// <summary>Anything on screen at all -- the log or the prompt.</summary>
        internal static bool Visible
        {
            get
            {
                if (!Available)
                {
                    return false;
                }
                if (Composing)
                {
                    return true;
                }
                long now = Environment.TickCount64;
                lock (_lock)
                {
                    return _lines.Count > 0
                        && now - _lines[^1].ArrivedAt < HoldMilliseconds;
                }
            }
        }

        /// <summary>Forget everything. Called when a session ends.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
                _compose.Clear();
            }
            Composing = false;
            _swallowNextChar = false;
        }

        /// <summary>
        /// A line from the network, already vetted by the server.
        /// </summary>
        public static void Receive(ChatPacket packet)
        {
            string name = packet.Name.Length > 0 ? packet.Name : $"Player{packet.Slot}";
            if (packet.Kind == ChatPacket.KindSystem)
            {
                Add("", packet.Text, ChatPacket.KindSystem);
            }
            else
            {
                Add(name, packet.Text, packet.Kind);
            }
            Received++;
        }

        /// <summary>The game talking rather than a player: no name, its own colour.</summary>
        public static void System(string text) => Add("", text, ChatPacket.KindSystem);

        public static void Add(string name, string text, byte kind)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return;
            }
            lock (_lock)
            {
                _lines.Add(new ChatLine(name, text, kind, Environment.TickCount64));
                // A cap, not a window: CollectVisible only ever reads the tail,
                // and a match left running all night must not grow a list of
                // every word anyone said in it.
                if (_lines.Count > 64)
                {
                    _lines.RemoveRange(0, _lines.Count - 64);
                }
            }
        }

        /// <summary>
        /// Open the prompt. Refused when there is nowhere to send to and
        /// nothing to say it over -- an offline match still accepts it, since
        /// the log is where the game's own notices go.
        /// </summary>
        /// <param name="swallowOpeningChar">
        /// Whether the key that opened this is about to arrive a second time
        /// as a character. True on the desktop, where GLFW raises the key
        /// callback before the character callback for one press; false on
        /// Android, where a key event carries its own character and is
        /// consumed where it arrives, and false for the touch button, which
        /// is not a key at all.
        /// </param>
        public static void Open(bool swallowOpeningChar = true)
        {
            if (Composing || !Available)
            {
                return;
            }
            lock (_lock)
            {
                _compose.Clear();
            }
            Composing = true;
            _swallowNextChar = swallowOpeningChar;
        }

        public static void Cancel()
        {
            lock (_lock)
            {
                _compose.Clear();
            }
            Composing = false;
            _swallowNextChar = false;
            _justClosed = true;
        }

        /// <summary>
        /// Send what is in the prompt and close it.
        ///
        /// The sender's own copy is added here rather than waiting for the
        /// server to send it back. Two reasons: a message that only appears
        /// after a round trip reads as a dropped one on a bad line, and a
        /// server built before chat existed never sends anything back at all
        /// -- with an echo, the player at least sees what they typed.
        /// </summary>
        public static void Submit()
        {
            string text;
            lock (_lock)
            {
                text = _compose.ToString().Trim();
                _compose.Clear();
            }
            Composing = false;
            _swallowNextChar = false;
            _justClosed = true;
            if (text.Length == 0)
            {
                return;
            }
            if (text.Length > MaxLength)
            {
                text = text[..MaxLength];
            }
            Send(text);
        }

        /// <summary>
        /// Put a line on the wire and on this screen. Public so the test
        /// harness can say something without a keyboard.
        /// </summary>
        public static void Send(string text)
        {
            bool prefix = text.StartsWith("/team ", StringComparison.OrdinalIgnoreCase);
            bool team = prefix && GameState.IsTeamMode(NetSession.ActiveMatchDefinition?.Mode ?? GameState.Mode);
            Add(NetSession.Active ? NetSession.PlayerName : "You", prefix ? text[6..].Trim() : text,
                team ? ChatPacket.KindTeam : ChatPacket.KindSay);
            if (NetSession.Active)
            {
                NetSession.SendChat(text);
                Sent++;
            }
        }

        /// <summary>
        /// One character the window received. Everything the DS font cannot
        /// draw is dropped here rather than sent and replaced with a question
        /// mark at the far end.
        /// </summary>
        public static void HandleText(int codePoint)
        {
            if (!Composing)
            {
                return;
            }
            if (_swallowNextChar)
            {
                _swallowNextChar = false;
                return;
            }
            if (codePoint < 32 || codePoint > 126)
            {
                return;
            }
            lock (_lock)
            {
                if (_compose.Length < MaxLength)
                {
                    _compose.Append((char)codePoint);
                }
            }
        }

        /// <summary>
        /// A key press from the game window, before anything else looks at it.
        /// Returns true when chat has taken it -- which is every key while the
        /// prompt is up, so that Escape closes the prompt instead of the
        /// match and Space types a space instead of jumping.
        /// </summary>
        /// <param name="canOpen">
        /// Whether there is a match to talk in. False in the model viewer and
        /// while a demo is playing back: a recording has nobody to send to,
        /// and its own chat lines are already in the file.
        /// </param>
        public static bool HandleKeyDown(KeyboardKeyEventArgs e, bool canOpen)
        {
            return HandleKeyDown(e.Key, e.Control, e.Alt || e.Command, canOpen);
        }

        /// <summary>
        /// The same, without an OpenTK event to build one from. Android has
        /// key events of its own and no window that raises those, and this is
        /// the whole of what the two platforms have to agree on.
        /// </summary>
        public static bool HandleKeyDown(Keys key, bool control, bool alt, bool canOpen,
            bool swallowOpeningChar = true)
        {
            if (!Composing)
            {
                // Unknown on both sides is not a match: a player who has set
                // chat_key=none has bound it to nothing, and Android reports
                // every key this build does not map as Unknown -- so without
                // this, unbinding chat would open it on any key at all.
                if (canOpen && Available && key != Keys.Unknown
                    && key == InputSettings.ChatKey && !alt && !control)
                {
                    Open(swallowOpeningChar);
                    return true;
                }
                return false;
            }
            switch (key)
            {
                case Keys.Enter:
                case Keys.KeyPadEnter:
                    Submit();
                    return true;
                case Keys.Escape:
                    Cancel();
                    return true;
                case Keys.Backspace:
                    lock (_lock)
                    {
                        if (_compose.Length > 0)
                        {
                            // Ctrl+Backspace takes the word, which is what
                            // every other text field on the machine does.
                            int end = _compose.Length;
                            int cut = control ? WordStart(_compose, end) : end - 1;
                            _compose.Remove(cut, end - cut);
                        }
                    }
                    return true;
                case Keys.V when control:
                    // Paste is deliberately not here: the clipboard belongs to
                    // the window, chat does not have one, and a chat line is
                    // eighty characters -- typing it is not the hard part.
                    return true;
                default:
                    // Everything else is swallowed while the prompt is up.
                    // The characters themselves arrive through HandleText;
                    // letting the key through as well would fire a weapon on
                    // the way past.
                    return true;
            }
        }

        private static int WordStart(StringBuilder text, int from)
        {
            int i = from;
            while (i > 0 && text[i - 1] == ' ')
            {
                i--;
            }
            while (i > 0 && text[i - 1] != ' ')
            {
                i--;
            }
            return i;
        }
    }
}
