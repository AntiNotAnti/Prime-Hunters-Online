#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;
using OpenTK.Mathematics;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace MphRead.Mods.Sound
{
    /// <summary>
    /// The sound effects, on a phone.
    ///
    /// Everything the engine plays that is not music goes through OpenAL, and
    /// there is no OpenAL on Android: OpenTK ships the bindings only, and the
    /// package the desktop gets the native from (Silk.NET.OpenAL.Soft.Native)
    /// has runtimes for Windows, Linux and macOS and none for a phone. So
    /// <c>Sfx.Load</c> caught the DllNotFoundException, installed the silent
    /// stub, and the game played its music with nothing else -- exactly the
    /// split a Windows machine with no oalinst.exe used to show.
    ///
    /// The answer is the one the renderer already uses: point the *name*
    /// somewhere else. Two using aliases in the Android csproj send <c>AL</c>
    /// and <c>ALC</c> here instead of to OpenTK, for every file in that
    /// compilation, and not one of the fifty-odd call sites in
    /// <c>Sound/Sfx.cs</c> and <c>Formats/Movie.cs</c> changes. The desktop
    /// build never sees any of this.
    ///
    /// What is behind the name is miniaudio, which is in the APK already --
    /// SoundFlow ships it for android-arm64 and android-x64 and it is what
    /// plays the music. The mixing OpenAL was doing is done here instead:
    /// <see cref="SfxMixer"/> is one more component on the music's own output,
    /// pulling from every playing voice each block and summing them.
    ///
    /// What is emulated, because the engine asks for it: per-source gain and
    /// pitch, looping with SOFT loop points, buffer queues (the movie player's
    /// streaming), the linear-clamped distance model, and stereo placement
    /// from the listener's own orientation. What is not: HRTF, doppler,
    /// velocity, and effects -- none of which the engine ever sets.
    /// </summary>
    internal static class AlEs
    {
        public static void GenBuffers(Span<int> buffers)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = SfxMixer.NewBuffer();
            }
        }

        public static int GenBuffer() => SfxMixer.NewBuffer();

        public static void DeleteBuffers(Span<int> buffers)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                SfxMixer.DeleteBuffer(buffers[i]);
            }
        }

        public static void DeleteBuffer(int buffer) => SfxMixer.DeleteBuffer(buffer);

        public static void GenSources(Span<int> sources)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = SfxMixer.NewSource();
            }
        }

        public static int GenSource() => SfxMixer.NewSource();

        public static void DeleteSources(Span<int> sources)
        {
            for (int i = 0; i < sources.Length; i++)
            {
                SfxMixer.DeleteSource(sources[i]);
            }
        }

        public static void DeleteSource(int source) => SfxMixer.DeleteSource(source);

        public static void BufferData<T>(int buffer, ALFormat format, T[] data, int sampleRate)
            where T : unmanaged
        {
            SfxMixer.FillBuffer(buffer, format, MemoryMarshal.AsBytes(data.AsSpan()), sampleRate);
        }

        public static void BufferData<T>(int buffer, ALFormat format, ReadOnlySpan<T> data,
            int sampleRate) where T : unmanaged
        {
            SfxMixer.FillBuffer(buffer, format, MemoryMarshal.AsBytes(data), sampleRate);
        }

        public static void Source(int source, ALSourceb param, bool value)
            => SfxMixer.SetSource(source, param, value);

        public static void Source(int source, ALSourcei param, int value)
            => SfxMixer.SetSource(source, param, value);

        public static void Source(int source, ALSourcef param, float value)
            => SfxMixer.SetSource(source, param, value);

        public static void Source(int source, ALSource3f param, ref Vector3 value)
            => SfxMixer.SetSource(source, param, value);

        public static void Source(int source, ALSource3f param, float x, float y, float z)
            => SfxMixer.SetSource(source, param, new Vector3(x, y, z));

        public static void SourcePlay(int source) => SfxMixer.Play(source);

        public static void SourceStop(int source) => SfxMixer.StopSource(source);

        public static void SourcePause(int source) => SfxMixer.Pause(source);

        public static void SourceQueueBuffers(int source, Span<int> buffers)
            => SfxMixer.QueueBuffers(source, buffers);

        public static void SourceUnqueueBuffers(int source, Span<int> buffers)
            => SfxMixer.UnqueueBuffers(source, buffers);

        public static void GetSource(int source, ALGetSourcei param, out int value)
            => value = SfxMixer.GetSource(source, param);

        public static int GetSource(int source, ALGetSourcei param)
            => SfxMixer.GetSource(source, param);

        public static void Listener(ALListener3f param, ref Vector3 value)
            => SfxMixer.SetListener(param, value);

        public static void Listener(ALListenerfv param, ref Vector3 at, ref Vector3 up)
            => SfxMixer.SetListenerOrientation(at, up);

        public static void DistanceModel(ALDistanceModel model)
        {
            // The engine asks for LinearDistanceClamped and nothing else, and
            // that is the one this mixes with.
        }

        public static ALError GetError() => ALError.NoError;

        /// <summary>
        /// AL_SOFT_loop_points, which this implements rather than advertises
        /// away: the engine falls back to looping the whole sample without it,
        /// and a looped sample with a lead-in then repeats its lead-in.
        /// </summary>
        public static class LoopPoints
        {
            public static bool IsExtensionPresent() => true;

            public static void Buffer(int buffer, BufferLoopPoint param, int start, int end)
                => SfxMixer.SetLoopPoints(buffer, start, end);
        }
    }

    /// <summary>
    /// The device and context half. There is one output on a phone and
    /// SoundFlow owns it, so these are bookkeeping: a handle the engine can
    /// hold, and an open that says whether there is anything to play through.
    /// </summary>
    internal static class AlcEs
    {
        private static readonly IntPtr _handle = new IntPtr(1);

        public static ALDevice OpenDevice(string? deviceName)
        {
            if (!SfxMixer.Open())
            {
                // What the engine already knows how to handle: Sfx.Load falls
                // back to its silent stub and CheckAudioLoad answers None. It
                // does not check whether OpenDevice returned null, so returning
                // one would leave it buffering two thousand samples into a
                // mixer with nothing behind it.
                throw new DllNotFoundException("no audio device on this machine");
            }
            return new ALDevice(_handle);
        }

        public static ALContext CreateContext(ALDevice device, ALContextAttributes attributes)
        {
            return device.Handle == IntPtr.Zero ? ALContext.Null : new ALContext(_handle);
        }

        public static bool MakeContextCurrent(ALContext context) => true;

        public static bool DestroyContext(ALContext context) => true;

        public static bool CloseDevice(ALDevice device)
        {
            SfxMixer.Close();
            return true;
        }

        public static AlcError GetError(ALDevice device) => AlcError.NoError;
    }
}
#endif
