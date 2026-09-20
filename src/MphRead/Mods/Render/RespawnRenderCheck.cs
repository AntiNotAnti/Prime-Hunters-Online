using System;
#if !ANDROID
using System.Diagnostics;
using System.IO;
using MphRead.Entities;
using MphRead.Formats;
using MphRead.Mods.Network;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using ReFuel.Stb;
#endif

namespace MphRead.Mods.Render
{
    /// <summary>A bounded, real-GL death/respawn check, one room per process.</summary>
    public static class RespawnRenderCheck
    {
        public static int Run(string? room, string? cyclesText, string? timeoutText)
        {
#if ANDROID
            Console.WriteLine("RESPAWNRENDER unsupported: this harness needs a desktop GL window.");
            return 2;
#else
            int cycles = 100;
            int timeout = 1800;
            if (String.IsNullOrWhiteSpace(room) || room.StartsWith('-')
                || cyclesText != null && (!Int32.TryParse(cyclesText, out cycles) || cycles < 1 || cycles > 5000)
                || timeoutText != null && (!Int32.TryParse(timeoutText, out timeout) || timeout < 1 || timeout > 86400))
            {
                Console.WriteLine("Usage: -respawnrendercheck ROOM [-cycles 1..5000] [-timeout 1..86400]"
                    + " (defaults: 100 cycles, 1800 wall seconds)");
                return 2;
            }
            int oldScale = RenderOptions.ResolutionScale;
            bool oldCel = RenderOptions.CelShading;
            float oldEdge = RenderOptions.CelEdge;
            bool oldFps = RenderOptions.ShowFps;
            bool oldForce = MapAudit.ForceEveryone;
            bool oldScoreboard = PlayerEntity.ModForceScoreboard;
            try
            {
                using var window = new CheckWindow();
                try
                {
                    window.Initialize(room, cycles, timeout);
                    window.Run();
                    return window.Report();
                }
                finally
                {
                    // Run can throw before OnUnload (including a failed
                    // negative control). Stop scene work before Dispose tears
                    // down the native window/context on either exit path.
                    window.Cleanup();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RESPAWNRENDER FAIL {room} | {ex}");
                return 1;
            }
            finally
            {
                RenderOptions.ResolutionScale = oldScale;
                RenderOptions.CelShading = oldCel;
                RenderOptions.CelEdge = oldEdge;
                RenderOptions.ShowFps = oldFps;
                MapAudit.ForceEveryone = oldForce;
                PlayerEntity.ModForceScoreboard = oldScoreboard;
            }
#endif
        }

#if !ANDROID
        // Like MapAudit, drive Scene directly: RenderWindow's normal loop
        // applies user window/focus/cursor settings and wall-clock pacing.
        private sealed class CheckWindow : GameWindow
        {
            private enum Phase { Warmup, Prepare, Dead, Settle, Done }
            private static readonly string[] Cases =
            {
                "opponent", "self", "environment", "beam-source", "rapid",
                "disruption", "whiteout", "scoreboard", "combined"
            };
            private const int SettleFrames = 300;
            private Scene _scene = null!;
            private string _room = "";
            private int _requested;
            private int _timeout;
            private readonly Stopwatch _wall = Stopwatch.StartNew();
            private Phase _phase;
            private int _age;
            private int _frames;
            private int _completed;
            private int _deaths;
            private int _respawns;
            private int _failures;
            private int _glErrors;
            private int _seenRendererGlErrors;
            private int _invariantFailures;
            private int _blackFrames;
            private int _fadeExcluded;
            private int _checkedFrames;
            private int _settleChecked;
            private int _resizes;
            private int _scales;
            private int _celToggles;
            private readonly int[] _coverage = new int[Cases.Length];
            private bool _rapidSecond;
            private bool _loaded;
            private bool _cleaned;
            private bool _effectSeen;
            private bool _controlDone;
            private int _controlComparisons;
            private int _controlFrames;
            private int _captures;
            private bool _capturePending;
            private readonly string _captureDirectory = Path.Combine(AppContext.BaseDirectory,
                "logs", $"respawn-render-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
            private byte[] _pixels = Array.Empty<byte>();
            private string Scenario => Cases[_completed % Cases.Length];
            private bool Whiteout => Scenario == "whiteout" || Scenario == "combined";
            private bool Disruption => Scenario == "disruption" || Scenario == "combined";
            private bool Scoreboard => Scenario == "scoreboard" || Scenario == "combined";

            public CheckWindow() : base(new GameWindowSettings { UpdateFrequency = 0 },
                new NativeWindowSettings
                {
                    ClientSize = new Vector2i(640, 480),
                    Title = "Fruity Prime respawn render check",
                    Profile = ContextProfile.Compatability,
                    Flags = ContextFlags.Default,
                    APIVersion = new Version(3, 2),
                    StartVisible = true,
                    StartFocused = false
                })
            {
                VSync = VSyncMode.Off;
                CursorState = CursorState.Normal;
            }

            public void Initialize(string room, int cycles, int timeout)
            {
                _room = room;
                _requested = cycles;
                _timeout = timeout;
                MapAudit.ForceEveryone = true;
                PlayerEntity.ModForceScoreboard = false;
                RenderOptions.ResolutionScale = 100;
                RenderOptions.CelShading = false;
                RenderOptions.CelEdge = 0.5f;
                RenderOptions.ShowFps = false; // Wall-clock FPS text cannot be a frozen-view baseline.
                // Frozen input snapshots prevent later keyboard/mouse activity
                // from driving the diagnostic even if someone focuses it.
                _scene = new Scene(FramebufferSize, KeyboardState.GetSnapshot(), MouseState.GetSnapshot(),
                    _ => { }, Close);
                _scene.ConsoleOutputEnabled = false;
                _scene.AddPlayer(Hunter.Samus);
                _scene.AddPlayer(Hunter.Kanden);
                for (int i = 0; i < PlayerEntity.Players.Count; i++)
                {
                    PlayerEntity player = PlayerEntity.Players[i];
                    player.IsBot = false;
                    player.BotLevel = 0;
                    if (i >= 2)
                    {
                        player.LoadFlags &= ~LoadFlags.Active;
                    }
                }
                PlayerEntity.PlayerCount = 2;
                PlayerEntity.MainPlayerIndex = 0;
                _scene.AddRoom(room, GameMode.Battle, playerCount: NetLaunch.RoomPlayerCount);
                Console.WriteLine($"RESPAWNRENDER start room={room} cycles={cycles} timeout={timeout}s"
                    + " | visible/unfocused, cursor free, vsync off | accelerated 60 Hz simulation"
                    + " | every step rendered; 300 settling frames per completed cycle"
                    + " | beam-source injects damage, does not test projectile collision");
            }

            protected override void OnLoad()
            {
                base.OnLoad();
                _scene.Size = FramebufferSize;
                _scene.OnLoad();
                _loaded = true;
                _scene.OnResize();
                GameState.PointGoal = 0;
                GameState.MatchTime = -1;
                PollErrors("load");
            }

            protected override void OnRenderFrame(FrameEventArgs args)
            {
                if (_wall.Elapsed.TotalSeconds > _timeout)
                {
                    throw new TimeoutException($"wall timeout after {_completed}/{_requested} cycles");
                }
                if (WindowState == WindowState.Minimized || FramebufferSize.X <= 0 || FramebufferSize.Y <= 0)
                {
                    throw new InvalidOperationException("Diagnostic window has no usable visible backbuffer.");
                }
                PrepareStep();
                if (_scene.Size != FramebufferSize)
                {
                    _scene.Size = FramebufferSize;
                    _scene.OnResize();
                    _resizes++;
                }
                GameState.ApplyPause();
                _scene.OnSimulationFrame();
                ObserveSpawn();
                ulong simulatedFrame = _scene.FrameCount;
                _scene.OnDrawFrame();
                if (!_scene.OnRenderFrame())
                {
                    throw new InvalidOperationException("Renderer stopped before the diagnostic completed.");
                }
                _frames++;
                if (_scene.FrameCount != simulatedFrame)
                {
                    throw new InvalidOperationException("Rendering advanced the simulation frame counter.");
                }
                // Check before readback, which binds the default READ FBO.
                if (!_scene.CheckRenderInvariants())
                {
                    _invariantFailures++;
                    Fail("end-of-frame GL invariant");
                }
                PollErrors("render/invariants");
                CheckPixels();
                PollErrors("backbuffer read");
                CaptureFailure();
                if (!_controlDone && _phase == Phase.Warmup && _age >= 299
                    && PlayerEntity.Main.Health > 0 && CameraSequence.Current == null
                    && _scene.FadeType == FadeType.None)
                {
                    CheckPassBoundaries();
                    _controlDone = true;
                }
                SwapBuffers();
                _scene.AfterRenderFrame();
                PollErrors("swap/after-frame");
                AdvancePhase();
                base.OnRenderFrame(args);
                if (_phase == Phase.Done)
                {
                    Close();
                }
            }

            private void PrepareStep()
            {
                PlayerEntity player = PlayerEntity.Main;
                if (_phase == Phase.Prepare && _age == 0)
                {
                    _rapidSecond = false;
                    _effectSeen = false;
                    if (Disruption) player.ModSetDisrupted(true);
                    if (Whiteout) player.BeginWhiteout();
                    PlayerEntity.ModForceScoreboard = Scoreboard;
                }
                if (_phase == Phase.Prepare && _age == 16)
                {
                    if ((Disruption || Whiteout) && !_effectSeen)
                    {
                        throw new InvalidOperationException("Requested transient effect was never active in a rendered frame.");
                    }
                    PlayerEntity.ModForceScoreboard = false;
                    Kill();
                }
                else if (_phase == Phase.Settle && Scenario == "rapid" && !_rapidSecond && _age == 1)
                {
                    _rapidSecond = true;
                    Kill();
                }
                // Leave whiteout live across the real Spawn entry point. Its
                // usual duration would otherwise expire during the death timer.
                if (_phase == Phase.Dead && Whiteout && player.RespawnTimer == 1)
                {
                    player.BeginWhiteout();
                }
                if (_phase == Phase.Settle)
                {
                    if (_age == 1 || _age == 150)
                    {
                        ClientSize = _age == 1 ? new Vector2i(800, 450) : new Vector2i(640, 480);
                    }
                    if (_age == 2 || _age == 151)
                    {
                        RenderOptions.ResolutionScale = _age == 2 ? 50 : 100;
                        _scales++;
                    }
                    if (_age == 5 || _age == 152)
                    {
                        RenderOptions.CelShading = _age == 5;
                        _celToggles++;
                    }
                }
            }

            private void Kill()
            {
                PlayerEntity player = PlayerEntity.Main;
                if (player.Health <= 0 || !player.LoadFlags.TestFlag(LoadFlags.Spawned))
                {
                    throw new InvalidOperationException("Expected a live, spawned player before kill.");
                }
                _scene.NoteRenderLifecycle($"harness kill {_completed + 1}/{_requested} {Scenario}");
                int deathsBefore = GameState.Deaths[player.SlotIndex];
                EntityBase? source = PlayerEntity.Players[1];
                DamageFlags flags = DamageFlags.IgnoreInvuln | DamageFlags.NoDmgInvuln;
                if (Scenario == "self" || Scenario == "rapid") source = player;
                if (Scenario == "environment")
                {
                    source = null;
                    flags |= DamageFlags.Death;
                }
                if (Scenario == "beam-source")
                {
                    source = new BeamProjectileEntity(_scene)
                    {
                        Owner = source, Beam = BeamType.PowerBeam, BeamKind = BeamType.PowerBeam
                    };
                }
                player.TakeDamage(10000, flags, direction: null, source: source);
                if (player.Health != 0 || GameState.Deaths[player.SlotIndex] != deathsBefore + 1)
                {
                    throw new InvalidOperationException("Injected damage did not produce exactly one real death.");
                }
                _deaths++;
                _phase = Phase.Dead;
                _age = 0;
                _scene.NoteRenderLifecycle("harness respawn requested via existing ForceEveryone hook");
            }

            private void ObserveSpawn()
            {
                PlayerEntity player = PlayerEntity.Main;
                if (_phase != Phase.Dead || player.Health <= 0) return;
                if (!player.LoadFlags.TestFlag(LoadFlags.Spawned) || player.RespawnTimer != 0)
                {
                    throw new InvalidOperationException("Respawn did not restore spawned state/timer.");
                }
                _respawns++;
                _phase = Phase.Settle;
                _age = 0;
                _settleChecked = 0;
                _scene.NoteRenderLifecycle("harness first rendered frame after respawn");
                if (player.HudDisruptedState != 0 || player.HudDisruptionFactor != 0
                    || player.HudWhiteoutState != -1 || player.HudWhiteoutFactor != 0)
                {
                    Fail("respawn left a transient disruption/whiteout state active");
                }
            }

            private void AdvancePhase()
            {
                PlayerEntity player = PlayerEntity.Main;
                if (_phase == Phase.Prepare)
                {
                    _effectSeen |= (!Disruption || player.HudDisruptedState != 0 && player.HudDisruptionFactor > 0)
                        && (!Whiteout || player.HudWhiteoutState >= 0);
                }
                if (_phase == Phase.Settle && player.Health <= 0)
                {
                    throw new InvalidOperationException("Unexpected death during post-respawn settling.");
                }
                _age++;
                if (_phase == Phase.Warmup)
                {
                    if (_age > 1800) throw new TimeoutException("Initial spawn/intro did not settle in 30 simulated seconds.");
                    if (_age >= 300 && player.Health > 0 && CameraSequence.Current == null
                        && _scene.FadeType == FadeType.None && _checkedFrames > 0 && _controlDone)
                    {
                        _phase = Phase.Prepare;
                        _age = 0;
                    }
                }
                else if (_phase == Phase.Dead && _age > 600)
                {
                    throw new TimeoutException("No respawn within ten simulated seconds of death.");
                }
                else if (_phase == Phase.Settle && _age == SettleFrames)
                {
                    if (_settleChecked < 120 || IsBlackFade())
                    {
                        Fail("settling never provided two seconds of non-fade backbuffer checks or ended in black fade");
                    }
                    _coverage[_completed % Cases.Length]++;
                    _completed++;
                    if (_completed % 10 == 0 || _completed == _requested)
                    {
                        Console.WriteLine($"RESPAWNRENDER progress {_completed}/{_requested}"
                            + $" frames={_frames} simulated={_frames / 60.0:F1}s wall={_wall.Elapsed.TotalSeconds:F1}s"
                            + $" failures={_failures}");
                    }
                    _phase = _completed == _requested ? Phase.Done : Phase.Prepare;
                    _age = 0;
                }
            }

            private bool IsBlackFade() => _scene.FadeType == FadeType.FadeInBlack
                || _scene.FadeType == FadeType.FadeOutBlack || _scene.FadeType == FadeType.FadeOutInBlack;

            private void CheckPixels()
            {
                // Full-final-frame black only. Region/bar detection and image
                // captures are deliberately outside this P0 diagnostic.
                if (_phase == Phase.Dead || PlayerEntity.Main.Health <= 0) return;
                if (IsBlackFade())
                {
                    _fadeExcluded++;
                    return;
                }
                ReadFinalPixels();
                _checkedFrames++;
                if (_phase == Phase.Settle) _settleChecked++;
                for (int i = 0; i < _pixels.Length; i++)
                {
                    if (_pixels[i] > 3) return;
                }
                _blackFrames++;
                Fail("full black final backbuffer outside intended black fade");
            }

            private void ReadFinalPixels()
            {
                int bytes = checked(_scene.Size.X * _scene.Size.Y * 3);
                if (_pixels.Length != bytes) _pixels = new byte[bytes];
                GL.GetInteger(GetPName.PackAlignment, out int alignment);
                GL.GetInteger(GetPName.ReadBuffer, out int readBuffer);
                try
                {
                    GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                    GL.ReadBuffer(ReadBufferMode.Back);
                    GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
                    GL.ReadPixels(0, 0, _scene.Size.X, _scene.Size.Y,
                        PixelFormat.Rgb, PixelType.UnsignedByte, _pixels);
                }
                finally
                {
                    GL.PixelStore(PixelStoreParameter.PackAlignment, alignment);
                    GL.ReadBuffer((ReadBufferMode)readBuffer);
                }
            }

            private void CheckPassBoundaries()
            {
                // No simulation, swap or clock advance between these pictures.
                // Check two clean draws first so instability cannot be confused
                // with failed repair. Repeat with cel depth/post processing on.
                ulong frame = _scene.FrameCount;
                float time = _scene.GlobalElapsedTime;
                int failuresBefore = _failures;
                string[] poisons = { "scissor", "depth", "color-mask", "blend", "raster", "hud-mask", "combined" };
                // Desktop compatibility GL creates this reserved name on bind,
                // as UiOverlay does. GenTexture would take the next name from
                // Scene's implicit texture counter while cel allocates depth.
                const int texture = 1_200_000;
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 0, 0, 0, 255 });
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                try
                {
                    for (int cel = 0; cel < 2; cel++)
                    {
                        RenderOptions.CelShading = cel != 0;
                        long postPasses = _scene.RenderPostProcessCount;
                        DrawFrozen(); // Also lets a new cel target finish calibration.
                        if (cel != 0 && _scene.RenderPostProcessCount == postPasses)
                        {
                            throw new InvalidOperationException("Cel outline did not execute; postprocess coverage unavailable on this driver.");
                        }
                        DrawFrozen();
                        byte[] baseline = (byte[])_pixels.Clone();
                        int hudProgram = GL.GetInteger(GetPName.CurrentProgram);
                        int mask = GL.GetUniformLocation(hudProgram, "use_mask");
                        if (mask < 0) throw new InvalidOperationException("End-frame HUD program has no use_mask uniform for negative control.");
                        DrawFrozen();
                        ComparePixels(baseline, $"clean repeat cel={cel}");
                        foreach (string poison in poisons)
                        {
                            // Erase the old final picture first: disabled writes
                            // must not pass by retaining the previous good image.
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                            GL.Disable(EnableCap.ScissorTest);
                            GL.ColorMask(true, true, true, true);
                            GL.ClearColor(0f, 0f, 0f, 1f);
                            GL.Clear(ClearBufferMask.ColorBufferBit);
                            bool all = poison == "combined";
                            if (all || poison == "scissor")
                            {
                                GL.Enable(EnableCap.ScissorTest);
                                GL.Scissor(0, 0, 1, 1);
                            }
                            if (all || poison == "depth")
                            {
                                GL.DepthMask(false);
                                GL.Enable(EnableCap.DepthTest);
                                GL.DepthFunc(DepthFunction.Never);
                            }
                            if (all || poison == "color-mask") GL.ColorMask(false, false, false, false);
                            if (all || poison == "blend")
                            {
                                GL.Enable(EnableCap.Blend);
                                GL.BlendEquation(BlendEquationMode.FuncReverseSubtract);
                                GL.BlendFunc(BlendingFactor.Zero, BlendingFactor.Zero);
                            }
                            if (all || poison == "raster")
                            {
                                GL.Enable(EnableCap.CullFace);
                                GL.CullFace(TriangleFace.FrontAndBack);
                                GL.PolygonMode(TriangleFace.FrontAndBack, OpenTK.Graphics.OpenGL.PolygonMode.Line);
                                GL.Enable(EnableCap.StencilTest);
                                GL.StencilFunc(StencilFunction.Never, 0, 0xFF);
                                GL.StencilMask(0);
                                GL.Viewport(0, 0, 1, 1);
                            }
                            if (all || poison == "hud-mask")
                            {
                                GL.UseProgram(hudProgram);
                                GL.Uniform1(mask, 1);
                                GL.ActiveTexture(TextureUnit.Texture1);
                                GL.BindTexture(TextureTarget.Texture2D, texture);
                            }
                            PollErrors($"poison {poison}");
                            DrawFrozen();
                            ComparePixels(baseline, $"poison={poison} cel={cel}");
                            _controlComparisons++;
                        }
                    }
                }
                finally
                {
                    GL.DeleteTexture(texture);
                    RenderOptions.CelShading = false;
                }
                if (frame != _scene.FrameCount || time != _scene.GlobalElapsedTime)
                {
                    throw new InvalidOperationException("Frozen-view control advanced simulation.");
                }
                Console.WriteLine($"RESPAWNRENDER boundary-control comparisons={_controlComparisons}"
                    + $" failures={_failures - failuresBefore} | exact RGB comparison; cel off/on; simulation frozen");
                if (_failures != failuresBefore)
                {
                    throw new InvalidOperationException("Frozen-view negative control failed; see comparison and failure PNGs.");
                }
            }

            private void DrawFrozen()
            {
                _scene.OnDrawFrame();
                if (!_scene.OnRenderFrame()) throw new InvalidOperationException("Frozen-view render stopped.");
                _controlFrames++;
                if (!_scene.CheckRenderInvariants())
                {
                    _invariantFailures++;
                    Fail("negative-control end-of-frame invariant");
                }
                ReadFinalPixels();
                PollErrors("negative-control draw/read");
                CaptureFailure();
            }

            private void ComparePixels(byte[] baseline, string label)
            {
                int changed = 0;
                for (int i = 0; i < baseline.Length; i += 3)
                {
                    if (baseline[i] != _pixels[i] || baseline[i + 1] != _pixels[i + 1]
                        || baseline[i + 2] != _pixels[i + 2]) changed++;
                }
                Console.WriteLine($"RESPAWNRENDER boundary {label} changedPixels={changed}/{baseline.Length / 3}");
                if (changed != 0)
                {
                    Fail($"frozen final image changed: {label}");
                    CaptureFailure();
                }
            }

            private void CaptureFailure()
            {
                if (!_capturePending || _captures >= 3) return;
                _capturePending = false;
                _captures++;
                try
                {
                    ReadFinalPixels();
                    Directory.CreateDirectory(_captureDirectory);
                    string path = Path.Combine(_captureDirectory, $"failure-{_captures}-cycle-{_completed + 1}-frame-{_frames}.png");
                    // ScreenCapture.SaveWindow deliberately refuses black images.
                    // Use its encoder seam directly so the failing image survives.
                    if (ScreenCapture.PngWriter != null)
                    {
                        ScreenCapture.PngWriter(_pixels, _scene.Size.X, _scene.Size.Y, path);
                    }
                    else
                    {
                        using FileStream output = File.Create(path);
                        StbImage.FlipVerticallyOnSave = true;
                        StbImage.WritePng<byte>(_pixels, _scene.Size.X, _scene.Size.Y, StbiImageFormat.Rgb, output);
                    }
                    Console.WriteLine($"RESPAWNRENDER failure image: {path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RESPAWNRENDER failure image unavailable: {ex.Message}");
                }
            }

            private void PollErrors(string stage)
            {
                // Debug checkpoints can consume errors before this poll.
                if (_scene.RenderGlErrorCount != _seenRendererGlErrors)
                {
                    _seenRendererGlErrors = _scene.RenderGlErrorCount;
                    Fail($"renderer GL checkpoints recorded {_seenRendererGlErrors} errors by {stage}");
                }
                for (int i = 0; i < 64; i++)
                {
                    ErrorCode error = GL.GetError();
                    if (error == ErrorCode.NoError) return;
                    _glErrors++;
                    Fail($"GL {error} at {stage}");
                }
                throw new InvalidOperationException("GL error queue did not drain in 64 reads.");
            }

            private void Fail(string reason)
            {
                _failures++;
                _capturePending = true;
                if (_failures <= 20)
                {
                    Console.WriteLine($"RESPAWNRENDER failure cycle={_completed + 1} case={Scenario}"
                        + $" phase={_phase} age={_age} frame={_frames}: {reason}");
                    _scene.NoteRenderLifecycle($"harness failure: {reason}");
                }
            }

            public int Report()
            {
                int rendererErrors = _scene.RenderGlErrorCount;
                int expectedDeaths = _requested + _coverage[4]; // rapid cases have a second respawn
                bool pass = _phase == Phase.Done && _completed == _requested && _failures == 0
                    && _controlDone && _controlComparisons == 14
                    && rendererErrors == 0 && _respawns == expectedDeaths && _deaths == expectedDeaths
                    && _resizes >= _requested * 2 && _scales >= _requested * 2 && _celToggles >= _requested * 2;
                Console.WriteLine($"RESPAWNRENDER {(pass ? "PASS" : "FAIL")} room={_room}"
                    + $" cycles={_completed}/{_requested} deaths={_deaths} respawns={_respawns}"
                    + $" frames={_frames} checked={_checkedFrames} black={_blackFrames} fadeExcluded={_fadeExcluded}"
                    + $" invariants={_invariantFailures} gl={_glErrors} rendererGl={rendererErrors} failures={_failures}"
                    + $" resize={_resizes} scale={_scales} cel={_celToggles}"
                    + $" controlComparisons={_controlComparisons} controlDraws={_controlFrames}"
                    + $" simulated={_frames / 60.0:F1}s wall={_wall.Elapsed.TotalSeconds:F1}s");
                for (int i = 0; i < Cases.Length; i++)
                {
                    Console.WriteLine($"RESPAWNRENDER coverage {Cases[i]}={_coverage[i]}");
                }
                Console.WriteLine("RESPAWNRENDER scope: full-black/invariants/GL only; HUD bars, fullscreen,"
                    + " alt-tab, projectile collision and other drivers/platforms require separate validation.");
                return pass ? 0 : 1;
            }

            public void Cleanup()
            {
                if (_cleaned || _scene == null) return;
                _cleaned = true;
                try
                {
                    // Match the normal window's scene shutdown: GL deletion
                    // alone leaves OpenAL and the output loop alive at exit.
                    _scene.DoCleanup();
                    if (!MphRead.Sound.Sfx.ShutdownCompletion.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("Audio device did not finish closing before diagnostic exit.");
                    }
                    // The normal shell keeps this process-wide engine for its
                    // next match. This one-shot diagnostic has no next match;
                    // release the native backend before runtime shutdown.
                    MusicPlayer.PlaybackDevice?.Stop();
                    MusicPlayer.Engine?.Dispose();
                }
                finally
                {
                    if (_loaded) _scene.UnloadGl();
                }
            }

            protected override void OnUnload()
            {
                Cleanup();
                base.OnUnload();
            }
        }
#endif
    }
}
