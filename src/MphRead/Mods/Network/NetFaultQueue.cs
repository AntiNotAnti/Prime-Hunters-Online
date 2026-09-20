using System;
using System.Collections.Generic;

namespace MphRead.Mods.Network
{
    /// <summary>A bounded, seeded datagram scheduler. Time is supplied by the caller so tests need no sleeps.</summary>
    public sealed class NetFaultQueue<T>
    {
        private readonly PriorityQueue<T, (double Due, long Order)> _queue = new();
        private readonly Random _random;
        private readonly double _delay, _jitter, _loss, _reorder, _duplicate;
        private readonly int _capacity;
        private long _order;
        private double _lastDue;
        public int Count => _queue.Count;
        public long Dropped { get; private set; }
        public long Duplicated { get; private set; }
        public long Reordered { get; private set; }

        public NetFaultQueue(int seed, double delayMs, double jitterMs, double loss,
            double reorder, double duplicate, int capacity = 2048)
        {
            if (!double.IsFinite(delayMs) || !double.IsFinite(jitterMs) || delayMs < 0 || jitterMs < 0
                || !Probability(loss) || !Probability(reorder) || !Probability(duplicate) || capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(delayMs));
            _random = new Random(seed);
            _delay = delayMs; _jitter = jitterMs; _loss = loss;
            _reorder = reorder; _duplicate = duplicate; _capacity = capacity;
        }

        private static bool Probability(double value) => double.IsFinite(value) && value >= 0 && value <= 1;

        public void Enqueue(double nowMs, T value, double extraDelayMs = 0)
        {
            if (_random.NextDouble() < _loss) { Dropped++; return; }
            // Jitter alone retains the old FIFO contract. Explicit reordering
            // delays one datagram without holding the following datagrams back.
            double due = Math.Max(_lastDue, nowMs + _delay + _random.NextDouble() * _jitter + extraDelayMs);
            _lastDue = due;
            if (_random.NextDouble() < _reorder)
            {
                due += Math.Max(20, _jitter + _delay) * (1 + _random.NextDouble());
                Reordered++;
            }
            Add(value, due);
            if (_random.NextDouble() < _duplicate)
            {
                Add(value, due + 1 + _random.NextDouble() * Math.Max(1, _jitter));
                Duplicated++;
            }
        }

        private void Add(T value, double due)
        {
            if (_queue.Count >= _capacity) { Dropped++; return; }
            _queue.Enqueue(value, (due, _order++));
        }

        public bool TryDequeue(double nowMs, out T value)
        {
            if (_queue.TryPeek(out _, out var priority) && priority.Due <= nowMs)
            {
                value = _queue.Dequeue();
                return true;
            }
            value = default!;
            return false;
        }
    }
}
