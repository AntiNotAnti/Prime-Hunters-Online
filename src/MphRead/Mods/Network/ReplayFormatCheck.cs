using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Collections.Generic;
using MphRead.Mods.Multiplayer;

namespace MphRead.Mods.Network
{
    internal static class ReplayFormatCheck
    {
        public static int Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "fruity-replay-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            int checks = 0;
            void Require(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                checks++;
            }
            try
            {
                var match = new MatchStatePacket { RoomKey = "MP1 SANCTORUS", NextRoomKey = "",
                    Mode = (byte)GameMode.Battle, TimeRemaining = 300, Flags = MatchStatePacket.FlagInProgress,
                    MatchId = 1, AuthorityEpoch = 1 };
                var matchBytes = new byte[1 + MatchStatePacket.Size];
                matchBytes[0] = (byte)PacketType.MatchState; match.Write(matchBytes.AsSpan(1));
                var session = new SessionStatePacket
                {
                    Phase = SessionPhase.InMatch, Policy = ServerSessionPolicy.Lobby,
                    OwnerSlot = byte.MaxValue, MaxPlayers = 8, MatchId = match.MatchId,
                    AuthorityEpoch = match.AuthorityEpoch,
                    WorldProfile = MatchWorldProfile.Resolve(8),
                    Match = new MatchDefinition
                    {
                        RoomKey = match.RoomKey, Mode = GameMode.Battle, Format = MatchFormat.Auto,
                        DisablePowerups = true
                    }
                };
                var sessionBytes = new byte[1 + SessionStatePacket.Size];
                sessionBytes[0] = (byte)PacketType.SessionState; session.Write(sessionBytes.AsSpan(1));
                var metadata = new ReplayMetadata { RoomKey = match.RoomKey, Mode = GameMode.Battle,
                    Bootstrap = new ReplayBootstrap { Packets = new[] { sessionBytes, matchBytes } } };

                // Current snapshots append match-time and health-sync state after
                // the player array. The replay validator must accept the same wire
                // packet the live session accepts.
                const uint bootstrapFrame = 1;
                int timeOffset = SnapshotHeader.Size;
                int healthOffset = timeOffset + NetMatchTimeSync.Size;
                byte[] snapshotPayload = new byte[healthOffset + NetHealthSync.HeaderSize];
                new SnapshotHeader
                {
                    MatchId = match.MatchId,
                    AuthorityEpoch = match.AuthorityEpoch,
                    Frame = bootstrapFrame,
                    PlayerCount = 0
                }.Write(snapshotPayload);
                NetMatchTimeSync.Write(snapshotPayload.AsSpan(timeOffset, NetMatchTimeSync.Size));
                BinaryPrimitives.WriteUInt16LittleEndian(snapshotPayload.AsSpan(healthOffset), match.MatchId);
                snapshotPayload[healthOffset + 2] = 0;
                byte[] snapshotBytes = new byte[1 + snapshotPayload.Length];
                snapshotBytes[0] = (byte)PacketType.Snapshot;
                snapshotPayload.CopyTo(snapshotBytes.AsSpan(1));
                var timelineRecorder = new ReplayRecorder();
                timelineRecorder.AcceptMatch(match, 0);
                var timelineRoster = RosterPacket.Create();
                timelineRoster.MatchId = match.MatchId;
                timelineRoster.AuthorityEpoch = match.AuthorityEpoch;
                timelineRecorder.AcceptRoster(timelineRoster, 0);
                timelineRecorder.AcceptSnapshot(snapshotBytes, 1, bootstrapFrame);
                Require(timelineRecorder.Timeline.RestorePointCount == 1, "accepted facts create baseline");
                Require(timelineRecorder.Timeline.TryFreeze(1, 1, out var timelineClip)
                    && timelineClip!.RestorePoint.Kind == ReplayRestoreKind.NetworkBaseline,
                    "network baseline is not a full scene checkpoint");
                var nextMatch = match; nextMatch.MatchId++;
                timelineRecorder.AcceptMatch(nextMatch, 2);
                Require(timelineRecorder.Timeline.NeedsRestorePoint, "match transition clears baseline");
                timelineRecorder.AcceptRoster(timelineRoster, 2);
                timelineRecorder.AcceptSnapshot(snapshotBytes, 3, 3);
                Require(timelineRecorder.Timeline.NeedsRestorePoint, "old roster cannot bootstrap new match");
                timelineRoster.MatchId = nextMatch.MatchId;
                timelineRecorder.AcceptRoster(timelineRoster, 4);
                timelineRecorder.AcceptSnapshot(snapshotBytes, 5, bootstrapFrame);
                Require(timelineRecorder.Timeline.NeedsRestorePoint, "old snapshot cannot bootstrap new match");
                timelineRecorder.Reset(); timelineRecorder.AcceptMatch(match, 0);
                timelineRoster.MatchId = match.MatchId; timelineRecorder.AcceptRoster(timelineRoster, 0);
                timelineRecorder.AcceptSnapshot(snapshotBytes, 1, bootstrapFrame);
                var roomTransition = match; roomTransition.RoomKey = "MP2 HIGHGROUND";
                timelineRecorder.AcceptMatch(roomTransition, 2);
                Require(timelineRecorder.Timeline.NeedsRestorePoint, "room transition clears historical state");
                var currentSnapshotMetadata = new ReplayMetadata { RoomKey = match.RoomKey, Mode = GameMode.Battle,
                    Bootstrap = new ReplayBootstrap { Packets = new[] { sessionBytes, matchBytes, snapshotBytes } } };
                string currentSnapshot = Path.Combine(directory, "current-snapshot.ppdemo");
                using (var snapshotWriter = new ReplayWriterV3(currentSnapshot, currentSnapshotMetadata)) { }
                Require(File.Exists(currentSnapshot), "current snapshot tails accepted in bootstrap");

                byte[] packet = { (byte)PacketType.Ping, 17, 42 };
                string clean = Path.Combine(directory, "clean.ppdemo");
                using (var writer = new ReplayWriterV3(clean, metadata))
                {
                    for (uint frame = 0; frame < 400; frame++)
                    {
                        writer.WriteRecord(frame, packet);
                        if (frame % 60 == 0)
                            writer.WriteEvent(new(frame, ReplayEventType.ScoreChanged, 0,
                                Value: (int)frame));
                        if (frame == 90)
                            writer.WriteEvent(new(frame, ReplayEventType.WeaponFired, 0,
                                Value: (int)BeamType.Imperialist));
                    }
                }
                Require(File.Exists(clean) && !File.Exists(clean + ".part"), "atomic finalization");
                using (var reader = DemoReader.Open(clean, out var result))
                {
                    Require(result == ReplayOpenResult.Success && reader?.Metadata?.DurationFrames == 399, "metadata-only duration");
                    Require(reader!.Metadata!.Events.Count == 8, "event index");
                    Require(reader.Metadata.Events.Any(e =>
                        e.Type == ReplayEventType.WeaponFired
                        && e.Value == (int)BeamType.Imperialist),
                        "weapon event roundtrip");
                    uint count = 0;
                    while (reader.ReadNext() is { } record)
                    {
                        Require(record.Frame == count++ && record.Data.AsSpan().SequenceEqual(packet), "ordered packet roundtrip");
                    }
                    Require(count == 400 && reader.LastResult == ReplayOpenResult.Success, "clean EOF");
                    Require(reader.Metadata.Integrity == ReplayIntegrity.Healthy, "validated integrity");
                }
                Require(ReplayArchive.Validate(clean) == ReplayOpenResult.Success, "validator");
                // Two passive readers can coexist with a foreground network session.
                // Neither joining, seeking, stopping nor recorded control traffic may
                // change foreground identity, transport, RNG or Replay Studio controls.
                NetSession.StartPlayback();
                NetSession.ApplyMatchState(match, false);
                Rng.SetRng1(12345);
                Rng.SetRng2(67890);
                ReplayController.Begin();
                ReplayController.SetPlaybackRate(2);
                var passiveA = new PassiveReplaySessionHost();
                var passiveB = new PassiveReplaySessionHost();
                using (var first = new ReplayPlaybackSession(passiveA))
                using (var second = new ReplayPlaybackSession(passiveB))
                {
                    Require(first.Join(clean) && second.Join(clean), "independent passive readers open");
                    first.Transport.SetPlaybackRate(.25f);
                    second.Transport.Pause();
                    for (int i = 0; i < 12; i++) first.PumpFrame();
                    Require(first.CurrentFrame == 11 && second.CurrentFrame == 0,
                        "session reader clocks are independent");
                    Require(second.Transport.IsPaused && first.Transport.PlaybackRate == .25f,
                        "session controls are independent");
                    first.Transport.Seek(399);
                    Require(first.Transport.FramesDue() == 120, "seek batch is bounded to 120 steps");
                    first.Transport.Seek(20);
                    Require(first.Transport.FramesDue() == 9, "seek batch stops exactly at target");
                    first.Transport.Seek(1);
                    first.Stop();
                    Require(!first.Transport.TakeRebuild(out _, out _), "stop clears pending rebuild");
                    Require(second.IsActive, "stopping one reader preserves the other");
                    foreach (PacketType control in new[] { PacketType.Bye, PacketType.Welcome, PacketType.Authority })
                        passiveB.Inject(new[] { (byte)control }, 13);
                    Require(passiveB.Match?.MatchId == match.MatchId, "control packets cannot mutate replica match");
                    Require(NetSession.Active && NetSession.LocalSlot == -1 && !NetSession.IsAuthority
                        && NetSession.CurrentMatchId == match.MatchId, "passive readers preserve live connection identity");
                    Require(Rng.Rng1 == 12345 && Rng.Rng2 == 67890, "passive readers preserve live RNG");
                    Require(ReplayController.PlaybackRate == 2 && ReplayController.State == ReplayState.Playing,
                        "passive readers preserve Studio transport");
                }
                NetSession.Stop();
                ReplayController.Stop();
                using (var indexed = DemoReader.Open(clean, out var indexedResult))
                {
                    Require(indexedResult == ReplayOpenResult.Success && indexed != null,
                        "indexed reader opens");
                    DemoRecord? after250 = indexed!.SeekAfter(250);
                    Require(after250 is DemoRecord seekRecord && seekRecord.Frame == 251,
                        "v3 footer index seek lands after requested frame");
                }
                string hashed = Path.Combine(directory, "hashed.ppdemo");
                var references = new[] { new ReplayExpectedHash(0, new string('A', 64)), new ReplayExpectedHash(300, new string('B', 64)) };
                Require(ReplayArchive.WithExpectedHashes(clean, hashed, references) == ReplayOpenResult.Success, "store reference hashes in v3 copy");
                using (var reader = DemoReader.Open(hashed))
                {
                    Require(reader?.Metadata?.ExpectedHashes.Count == 2 && reader.Metadata.ExpectedHashes[1] == references[1], "reference hash roundtrip");
                    Require(reader!.Metadata!.HashSchema == ReplayStateHash.Schema && reader.Metadata.HashBuildId == ReplayStateHash.BuildId, "reference hash schema/build");
                }
                using (var reader = DemoReader.Open(hashed, out _, metadataOnly: true))
                    Require(reader?.Metadata?.ExpectedHashes.Count == 0, "library does not retain reference hashes");
                Require(ReplayArchive.Validate(hashed) == ReplayOpenResult.Success, "hash footer integrity");
                byte[] hashBytes = File.ReadAllBytes(hashed);
                int hashFooter = (int)BinaryPrimitives.ReadInt64LittleEndian(hashBytes.AsSpan(hashBytes.Length - 12));
                int hashFooterLength = BinaryPrimitives.ReadInt32LittleEndian(hashBytes.AsSpan(hashFooter + 4));
                string badHash = Path.Combine(directory, "bad-hash.ppdemo");
                byte[] duplicateFrame = (byte[])hashBytes.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(duplicateFrame.AsSpan(duplicateFrame.Length - 12 - 36), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(duplicateFrame.AsSpan(hashFooter + 8),
                    ReplayFormatV3.Crc(duplicateFrame.AsSpan(hashFooter + 12, hashFooterLength)));
                File.WriteAllBytes(badHash, duplicateFrame);
                Require(DemoReader.Open(badHash, out var badHashResult) == null && badHashResult == ReplayOpenResult.Corrupt,
                    "CRC-valid duplicate hash frames rejected");
                byte[] unboundedCount = (byte[])hashBytes.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(unboundedCount.AsSpan(unboundedCount.Length - 12 - 72 - 4), int.MaxValue);
                BinaryPrimitives.WriteUInt32LittleEndian(unboundedCount.AsSpan(hashFooter + 8),
                    ReplayFormatV3.Crc(unboundedCount.AsSpan(hashFooter + 12, hashFooterLength)));
                File.WriteAllBytes(badHash, unboundedCount);
                Require(DemoReader.Open(badHash, out var badHashCount) == null && badHashCount == ReplayOpenResult.Corrupt,
                    "bounded hash allocation");
                using (var transport = new NetTransport(0, playbackOnly: true))
                {
                    long sent = NetTransport.TotalPacketsSent;
                    Require(transport.LocalPort == 0, "playback opens no socket");
                    transport.Send(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9), PacketType.Ping, packet);
                    Require(NetTransport.TotalPacketsSent == sent, "playback sends no traffic");
                    for (int i = 0; i < 4096; i++) transport.EnqueueForPlayback(packet, packet.Length);
                    int delivered = 0; foreach (var unused in transport.Drain()) delivered++;
                    Require(delivered == 4096 && transport.PacketsDropped == 0, "recorded packet burst is not dropped");
                }
                Require(DemoPlayback.Join(clean), "matching protocol bootstrap joins");
                Require(NetSession.ActiveMatchDefinition?.DisablePowerups == true,
                    "session rules survive replay bootstrap");
                foreach (byte[] control in new[] { new byte[] { (byte)PacketType.Welcome, 0 },
                    new byte[] { (byte)PacketType.Authority }, new byte[] { (byte)PacketType.Bye } })
                    NetSession.InjectPlaybackPacket(control, control.Length);
                NetSession.Update(0);
                Require(NetSession.Active && NetSession.LocalSlot == -1 && !NetSession.IsAuthority,
                    "reconnect/control packets cannot create a local player or end playback");
                DemoPlayback.Stop(); NetSession.Stop();
                string extracted = Path.Combine(directory, "extracted.ppdemo");
                Require(ReplayArchive.Extract(clean, 60, 180, extracted) == ReplayOpenResult.Success, "extract clip");
                using (var reader = DemoReader.Open(extracted))
                {
                    Require(reader?.Metadata?.Type == ReplayType.Clip && reader.DurationFrames == 120, "clip metadata");
                    Require(reader!.Metadata!.Events.Count == 4
                        && reader.Metadata.Events[0].Frame == 0
                        && reader.Metadata.Events.Any(e =>
                            e.Type == ReplayEventType.WeaponFired && e.Frame == 30),
                        "clip event rebase");
                    Require(reader.ReadNext()?.Frame == 0, "clip frame rebase");
                }
                string interrupted = Path.Combine(directory, "interrupted.ppdemo");
                var partialWriter = new ReplayWriterV3(interrupted, metadata);
                for (uint i = 0; i < 360; i++) partialWriter.WriteRecord(i, packet);
                partialWriter.Abort();
                string part = interrupted + ".part";
                Require(ReplayArchive.Validate(part) == ReplayOpenResult.Truncated, "missing footer");
                using (var stream = new FileStream(part, FileMode.Open, FileAccess.Write)) stream.SetLength(stream.Length - 10);
                Require(ReplayArchive.Recover(part, out string? recovered, out var recovery) && recovered != null
                    && recovery == ReplayOpenResult.Truncated, "recover interrupted chunk");
                using (var reader = DemoReader.Open(recovered!))
                {
                    int count = 0; while (reader!.ReadNext() != null) count++;
                    Require(count == 120 && reader.Metadata!.Recovered && reader.LastResult == ReplayOpenResult.Success,
                        "only complete CRC-valid chunks recovered");
                }
                byte[] bytes = File.ReadAllBytes(clean);
                int firstChunk = 14 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6));
                string corrupt = Path.Combine(directory, "corrupt.ppdemo");
                byte[] changed = (byte[])bytes.Clone(); changed[firstChunk + 24] ^= 0x80;
                File.WriteAllBytes(corrupt, changed);
                Require(ReplayArchive.Validate(corrupt) == ReplayOpenResult.Corrupt, "bad chunk CRC");
                changed = (byte[])bytes.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(firstChunk + 20), int.MaxValue);
                File.WriteAllBytes(corrupt, changed);
                Require(ReplayArchive.Validate(corrupt) == ReplayOpenResult.Corrupt, "bounded decompression allocation");
                changed = (byte[])bytes.Clone(); changed[14] ^= 1; File.WriteAllBytes(corrupt, changed);
                Require(DemoReader.Open(corrupt, out var damagedHeader) == null && damagedHeader == ReplayOpenResult.Corrupt, "header CRC");
                changed = (byte[])bytes.Clone(); changed[0] = 0; File.WriteAllBytes(corrupt, changed);
                Require(DemoReader.Open(corrupt, out var magic) == null && magic == ReplayOpenResult.InvalidMagic, "bad magic");
                changed = (byte[])bytes.Clone(); changed[4] = 77; File.WriteAllBytes(corrupt, changed);
                Require(DemoReader.Open(corrupt, out var version) == null && version == ReplayOpenResult.UnsupportedFormat, "unknown format");
                changed = (byte[])bytes.Clone(); changed[5]++; File.WriteAllBytes(corrupt, changed);
                Require(!DemoPlayback.Join(corrupt) && DemoPlayback.LastResult == ReplayOpenResult.ProtocolMismatch, "protocol refuses before playback");
                Require(DemoReader.Open(Path.Combine(directory, "missing"), out var missing) == null
                    && missing == ReplayOpenResult.FileMissing, "missing file");
                string legacy = Path.Combine(directory, "v2.ppdemo");
                using (var writer = new DemoWriter(legacy)) { writer.WriteRecord(0, packet); writer.WriteRecord(900, packet); }
                using (var reader = DemoReader.Open(legacy))
                {
                    Require(reader?.FormatVersion == 2 && reader.ReadNext()?.Frame == 0 && reader.ReadNext()?.Frame == 900,
                        "unchanged v2 delta/long-gap compatibility");
                    Require(reader!.ReadNext() == null && reader.LastResult == ReplayOpenResult.Success, "v2 EOF");
                }
                string truncated = Path.Combine(directory, "v2-truncated.ppdemo");
                using (var stream = File.Create(truncated))
                {
                    stream.Write(DemoFile.Magic); stream.WriteByte(2); stream.WriteByte((byte)NetConfig.ProtocolVersion);
                    using var deflate = new DeflateStream(stream, CompressionLevel.Fastest);
                    deflate.Write(new byte[] { 0, 10, 0, 1 });
                }
                Require(ReplayArchive.Validate(truncated) == ReplayOpenResult.Truncated, "explicit v2 partial record");
                string empty = Path.Combine(directory, "empty.ppdemo");
                using (var writer = new DemoWriter(empty)) { }
                Require(ReplayArchive.Validate(empty) == ReplayOpenResult.Empty, "empty replay");

                // Dedicated-server retention is pure file policy and needs no game
                // assets. Protect favorites/newest first, then age and byte quota.
                DateTime retentionNow = new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
                string retentionDir = Path.Combine(directory, "server-retention-age");
                Directory.CreateDirectory(retentionDir);
                string ReplayFile(string name, int daysOld, int bytes = 1024)
                {
                    string path = Path.Combine(retentionDir, name + DemoFile.Extension);
                    File.WriteAllBytes(path, new byte[bytes]);
                    File.SetLastWriteTimeUtc(path, retentionNow.AddDays(-daysOld));
                    return path;
                }
                string favorite = ReplayFile("favorite-old", 40);
                File.WriteAllText(favorite + ".favorite", "");
                string staleA = ReplayFile("stale-a", 30);
                string staleB = ReplayFile("stale-b", 20);
                string recentA = ReplayFile("recent-a", 2);
                string recentB = ReplayFile("recent-b", 1);
                ServerReplayRetentionResult ageResult = ServerReplayRetention.Apply(
                    retentionDir, new ServerReplayPolicy(true, 0, 14, 2),
                    nowUtc: retentionNow);
                Require(ageResult.DeletedFiles == 2
                    && File.Exists(favorite)
                    && !File.Exists(staleA) && !File.Exists(staleB)
                    && File.Exists(recentA) && File.Exists(recentB),
                    "server replay age/keep-last/favorite retention");

                string quotaDir = Path.Combine(directory, "server-retention-quota");
                Directory.CreateDirectory(quotaDir);
                string QuotaFile(string name, int daysOld)
                {
                    string path = Path.Combine(quotaDir, name + DemoFile.Extension);
                    File.WriteAllBytes(path, new byte[1024]);
                    File.SetLastWriteTimeUtc(path, retentionNow.AddDays(-daysOld));
                    return path;
                }
                string quotaOldest = QuotaFile("oldest", 4);
                string quotaOlder = QuotaFile("older", 3);
                string quotaRecent = QuotaFile("recent", 2);
                string quotaNewest = QuotaFile("newest", 1);
                ServerReplayRetentionResult quotaResult = ServerReplayRetention.Apply(
                    quotaDir, new ServerReplayPolicy(true, 0, 0, 1),
                    nowUtc: retentionNow, storageLimitBytes: 2300);
                Require(quotaResult.LimitSatisfied && quotaResult.DeletedFiles == 2
                    && !File.Exists(quotaOldest) && !File.Exists(quotaOlder)
                    && File.Exists(quotaRecent) && File.Exists(quotaNewest),
                    "server replay byte quota preserves newest");

                // Mutate packet/chunk/footer/header bytes without trusting any unverified length.
                var random = new Random(173);
                for (int i = 0; i < 80; i++)
                {
                    changed = (byte[])bytes.Clone(); changed[random.Next(6, changed.Length)] ^= (byte)(1 << random.Next(8));
                    File.WriteAllBytes(corrupt, changed);
                    _ = ReplayArchive.Validate(corrupt);
                    checks++;
                }
                NetSession.StartPlayback();
                // DemoClip only records inside a valid network stream. Match
                // identity zero is deliberately rejected by ApplyMatchState,
                // so give this stalled-client fixture the same non-zero
                // lifecycle identity a real replay session has.
                NetSession.ApplyMatchState(new MatchStatePacket
                {
                    MatchId = 1,
                    AuthorityEpoch = 1,
                    RoomKey = "",
                    NextRoomKey = "",
                    Mode = (byte)GameMode.Battle
                }, false);
                int priorSeconds = DemoClip.Seconds;
                try
                {
                    DemoClip.Seconds = 120;
                    // No simulation frames advance: a packet flood during a stalled client
                    // must remain bounded by memory as well as by the time window.
                    for (int i = 0; i < 1000000; i++) DemoClip.Add(packet.AsSpan(0, 1));
                    Require(DemoClip.BufferedBytes <= 24 * 1024 * 1024 && DemoClip.BufferedPages > 1, "stalled packet flood is bounded");
                    DemoClip.Purge();
                    Require(DemoClip.BufferedBytes == 0 && DemoClip.BufferedPages == 0, "pooled clip pages released");
                }
                finally { DemoClip.Seconds = priorSeconds; NetSession.Stop(); }
                Console.WriteLine($"[replayformat] PASS {checks} checks (v2/v3, order, metadata, CRC, recovery, extraction, malformed files)");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine($"[replayformat] FAIL: {ex}"); return 1; }
            finally
            {
                DemoPlayback.Stop(); NetSession.Stop();
                // Only this freshly created, unpredictable temporary directory is removed.
                Directory.Delete(directory, true);
            }
        }
    }
}
