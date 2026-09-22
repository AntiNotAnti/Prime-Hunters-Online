using System;
using System.Collections.Generic;
using System.IO;

namespace MphRead.Mods.Network
{
    /// <summary>
    /// What a demo file actually contains, without loading a room to find
    /// out.
    ///
    /// A replay that looks wrong has two very different causes -- the file is
    /// missing something, or the player is mishandling what is there -- and
    /// nothing could tell them apart from the outside. The count that matters
    /// most is snapshots: they are the only carrier of health, score, the
    /// damage sequence and the spawn flag, so a demo with none of them opens
    /// on an empty room however good the rest of it is.
    /// </summary>
    internal static class DemoInfo
    {
        public static int Print(string path, bool replay)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[demo] no such file: {path}");
                return 1;
            }
            using DemoReader? reader = DemoReader.Open(path);
            if (reader == null)
            {
                Console.WriteLine($"[demo] \"{path}\" is not a demo this build can read "
                    + "(bad magic, unsupported format, or corrupt metadata)");
                return 1;
            }
            var counts = new Dictionary<PacketType, int>();
            var bytes = new Dictionary<PacketType, long>();
            long records = 0;
            long payload = 0;
            uint firstFrame = 0;
            uint lastFrame = 0;
            uint biggestGap = 0;
            uint previousFrame = 0;
            bool first = true;
            while (reader.ReadNext() is DemoRecord record)
            {
                records++;
                payload += record.Data.Length;
                if (first)
                {
                    firstFrame = record.Frame;
                    previousFrame = record.Frame;
                    first = false;
                }
                biggestGap = Math.Max(biggestGap, record.Frame - previousFrame);
                previousFrame = record.Frame;
                lastFrame = record.Frame;
                if (record.Data.Length > 0)
                {
                    var type = (PacketType)record.Data[0];
                    counts.TryGetValue(type, out int count);
                    counts[type] = count + 1;
                    bytes.TryGetValue(type, out long size);
                    bytes[type] = size + record.Data.Length;
                }
            }
            long onDisk = new FileInfo(path).Length;
            uint frames = records == 0 ? 0 : lastFrame - firstFrame + 1;
            double seconds = frames / 60.0;
            Console.WriteLine($"[demo] {path}");
            Console.WriteLine($"  protocol {reader.ProtocolVersion} "
                + $"(this build: {NetConfig.ProtocolVersion})"
                + (reader.ProtocolVersion == NetConfig.ProtocolVersion ? "" : "  -- MISMATCH"));
            Console.WriteLine($"  {records} record(s) over frames {firstFrame}-{lastFrame} "
                + $"({seconds:0.0} s at 60 fps)");
            Console.WriteLine($"  {onDisk / 1024.0:0.0} KiB on disk, {payload / 1024.0:0.0} KiB of "
                + $"packets -- {(payload > 0 ? (double)payload / onDisk : 0):0.00}x, "
                + $"{onDisk / Math.Max(seconds, 0.001) / 1024.0:0.0} KiB/s");
            Console.WriteLine($"  longest gap between records: {biggestGap} frame(s)");
            foreach (KeyValuePair<PacketType, int> entry in counts)
            {
                Console.WriteLine($"  {entry.Key,-14} {entry.Value,7} "
                    + $"({entry.Value / Math.Max(seconds, 0.001),6:0.0}/s, "
                    + $"{bytes[entry.Key] / 1024.0:0.0} KiB)");
            }
            if (!counts.ContainsKey(PacketType.Snapshot) && reader.Metadata?.WorldCheckpoint.Length is not > 0)
            {
                Console.WriteLine("  NO SNAPSHOTS -- nothing in this file ever places a player, "
                    + "so it will play back as an empty room.");
                return 1;
            }
            if (reader.LastResult != ReplayOpenResult.Success)
            {
                Console.WriteLine($"  Integrity failure: {reader.LastResult}");
                return 1;
            }
            return replay ? Replay(path) : 0;
        }

        /// <summary>
        /// Run the file through the real player and the real session, with no
        /// room and no window, and report how the packets landed.
        ///
        /// This is the measurement the complaint was about. A replay is only
        /// as smooth as the stream feeding it, and the number that says so is
        /// how many of its frames got a fresh snapshot: one each is what the
        /// recording had, none-then-two is the stutter, and the longest run
        /// of frames with nothing is how long a player stands still.
        /// </summary>
        private static int Replay(string path)
        {
            Console.WriteLine("  --- decoded by an isolated replay session ---");
            using var session = new ReplayPlaybackSession(new PassiveReplaySessionHost());
            if (!session.Join(path))
            {
                Console.WriteLine($"  replay failed: {session.LastError}"); return 1;
            }
            long snapshots = 0, intents = 0, frames = 0, framesWithSnapshot = 0, worstGap = 0, gap = 0;
            session.FactRead += (_, packet) =>
            {
                if (packet[0] == (byte)PacketType.Snapshot) snapshots++;
                if (packet[0] == (byte)PacketType.SlotIntent) intents++;
            };
            while (!session.AtEnd)
            {
                long before = snapshots;
                session.PumpFrame(); frames++;
                if (snapshots == before) { gap++; worstGap = Math.Max(gap, worstGap); }
                else { gap = 0; framesWithSnapshot++; }
            }
            Console.WriteLine($"  {frames} source frames decoded, {snapshots} snapshots, {intents} slot intents");
            Console.WriteLine($"  {framesWithSnapshot} frames carried a snapshot; longest recorded gap {worstGap}");
            Console.WriteLine($"  visible duration {session.DurationSeconds:0.00}s, lead-in {session.Metadata?.LeadInFrames ?? 0} frames");
            // Cadence describes the source, not presentation quality. Sparse or
            // bundled snapshots are legal; replica rendering uses its own clock.
            return session.LastResult == ReplayOpenResult.Success ? 0 : 1;
        }
    }
}
