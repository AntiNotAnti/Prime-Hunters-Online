using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using MphRead.Mods.Network;
using MphRead.Mods.Network.Telemetry;

namespace MphRead.NetTest;

internal static class Protocol19Tests
{
    private static int _checks;
    private static void Check(bool ok, string message)
    { _checks++; if (!ok) throw new InvalidOperationException(message); Console.WriteLine("V19 PASS " + message); }
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "prime-telemetry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Codecs(); FormEpisodes(); Timing(); Pipeline(root);
            Console.WriteLine($"PASS: {_checks} Protocol 19 combat/telemetry checks"); return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    private static void Codecs()
    {
        Check(NetConfig.ProtocolVersion == 19 && SnapshotFast.MaximumEncodedSize <= 1200, "version and eight-player fast packet budget");
        Span<byte> bytes = stackalloc byte[PlayerState.Size];
        foreach (ushort health in new ushort[] { 0, 1, 37, 100, ushort.MaxValue })
        {
            var state = new PlayerState { SlotIndex = 7, SlotGeneration = 65535, LifeId = 42, HalfturretActive = true, HalfturretHealth = health };
            state.Write(bytes); var decoded = PlayerState.Read(bytes);
            Check(decoded.HalfturretActive && decoded.HalfturretHealth == health && decoded.LifeId == 42 && decoded.SlotGeneration == 65535, "turret canonical codec " + health);
        }
        Span<byte> ackBytes = stackalloc byte[CombatAckEntry.Size];
        for (byte result = 0; result <= (byte)CombatAckResult.Corrected; result++)
        {
            var ack = new CombatAckEntry { ClaimId = 0x1234, Result = result, VictimSlot = 7, VictimGeneration = 65535,
                VictimLife = 123, DamageApplied = 201, HealthAfter = 9, HalfturretHealthAfter = 1, DamageSequence = 0xDEADBEEF, Flags = (CombatAckFlags)127 };
            ack.Write(ackBytes); var decoded = CombatAckEntry.Read(ackBytes);
            Check(decoded.Equals(ack) && ackBytes[0] == 0x34 && ackBytes[1] == 0x12 && ackBytes[14] == 0xEF, "CombatAck exact little-endian outcome " + result);
        }
        var canonical = new byte[SnapshotHeader.Size + 8 * PlayerState.Size];
        new SnapshotHeader { PlayerCount = 8 }.Write(canonical);
        for (byte i = 0; i < 8; i++) new PlayerState { SlotIndex = i, HalfturretActive = true, HalfturretHealth = ushort.MaxValue }.Write(canonical.AsSpan(SnapshotHeader.Size + i * PlayerState.Size));
        byte[] fast = new byte[1200]; int length = SnapshotFast.Write(canonical, fast);
        Check(length + NetHeader.Size <= 1200, "eight maximum Weavel states fit realtime MTU");
    }
    private static void FormEpisodes()
    {
        Type type = typeof(PlayerReplicationBridge).Assembly.GetType("MphRead.Mods.Network.FormReconciliation")!;
        object state = Activator.CreateInstance(type)!;
        var step = type.GetMethod("Step")!;
        for (uint frame = 1; frame <= 90; frame++)
        {
            object? correction = step.Invoke(state, new object[] { frame, true, false, frame % 3 != 0, false, 400 });
            Check(frame < 91 && correction != null, "animation flicker frame " + frame);
        }
        Check(step.Invoke(state, new object[] { 91u, true, false, true, false, 400 })!.ToString() == "Force"
            && type.GetProperty("Reason")!.GetValue(state)!.ToString() == "ForcedMaximumMismatch", "absolute mismatch survives animation flicker");
        uint episode = (uint)type.GetProperty("EpisodeId")!.GetValue(state)!;
        step.Invoke(state, new object[] { 92u, false, true, false, true, 400 });
        Check((uint)type.GetProperty("EpisodeId")!.GetValue(state)! == episode + 1, "desired form changes episode identity");
        step.Invoke(state, new object[] { 93u, false, false, false, false, 400 });
        Check(!(bool)type.GetProperty("EpisodeActive")!.GetValue(state)!, "matching form ends episode");
    }
    private static void Timing()
    {
        Check(!LagCompensationPolicy.Configure("Enforce"), "stricter enforcement remains disabled");
        LagCompensationPolicy.Configure("Shadow");
        LagCompensationPolicy.SetTiming(1, new LagTiming(100, 20, 80, 3));
        var decision = LagCompensationPolicy.Evaluate(1, 100, 1, 128, 2);
        Check(decision.GlobalServedDepth == 45 && decision.RequestedDepth == 100.5 && decision.WouldClamp,
            "shared evaluator preserves 45-frame production ceiling and fractional ACK");
        Check(LagCompensationPolicy.RttBucket(199) == 3 && LagCompensationPolicy.RttBucket(200) == 4
            && LagCompensationPolicy.RttBucket(400) == 7 && LagCompensationPolicy.JitterBucket(80) == 4,
            "study bucket boundaries");
    }
    private sealed class FailedWriteStream : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("disk full");
        public override void Write(byte[] b, int o, int n) => throw new IOException("disk full");
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
    }
    private static void UploadFailure(NetTelemetryConfig config, string summary, bool slow)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = new Thread(() =>
        {
            try
            {
                using var socket = listener.AcceptTcpClient();
                using var stream = socket.GetStream();
                // This fixture only waits for request bytes before sending a
                // failure response; it deliberately does not decode the body.
#pragma warning disable CA2022
                byte[] buffer = new byte[8192]; stream.Read(buffer, 0, buffer.Length);
#pragma warning restore CA2022
                if (slow) Thread.Sleep(1600);
                byte[] response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 500 Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                stream.Write(response);
            }
            catch (Exception) { }
        }) { IsBackground = true }; server.Start();
        int failures = 0;
        NetTelemetryUpload.Run(config with { Endpoint = $"http://127.0.0.1:{port}/api/net-telemetry/v1/matches", UploadTimeoutSeconds = 1 }, summary, () => failures++);
        listener.Stop(); server.Join(2500);
        Check(failures == 1 && Directory.GetFiles(Path.Combine(config.Directory, "pending"), "*.retry").Length == 1,
            slow ? "slow HTTP timeout retains retry without simulation work" : "HTTP 500 retains local summary with exponential retry");
    }
    private static TelemetryHeader Header() => new(1, 19, Guid.NewGuid().ToString("N"), "test", "test", "test", "Battle", "test-room", 8);
    private static void Pipeline(string root)
    {
        var config = new NetTelemetryConfig { Directory = root, Detail = TelemetryDetail.Study, QueueCapacity = 8192 };
        using var gate = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        var writer = new NetTelemetryWriter(config, Header(), _ => { entered.Set(); gate.Wait(); return Stream.Null; });
        Check(entered.Wait(3000), "writer starts off simulation thread");
        var sample = new NetTelemetryEvent(TelemetryEventType.ServerStep, 1, A: 1.5);
        writer.Emit(sample); // warm channel paths before allocation measurement
        long before = GC.GetAllocatedBytesForCurrentThread(); var clock = Stopwatch.StartNew();
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20000; i++) writer.Emit(sample);
        long allocations = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Check(allocations == 0, $"Emit zero allocations with blocked writer ({allocations} bytes)");
        Check(writer.Counters.EventsQueued == 8192 && writer.Counters.EventsDropped == 11809 && writer.Counters.QueueHighWater <= 8192, "queue pressure drops telemetry at fixed capacity");
        clock.Restart(); writer.Stop(); Check(clock.ElapsedMilliseconds < 50, "shutdown request never waits on writer");
        gate.Set(); Check(writer.WaitForExit(5000) && writer.Counters.EventsWritten == 8192, "match end drains pending queue before summary");
        foreach (string scenario in new[] { "unavailable", "full", "write-exception" })
        {
            var failed = new NetTelemetryWriter(config with { Directory = Path.Combine(root, scenario) }, Header(), _ => throw new IOException(scenario));
            for (int i = 0; i < 10; i++) failed.Emit(sample);
            failed.Stop(); Check(failed.WaitForExit(5000) && failed.Counters.WriterFailures > 0 && failed.Counters.EventsWritten == 10, "storage " + scenario + " preserves event consumption");
        }
        var writeFailure = new NetTelemetryWriter(config with { Directory = Path.Combine(root, "write-failure") }, Header(), _ => new FailedWriteStream());
        writeFailure.Emit(sample); writeFailure.Stop();
        Check(writeFailure.WaitForExit(5000) && writeFailure.Counters.WriterFailures > 0 && writeFailure.Counters.EventsWritten == 1, "write/flush disk-full exception is isolated");
        string file = Path.Combine(root, "not-a-directory"); File.WriteAllText(file, "x");
        var malformed = new NetTelemetryWriter(config with { Directory = file }, Header()); malformed.Emit(sample); malformed.Stop();
        Check(malformed.WaitForExit(5000) && malformed.Counters.WriterFailures > 0, "malformed directory cannot escape worker");
        string output = Path.Combine(root, "real");
        var real = new NetTelemetryWriter(config with { Directory = output }, Header()); real.Emit(sample); real.Stop();
        Check(real.WaitForExit(5000), "real gzip writer completes");
        using var gzip = new GZipStream(File.OpenRead(Directory.GetFiles(output, "*.gz", SearchOption.AllDirectories).Single()), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip); string raw = reader.ReadToEnd();
        Check(raw.Contains("protocol\":19") && raw.Contains("ServerStep") && !raw.Contains("127.0.0.1"), "versioned compressed header/events and privacy-safe schema");
        string summary = Directory.GetFiles(output, "*.summary.json", SearchOption.AllDirectories).Single();
        UploadFailure(config with { Directory = Path.Combine(root, "http500") }, summary, false);
        UploadFailure(config with { Directory = Path.Combine(root, "http-slow") }, summary, true);
        int failures = 0;
        NetTelemetryUpload.Run(config with { Upload = true, Endpoint = "http://127.0.0.1:1/api/net-telemetry/v1/matches", UploadTimeoutSeconds = 1 }, summary, () => failures++);
        Check(failures > 0 && Directory.GetFiles(Path.Combine(root, "pending"), "*.retry").Length == 1, "offline upload retains bounded batch and retry deadline");
    }
}
