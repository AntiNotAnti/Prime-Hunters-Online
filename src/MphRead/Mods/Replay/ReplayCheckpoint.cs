using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MphRead.Entities;
using MphRead.Mods.Network;

namespace MphRead.Mods.Replay
{
    /// <summary>
    /// In-memory world checkpoints used by the replay editor. The cache is deliberately
    /// conservative: it only restores into the same scene/entity graph and validates
    /// against hashes captured during the original linear pass. Any disagreement marks
    /// that checkpoint unusable and asks ReplayController for the normal frame-zero
    /// reconstruction instead.
    /// </summary>
    internal static class ReplayCheckpointManager
    {
        private const uint IntervalFrames = 10 * 60;
        private const int MaxCheckpoints = 96;
        private const int MaxHistoryFrames = 60 * 60 * 60; // one hour at 60 Hz

        private static readonly List<Checkpoint> Checkpoints = new();
        private static readonly Dictionary<uint, string> History = new();
        private static readonly HashSet<uint> Bad = new();
        private static string? _path;
        private static bool _restoring;
        private static uint _restoreTarget;
        private static bool _restoreResume;
        private static uint _activeCheckpoint;

        public static int Count => Checkpoints.Count;
        public static bool Restoring => _restoring;

        public static void NoteReplay(string? path)
        {
            if (string.Equals(_path, path, StringComparison.Ordinal)) return;
            _path = path;
            Checkpoints.Clear();
            History.Clear();
            Bad.Clear();
            _restoring = false;
            _activeCheckpoint = 0;
        }

        public static void AfterFrame(Scene scene)
        {
            if (!DemoPlayback.IsActive || DemoPlayback.CurrentPath == null) return;
            NoteReplay(DemoPlayback.CurrentPath);

            uint frame = DemoPlayback.CurrentFrame;
            string actual = ReplayStateHash.Compute(scene);

            if (_restoring && History.TryGetValue(frame, out string? expected)
                && !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                Bad.Add(_activeCheckpoint);
                _restoring = false;
                Console.WriteLine($"[replay] checkpoint {_activeCheckpoint} diverged at frame {frame}; "
                    + "falling back to deterministic reconstruction");
                ReplayController.RequestFullRebuild(_restoreTarget, _restoreResume);
                return;
            }

            if (!History.ContainsKey(frame) && History.Count < MaxHistoryFrames)
                History.Add(frame, actual);

            if (_restoring && frame >= _restoreTarget)
                _restoring = false;

            if (!_restoring && frame > 0 && frame % IntervalFrames == 0
                && scene.MessageQueue.Count == 0
                && !Checkpoints.Any(c => c.Frame == frame))
            {
                try
                {
                    Checkpoints.Add(Checkpoint.Capture(scene, frame, actual));
                    if (Checkpoints.Count > MaxCheckpoints)
                    {
                        Bad.Remove(Checkpoints[0].Frame);
                        Checkpoints.RemoveAt(0);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or TargetException or FieldAccessException or ArgumentException)
                {
                    Console.WriteLine($"[replay] checkpoint {frame} was skipped: {ex.Message}");
                }
            }
        }

        public static bool TryRestore(Scene scene, uint target, bool resume, out uint restoredFrame)
        {
            restoredFrame = 0;
            if (!DemoPlayback.IsActive || DemoPlayback.CurrentPath == null || target >= DemoPlayback.CurrentFrame)
                return false;
            NoteReplay(DemoPlayback.CurrentPath);

            var membershipList = new List<EntityBase>();
            foreach (EntityBase entity in scene.Entities) membershipList.Add(entity);
            EntityBase[] membership = membershipList.ToArray();
            foreach (Checkpoint checkpoint in Checkpoints
                .Where(c => c.Frame <= target && !Bad.Contains(c.Frame))
                .OrderByDescending(c => c.Frame))
            {
                if (!checkpoint.SameMembership(membership))
                    continue;

                if (!DemoPlayback.Reposition(checkpoint.Frame, checkpoint.NetFrame))
                {
                    Bad.Add(checkpoint.Frame);
                    continue;
                }

                try
                {
                    checkpoint.Restore(scene);
                }
                catch (Exception ex) when (ex is TargetException or FieldAccessException
                    or ArgumentException or InvalidOperationException)
                {
                    Bad.Add(checkpoint.Frame);
                    Console.WriteLine($"[replay] checkpoint {checkpoint.Frame} could not restore: {ex.Message}");
                    continue;
                }

                string actual = ReplayStateHash.Compute(scene);
                if (!string.Equals(actual, checkpoint.Hash, StringComparison.Ordinal))
                {
                    Bad.Add(checkpoint.Frame);
                    Console.WriteLine($"[replay] checkpoint {checkpoint.Frame} failed immediate validation");
                    continue;
                }

                ReplayVerification.SeekTo(checkpoint.Frame);
                _restoring = true;
                _restoreTarget = target;
                _restoreResume = resume;
                _activeCheckpoint = checkpoint.Frame;
                restoredFrame = checkpoint.Frame;
                return true;
            }
            return false;
        }

        private sealed class Checkpoint
        {
            public uint Frame { get; init; }
            public uint NetFrame { get; init; }
            public uint Rng1 { get; init; }
            public uint Rng2 { get; init; }
            public string Hash { get; init; } = "";
            public EntityBase[] Membership { get; init; } = Array.Empty<EntityBase>();
            public ObjectSnapshot Scene { get; init; } = null!;
            public ObjectSnapshot GameState { get; init; } = null!;
            public ObjectSnapshot SpinningState { get; init; } = null!;
            public ObjectSnapshot PlayerStaticState { get; init; } = null!;
            public ObjectSnapshot[] Entities { get; init; } = Array.Empty<ObjectSnapshot>();

            public static Checkpoint Capture(Scene scene, uint frame, string hash)
            {
                var entityList = new List<EntityBase>();
                foreach (EntityBase entity in scene.Entities) entityList.Add(entity);
                EntityBase[] entities = entityList.ToArray();
                return new Checkpoint
                {
                    Frame = frame,
                    NetFrame = NetSession.NetFrame,
                    Rng1 = Rng.Rng1,
                    Rng2 = Rng.Rng2,
                    Hash = hash,
                    Membership = entities,
                    Scene = ObjectSnapshot.Capture(scene),
                    GameState = ObjectSnapshot.CaptureStatic(typeof(GameState)),
                    SpinningState = ObjectSnapshot.CaptureStatic(typeof(SpinningEntityBase)),
                    PlayerStaticState = ObjectSnapshot.CaptureStatic(typeof(PlayerEntity)),
                    Entities = entities.Select(ObjectSnapshot.Capture).ToArray()
                };
            }

            public bool SameMembership(EntityBase[] current)
            {
                if (Membership.Length != current.Length) return false;
                for (int i = 0; i < current.Length; i++)
                    if (!ReferenceEquals(Membership[i], current[i])) return false;
                return true;
            }

            public void Restore(Scene scene)
            {
                Scene.Restore(scene);
                GameState.Restore(null);
                SpinningState.Restore(null);
                PlayerStaticState.Restore(null);
                for (int i = 0; i < Entities.Length; i++)
                    Entities[i].Restore(Membership[i]);
                Rng.SetRng1(Rng1);
                Rng.SetRng2(Rng2);
            }
        }

        private sealed class ObjectSnapshot
        {
            private readonly Type _type;
            private readonly FieldValue[] _fields;

            private ObjectSnapshot(Type type, FieldValue[] fields)
            {
                _type = type;
                _fields = fields;
            }

            public static ObjectSnapshot Capture(object target)
                => new(target.GetType(), CaptureFields(target, target.GetType(), statics: false));

            public static ObjectSnapshot CaptureStatic(Type type)
                => new(type, CaptureFields(null, type, statics: true));

            public void Restore(object? target)
            {
                foreach (FieldValue item in _fields)
                {
                    if (item.Value is Array saved)
                    {
                        Array? current = item.Field.GetValue(target) as Array;
                        if (current != null && current.Rank == saved.Rank && current.Length == saved.Length)
                        {
                            Array.Copy(saved, current, saved.Length);
                        }
                        else if (!item.Field.IsInitOnly)
                        {
                            item.Field.SetValue(target, saved.Clone());
                        }
                    }
                    else if (!item.Field.IsInitOnly)
                    {
                        item.Field.SetValue(target, item.Value);
                    }
                }
            }

            private static FieldValue[] CaptureFields(object? target, Type type, bool statics)
            {
                var result = new List<FieldValue>();
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                    | (statics ? BindingFlags.Static : BindingFlags.Instance)
                    | BindingFlags.DeclaredOnly;

                for (Type? current = type; current != null && current != typeof(object);
                    current = statics ? null : current.BaseType)
                {
                    foreach (FieldInfo field in current.GetFields(flags))
                    {
                        if (field.IsLiteral) continue;
                        object? value = field.GetValue(target);
                        if (value is Array array)
                        {
                            Type? element = field.FieldType.GetElementType();
                            if (element != null && (element.IsValueType || element == typeof(string)))
                                result.Add(new FieldValue(field, array.Clone()));
                            continue;
                        }
                        if (value == null)
                        {
                            if (!field.FieldType.IsValueType && field.FieldType != typeof(string))
                                continue;
                            result.Add(new FieldValue(field, null));
                            continue;
                        }
                        if (field.FieldType.IsValueType || field.FieldType == typeof(string))
                            result.Add(new FieldValue(field, value));
                    }
                }
                return result.ToArray();
            }

            private readonly record struct FieldValue(FieldInfo Field, object? Value);
        }
    }
}
