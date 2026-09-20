using System;
using System.Collections.Generic;
using System.Diagnostics;
using MphRead.Mods;
using MphRead.Mods.Render;
using MphRead.Sound;
using OpenTK.Mathematics;

namespace MphRead.Droid
{
    /// <summary>
    /// Rendering map previews, given a current GL context.
    ///
    /// The picture comes from the game's own intro camera and is read out of
    /// the scene's offscreen target by <see cref="ScreenCapture"/> -- the same
    /// code and the same frame the desktop captures. **Nothing is shipped**:
    /// every preview is rendered on the device from the player's own extracted
    /// files, into <see cref="ThumbnailGenerator.CacheDirectory"/> beside them.
    ///
    /// One room at a time, because a scene is not a local thing: the entity
    /// lists, the player roster and the game state are static, so two scenes
    /// in one process would be one world with two cameras. Several at once is
    /// what <see cref="PreviewWorkers"/> is for -- several *processes*, which
    /// is also how the desktop does it.
    /// </summary>
    internal static class PreviewRun
    {
        /// <summary>
        /// Preview size on a phone.
        ///
        /// The desktop renders 1600x900 because it has a 1080p window to do it
        /// in and a launcher that may be shown on a large display. A phone has
        /// neither, and every pixel here is paid for four times over: fill rate
        /// on a tile-based GPU, a glReadPixels stall, a managed pixel loop in
        /// <see cref="AndroidPng"/>, and a PNG encode. The launcher shows these
        /// in a 248-point band, so 640x360 is already more than it can use, and
        /// it is a seventh of the work.
        /// </summary>
        public const int Width = 640;
        public const int Height = 360;

        // The intro sequence has to start and the match-start fade clear
        // before the frame is worth keeping. The desktop's number.
        private const int SettleFrames = 12;

        /// <summary>
        /// Render each room in turn and write what it can. Returns how many
        /// pictures were written. Must run on the thread that owns the context.
        /// </summary>
        public static int Render(IReadOnlyList<string> rooms, int width, int height,
            Action<string> report, Func<bool>? cancelled = null)
        {
            EsBindings.Load();
            GlEs.Reset();
            // Suppresses the HUD and mutes the sound: a preview is a picture of
            // a room, not of a match in progress.
            ThumbnailMode.Enter();
            var input = new AndroidInput();
            int written = 0;
            try
            {
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (cancelled?.Invoke() == true)
                    {
                        break;
                    }
                    string room = rooms[i];
                    var clock = Stopwatch.StartNew();
                    bool saved = RenderOne(room, input, width, height, report);
                    if (saved)
                    {
                        written++;
                    }
                    report($"[thumbnails] {i + 1}/{rooms.Count}  {room}"
                        + (saved ? $"  {clock.Elapsed.TotalSeconds:0.0}s" : "  -- nothing usable"));
                }
            }
            finally
            {
                // The flag is process-wide and the game runs in this same
                // process on Android, so leaving it set is a match with no HUD
                // and no sound afterwards.
                ThumbnailMode.Exit();
            }
            return written;
        }

        private static bool RenderOne(string room, AndroidInput input, int width, int height,
            Action<string> report)
        {
            Scene? scene = null;
            try
            {
                // A player has to exist for the multiplayer intro camera to run
                // at all: GameState sets the sequence up against
                // PlayerEntity.Main's camera info.
                scene = new Scene(new Vector2i(width, height),
                    input.Keyboard, input.Mouse, _ => { }, () => { });
                scene.AddPlayer(Hunter.Samus, recolor: 0, team: -1);
                scene.AddRoom(room, GameMode.Battle, playerCount: 1);
                scene.OnLoad();
                Silence(report);
                GL.Viewport(0, 0, width, height);
                scene.OnResize();
                // Only the last frame is drawn. The intro camera advances on
                // the update, not on the draw, and the update clears and
                // rebuilds the render lists either way -- so the frames that
                // exist only to let the camera reach its mark cost the
                // simulation and nothing else. Measured on a phone: the whole
                // settle went from most of the room's time to 15 ms.
                for (int frame = 0; frame < SettleFrames; frame++)
                {
                    GameState.ApplyPause();
                    scene.OnUpdateFrame();
                }
                GameState.ApplyPause();
                scene.OnUpdateFrame();
                if (!scene.OnRenderFrame())
                {
                    return false;
                }
                scene.AfterRenderFrame();
                return ScreenCapture.Save(scene, ThumbnailGenerator.PathFor(room));
            }
            catch (Exception ex)
            {
                // The whole exception, not just the message: a preview run that
                // fails on every room fails for one reason, and the message
                // alone rarely says which.
                Console.WriteLine($"[thumbnails] {room} failed: {ex}");
                report($"[thumbnails] {room}: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    scene?.DoCleanup();
                }
                catch (Exception ex)
                {
                    report($"[thumbnails] {room}: cleanup failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// A preview needs no sound, and loading a room starts its music.
        /// Thirty-three rooms is thirty-three streams begun and abandoned,
        /// which is what took the audio thread down with a SIGSEGV.
        /// </summary>
        private static void Silence(Action<string> report)
        {
            try
            {
                Music.Stop();
                MusicPlayer.Stop();
            }
            catch (Exception ex)
            {
                report($"[thumbnails] could not stop the music: {ex.Message}");
            }
        }
    }
}
