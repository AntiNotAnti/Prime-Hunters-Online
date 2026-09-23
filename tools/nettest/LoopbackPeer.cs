using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MphRead.Mods.Network;

namespace MphRead.NetTest;

// Blocking test facade over the production transport. Tests continue inspecting
// application packets; envelope/admission/ACK handling is never reimplemented here.
internal sealed class LoopbackPeer : IDisposable
{
    public NetTransport Transport { get; }
    private readonly Queue<(byte[] Bytes, IPEndPoint From)> _packets = new();
    public LoopbackPeer(int port = 0) { Transport = new(port); Transport.AnswerPingsImmediately(); }
    public LoopbackPeer Client => this;
    public int ReceiveTimeout { get; set; } = 2000;
    public int Available { get { Pump(); return _packets.Count; } }
    private void Pump()
    {
        foreach (var packet in Transport.Drain()) _packets.Enqueue((packet.Data.AsSpan(0, packet.Length).ToArray(), packet.Sender));
    }
    public void Send(byte[] bytes, int length, IPEndPoint target) => Send(bytes.AsSpan(0, length), target);
    public void Send(ReadOnlySpan<byte> bytes, IPEndPoint target) => Transport.Send(target, (PacketType)bytes[0], bytes[1..]);
    public byte[] Receive(ref IPEndPoint from)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            Pump();
            if (_packets.TryDequeue(out var packet)) { from = packet.From; return packet.Bytes; }
            Thread.Sleep(1);
        } while (clock.ElapsedMilliseconds < ReceiveTimeout);
        throw new SocketException((int)SocketError.TimedOut);
    }
    public void Dispose() => Transport.Dispose();
}
