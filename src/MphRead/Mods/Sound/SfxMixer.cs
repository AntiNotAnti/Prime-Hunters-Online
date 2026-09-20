#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MphRead.Sound;
using OpenTK.Audio.OpenAL;
using OpenTK.Mathematics;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace MphRead.Mods.Sound
{
    /// <summary>
    /// What OpenAL was doing, done here: buffers, voices, and one block of
    /// stereo at a time.
    ///
    /// It is a component on the music's own playback device rather than a
    /// second device -- see <see cref="MusicPlayer.PlaybackDevice"/> -- and it
    /// hands SoundFlow an endless stream that mixes whatever is playing at the
    /// moment it is asked. The game writes to the voices from its own thread
    /// and the audio callback reads them from another, so everything below
    /// takes the lock; a block is a few hundred frames of arithmetic over at
    /// most a dozen voices, which is short enough to hold it for.
    /// </summary>
    internal static class SfxMixer
    {
        private sealed class Buffer
        {
            public float[] Data = Array.Empty<float>();
            public int Channels = 1;
            public int Frames;
            public int SampleRate = 22050;
            public int LoopStart = -1;
            public int LoopEnd = -1;
        }

        private sealed class Voice
        {
            public readonly List<int> Queue = new List<int>();
            public int Index;
            public int Processed;
            public double Cursor;
            public ALSourceState State = ALSourceState.Initial;
            public float Gain = 1;
            public float Pitch = 1;
            public bool Looping;
            public bool Relative;
            public Vector3 Position;
            public float ReferenceDistance = 1;
            public float MaxDistance = Single.MaxValue;
            public float RolloffFactor = 1;

            public void Reset()
            {
                Queue.Clear();
                Index = 0;
                Processed = 0;
                Cursor = 0;
                State = ALSourceState.Initial;
            }
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<int, Buffer> _buffers = new Dictionary<int, Buffer>();
        private static readonly Dictionary<int, Voice> _voices = new Dictionary<int, Voice>();
        private static int _nextBuffer = 1;
        private static int _nextSource = 1;

        private static Vector3 _listenerPosition;
        private static Vector3 _listenerFacing = -Vector3.UnitZ;
        private static Vector3 _listenerUp = Vector3.UnitY;

        private static MixStream? _stream;
        private static RawDataProvider? _provider;
        private static SoundPlayer? _player;
        private static bool _unavailable;
        private static int _outputRate = 32728;

        // ------------------------------------------------------------ device

        /// <summary>
        /// Attach to the music's output. False means there is no audio device
        /// at all on this machine, which is what makes <c>Sfx.Load</c> install
        /// its silent stub -- the same answer a desktop with no sound card
        /// gives.
        /// </summary>
        public static bool Open()
        {
            if (_unavailable)
            {
                return false;
            }
            AudioPlaybackDevice? device = MusicPlayer.PlaybackDevice;
            MiniAudioEngine? engine = MusicPlayer.Engine;
            if (device == null || engine == null)
            {
                _unavailable = true;
                return false;
            }
            try
            {
                lock (_lock)
                {
                    if (_player == null)
                    {
                        AudioFormat format = MusicPlayer.Format;
                        _outputRate = format.SampleRate;
                        _stream = new MixStream();
                        _provider = new RawDataProvider(_stream, SampleFormat.F32, format.SampleRate);
                        _player = new SoundPlayer(engine, format, _provider);
                        device.MasterMixer.AddComponent(_player);
                    }
                }
                // Both every time: Sfx.ShutDown stops the device through
                // MusicPlayer.Remove, and the next match asks for a device
                // again rather than for a new mixer.
                device.Start();
                _player.Play();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sound] the SFX mixer could not start ({ex.Message}); continuing without SFX");
                _unavailable = true;
                return false;
            }
        }

        /// <summary>
        /// Silence, without tearing anything down: <c>Sfx.CheckAudioLoad</c>
        /// opens and closes a device just to ask a question, and rebuilding the
        /// mixer for that would drop the music's output component with it.
        /// </summary>
        public static void Close()
        {
            lock (_lock)
            {
                foreach (KeyValuePair<int, Voice> entry in _voices)
                {
                    entry.Value.State = ALSourceState.Stopped;
                }
            }
        }

        // ----------------------------------------------------------- objects

        public static int NewBuffer()
        {
            lock (_lock)
            {
                int id = _nextBuffer++;
                _buffers[id] = new Buffer();
                return id;
            }
        }

        public static void DeleteBuffer(int id)
        {
            lock (_lock)
            {
                _buffers.Remove(id);
            }
        }

        public static int NewSource()
        {
            lock (_lock)
            {
                int id = _nextSource++;
                _voices[id] = new Voice();
                return id;
            }
        }

        public static void DeleteSource(int id)
        {
            lock (_lock)
            {
                _voices.Remove(id);
            }
        }

        /// <summary>
        /// Decode a sample into the float frames the mixer sums.
        ///
        /// Outside the lock: the game buffers every one of its two thousand
        /// samples during a room load, and holding the audio thread off for
        /// each would be an audible gap per sample rather than one per load.
        /// </summary>
        public static void FillBuffer(int id, ALFormat format, ReadOnlySpan<byte> data, int sampleRate)
        {
            int channels = format == ALFormat.Stereo8 || format == ALFormat.Stereo16 ? 2 : 1;
            bool sixteen = format == ALFormat.Mono16 || format == ALFormat.Stereo16;
            int frames = sixteen ? data.Length / (2 * channels) : data.Length / channels;
            var samples = new float[frames * channels];
            if (sixteen)
            {
                ReadOnlySpan<short> source = MemoryMarshal.Cast<byte, short>(data);
                for (int i = 0; i < samples.Length && i < source.Length; i++)
                {
                    samples[i] = source[i] / 32768f;
                }
            }
            else
            {
                for (int i = 0; i < samples.Length && i < data.Length; i++)
                {
                    // PCM8 in a wave file is unsigned, with silence at 128.
                    samples[i] = (data[i] - 128) / 128f;
                }
            }
            lock (_lock)
            {
                if (!_buffers.TryGetValue(id, out Buffer? buffer))
                {
                    return;
                }
                buffer.Data = samples;
                buffer.Channels = channels;
                buffer.Frames = frames;
                buffer.SampleRate = sampleRate > 0 ? sampleRate : 22050;
                buffer.LoopStart = -1;
                buffer.LoopEnd = -1;
            }
        }

        public static void SetLoopPoints(int id, int start, int end)
        {
            lock (_lock)
            {
                if (_buffers.TryGetValue(id, out Buffer? buffer))
                {
                    buffer.LoopStart = start;
                    buffer.LoopEnd = end;
                }
            }
        }

        // ----------------------------------------------------------- sources

        public static void SetSource(int id, ALSourceb param, bool value)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice))
                {
                    return;
                }
                if (param == ALSourceb.Looping)
                {
                    voice.Looping = value;
                }
                else if (param == ALSourceb.SourceRelative)
                {
                    voice.Relative = value;
                }
            }
        }

        public static void SetSource(int id, ALSourcei param, int value)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice) || param != ALSourcei.Buffer)
                {
                    return;
                }
                voice.Reset();
                if (value != 0)
                {
                    voice.Queue.Add(value);
                }
            }
        }

        public static void SetSource(int id, ALSourcef param, float value)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice))
                {
                    return;
                }
                switch (param)
                {
                case ALSourcef.Gain:
                    voice.Gain = Math.Max(0, value);
                    break;
                case ALSourcef.Pitch:
                    // Zero would freeze the voice on one frame for ever.
                    voice.Pitch = Math.Clamp(value, 0.01f, 8f);
                    break;
                case ALSourcef.ReferenceDistance:
                    voice.ReferenceDistance = value;
                    break;
                case ALSourcef.MaxDistance:
                    voice.MaxDistance = value;
                    break;
                case ALSourcef.RolloffFactor:
                    voice.RolloffFactor = value;
                    break;
                }
            }
        }

        public static void SetSource(int id, ALSource3f param, Vector3 value)
        {
            lock (_lock)
            {
                if (_voices.TryGetValue(id, out Voice? voice) && param == ALSource3f.Position)
                {
                    voice.Position = value;
                }
            }
        }

        public static void Play(int id)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice))
                {
                    return;
                }
                if (voice.Queue.Count == 0)
                {
                    voice.State = ALSourceState.Stopped;
                    return;
                }
                if (voice.State != ALSourceState.Paused)
                {
                    voice.Index = 0;
                    voice.Cursor = 0;
                }
                voice.State = ALSourceState.Playing;
            }
        }

        public static void Pause(int id)
        {
            lock (_lock)
            {
                if (_voices.TryGetValue(id, out Voice? voice)
                    && voice.State == ALSourceState.Playing)
                {
                    voice.State = ALSourceState.Paused;
                }
            }
        }

        public static void StopSource(int id)
        {
            lock (_lock)
            {
                if (_voices.TryGetValue(id, out Voice? voice))
                {
                    voice.State = ALSourceState.Stopped;
                    voice.Cursor = 0;
                    voice.Index = 0;
                }
            }
        }

        public static void QueueBuffers(int id, Span<int> buffers)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice))
                {
                    return;
                }
                for (int i = 0; i < buffers.Length; i++)
                {
                    voice.Queue.Add(buffers[i]);
                }
            }
        }

        public static void UnqueueBuffers(int id, Span<int> buffers)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice))
                {
                    return;
                }
                int count = Math.Min(buffers.Length, Math.Min(voice.Processed, voice.Queue.Count));
                for (int i = 0; i < count; i++)
                {
                    buffers[i] = voice.Queue[0];
                    voice.Queue.RemoveAt(0);
                }
                voice.Processed -= count;
                voice.Index = Math.Max(0, voice.Index - count);
            }
        }

        public static int GetSource(int id, ALGetSourcei param)
        {
            lock (_lock)
            {
                if (!_voices.TryGetValue(id, out Voice? voice))
                {
                    return (int)ALSourceState.Stopped;
                }
                switch (param)
                {
                case ALGetSourcei.SourceState:
                    return (int)voice.State;
                case ALGetSourcei.BuffersQueued:
                    return voice.Queue.Count;
                case ALGetSourcei.BuffersProcessed:
                    return voice.Processed;
                case ALGetSourcei.Buffer:
                    return voice.Index < voice.Queue.Count ? voice.Queue[voice.Index] : 0;
                default:
                    return 0;
                }
            }
        }

        public static void SetListener(ALListener3f param, Vector3 value)
        {
            lock (_lock)
            {
                if (param == ALListener3f.Position)
                {
                    _listenerPosition = value;
                }
            }
        }

        public static void SetListenerOrientation(Vector3 facing, Vector3 up)
        {
            lock (_lock)
            {
                _listenerFacing = facing;
                _listenerUp = up;
            }
        }

        // ------------------------------------------------------------- mixing

        /// <summary>
        /// Fill one block of interleaved stereo. Runs on SoundFlow's audio
        /// thread: it must never throw and must never wait on anything but the
        /// lock.
        /// </summary>
        private static void Mix(Span<float> output)
        {
            output.Clear();
            try
            {
                lock (_lock)
                {
                    foreach (KeyValuePair<int, Voice> entry in _voices)
                    {
                        Voice voice = entry.Value;
                        if (voice.State == ALSourceState.Playing)
                        {
                            MixVoice(voice, output);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sound] the SFX mixer faulted: {ex.Message}");
                output.Clear();
                return;
            }
            for (int i = 0; i < output.Length; i++)
            {
                // A dozen voices at the volumes the game asks for will overshoot
                // now and then; clipping quietly beats wrapping loudly.
                output[i] = Math.Clamp(output[i], -1f, 1f);
            }
        }

        private static void MixVoice(Voice voice, Span<float> output)
        {
            if (voice.Index >= voice.Queue.Count
                || !_buffers.TryGetValue(voice.Queue[voice.Index], out Buffer? buffer)
                || buffer.Frames <= 0)
            {
                voice.State = ALSourceState.Stopped;
                return;
            }
            Placement(voice, out float left, out float right);
            double step = buffer.SampleRate / (double)_outputRate * voice.Pitch;
            // Loop points are in frames and may be absent, in which case the
            // whole sample is the loop -- which is what OpenAL does too.
            int loopStart = 0;
            int loopEnd = buffer.Frames;
            if (buffer.LoopEnd > buffer.LoopStart && buffer.LoopStart >= 0)
            {
                loopStart = Math.Min(buffer.LoopStart, buffer.Frames - 1);
                loopEnd = Math.Min(buffer.LoopEnd, buffer.Frames);
            }
            for (int i = 0; i + 1 < output.Length; i += 2)
            {
                if (voice.Cursor >= (voice.Looping ? loopEnd : buffer.Frames))
                {
                    if (voice.Looping && loopEnd > loopStart)
                    {
                        voice.Cursor = loopStart + (voice.Cursor - loopEnd) % (loopEnd - loopStart);
                    }
                    else if (voice.Index + 1 < voice.Queue.Count)
                    {
                        voice.Index++;
                        voice.Processed++;
                        voice.Cursor = 0;
                        if (!_buffers.TryGetValue(voice.Queue[voice.Index], out buffer)
                            || buffer.Frames <= 0)
                        {
                            voice.State = ALSourceState.Stopped;
                            return;
                        }
                        step = buffer.SampleRate / (double)_outputRate * voice.Pitch;
                        loopStart = 0;
                        loopEnd = buffer.Frames;
                    }
                    else
                    {
                        voice.Processed++;
                        voice.Index++;
                        voice.State = ALSourceState.Stopped;
                        return;
                    }
                }
                Sample(buffer, voice.Cursor, out float monoLeft, out float monoRight);
                output[i] += monoLeft * left * voice.Gain;
                output[i + 1] += monoRight * right * voice.Gain;
                voice.Cursor += step;
            }
        }

        private static void Sample(Buffer buffer, double cursor, out float left, out float right)
        {
            int frame = (int)cursor;
            if (frame < 0)
            {
                frame = 0;
            }
            int next = Math.Min(frame + 1, buffer.Frames - 1);
            float blend = (float)(cursor - frame);
            if (buffer.Channels == 2)
            {
                left = Lerp(buffer.Data[frame * 2], buffer.Data[next * 2], blend);
                right = Lerp(buffer.Data[frame * 2 + 1], buffer.Data[next * 2 + 1], blend);
                return;
            }
            left = right = Lerp(buffer.Data[frame], buffer.Data[next], blend);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>
        /// Distance attenuation and stereo placement, the way the engine asked
        /// for them: AL_LINEAR_DISTANCE_CLAMPED, and a pan taken from where the
        /// source is in the listener's own frame.
        /// </summary>
        private static void Placement(Voice voice, out float left, out float right)
        {
            Vector3 relative = voice.Relative ? voice.Position : voice.Position - _listenerPosition;
            float distance = relative.Length;
            float attenuation = 1;
            float span = voice.MaxDistance - voice.ReferenceDistance;
            // The engine parks its listener-relative sounds at ReferenceDistance
            // == MaxDistance == float.MaxValue, which is its way of saying "do
            // not attenuate this"; the subtraction is then zero or infinity.
            if (voice.RolloffFactor > 0 && span > 0 && !Single.IsInfinity(span)
                && voice.MaxDistance < Single.MaxValue)
            {
                float clamped = Math.Clamp(distance, voice.ReferenceDistance, voice.MaxDistance);
                attenuation = Math.Clamp(
                    1 - voice.RolloffFactor * (clamped - voice.ReferenceDistance) / span, 0, 1);
            }
            float pan = 0;
            if (distance > 0.0001f)
            {
                Vector3 facing = voice.Relative ? -Vector3.UnitZ : _listenerFacing;
                Vector3 up = voice.Relative ? Vector3.UnitY : _listenerUp;
                Vector3 side = Vector3.Cross(facing, up);
                if (side.LengthSquared > 0.0001f)
                {
                    pan = Math.Clamp(Vector3.Dot(relative / distance, side.Normalized()), -1, 1);
                }
            }
            // Equal power, scaled so a centred source is at full gain in both
            // ears rather than at -3 dB.
            left = Math.Min(1f, MathF.Sqrt(0.5f * (1 - pan)) * 1.41421356f) * attenuation;
            right = Math.Min(1f, MathF.Sqrt(0.5f * (1 + pan)) * 1.41421356f) * attenuation;
        }

        /// <summary>
        /// The endless stream SoundFlow pulls from. It never ends and never
        /// blocks: a block with nothing playing is a block of silence.
        /// </summary>
        private sealed class MixStream : Stream
        {
            private long _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => Int64.MaxValue;

            public override long Position
            {
                get => _position;
                set { }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // Whole stereo frames only: eight bytes of two floats.
                int bytes = count / 8 * 8;
                if (bytes <= 0)
                {
                    return 0;
                }
                Mix(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, bytes)));
                _position += bytes;
                return bytes;
            }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => _position;

            public override void SetLength(long value) { }

            public override void Write(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();
        }
    }
}
#endif
