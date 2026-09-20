using System;
using System.Linq;
using MphRead.Entities;
using MphRead.Hud;
using MphRead.Mods.Chat;
using MphRead.Mods.Input;
using MphRead.Mods.Network;
using OpenTK.Mathematics;

namespace MphRead.Mods.Replay
{
    internal static class ReplayHud
    {
        private static Scene? _scene;
        private static HudObjectInstance? _font;
        private static readonly bool[] Markers = new bool[154];
        private static long _textAt;
        private static long _diagnosticsAt;
        private static string _status = "", _watching = "";
        private static string[] _analyticsLines = Array.Empty<string>();
        private static string[] _networkLines = Array.Empty<string>();
        private static ReplayState _state;

        public static bool ShowAnalytics { get; set; }
        public static bool ShowNetworkDebug { get; set; }

        internal static void Reset()
        {
            _scene = null;
            _font = null;
            _diagnosticsAt = 0;
            _analyticsLines = Array.Empty<string>();
            _networkLines = Array.Empty<string>();
        }

        private static readonly ColorRgba[] Palette =
        {
            new ColorRgba(), new ColorRgba(255, 255, 255, 255)
        };

        public static string Time(uint frame) => $"{frame / 3600:00}:{frame / 60 % 60:00}";

        public static void Draw(Scene scene)
        {
            if (!DemoPlayback.IsActive) return;
            if (_scene != scene)
            {
                _scene = scene;
                _font = new HudObjectInstance(width: ChatFont.Cell, height: ChatFont.Cell);
                _font.SetPaletteData(Palette, scene);
                _font.SetCharacterData(ChatFont.Pixels, scene);
                _font.Enabled = true;
                _textAt = 0;
                _diagnosticsAt = 0;
                Array.Clear(Markers);
                foreach (ReplayEvent marker in DemoPlayback.Events)
                {
                    if (marker.Type is not (ReplayEventType.Kill
                        or ReplayEventType.PlayerDeath
                        or ReplayEventType.Objective
                        or ReplayEventType.MatchEnded))
                        continue;
                    int at = ReplayController.DurationFrames == 0 ? 0
                        : (int)(153UL * marker.Frame / ReplayController.DurationFrames);
                    Markers[Math.Clamp(at, 0, 153)] = true;
                }
            }

            float alpha = ReplayController.State == ReplayState.Playing
                && Environment.TickCount64 - ReplayController.LastInteraction > 4000
                    ? 0.45f : 1;
            scene.DrawHudFlatBox(46, 159, 210, 191,
                new Vector4(0, 0, 0, alpha * 0.7f));
            if (Environment.TickCount64 - _textAt >= 100
                || _state != ReplayController.State)
            {
                _state = ReplayController.State;
                _textAt = Environment.TickCount64;
                string state = _state == ReplayState.Ended ? "Replay finished"
                    : _state == ReplayState.Error ? "Replay damaged"
                    : _state.ToString();
                _status = $"{state}   {Time(ReplayController.CurrentFrame)} / "
                    + $"{Time(ReplayController.DurationFrames)}   "
                    + $"{ReplayController.PlaybackRate:0.##}x";
                int slot = PlayerEntity.MainPlayerIndex;
                _watching = ReplayCamera.Mode is ReplayCameraMode.Chase or ReplayCameraMode.Orbit
                    ? $"{ReplayCamera.Mode}: {GameState.Nicknames[
                        Math.Clamp(slot, 0, GameState.Nicknames.Length - 1)]}"
                    : SpectatorMode.FreeCamera ? "Free camera"
                    : $"Watching: {(slot >= 0 && slot < GameState.Nicknames.Length
                        ? GameState.Nicknames[slot] : "")}";
            }

            Text(scene, 49, 163, _status, alpha, 207);
            float progress = ReplayController.DurationFrames == 0 ? 0
                : ReplayController.CurrentFrame / (float)ReplayController.DurationFrames;
            scene.DrawHudFlatBox(51, 173, 205, 175,
                new Vector4(0.4f, 0.4f, 0.4f, alpha));
            scene.DrawHudFlatBox(51, 173, 51 + 154 * progress, 175,
                new Vector4(0.4f, 1, 0.6f, alpha));
            for (int i = 0; i < Markers.Length; i++)
            {
                if (Markers[i])
                {
                    scene.DrawHudFlatBox(51 + i, 172, 52 + i, 176,
                        new Vector4(1, 0.65f, 0.3f, alpha));
                }
            }
            Text(scene, 49, 178, _watching, alpha, 207);

            // The essential transport stays discoverable while watching. The
            // full Replay Studio remains in the pause menu for editing, camera
            // and export work, but play/pause, seek and speed do not require it.
            float controlsAlpha = Math.Max(alpha, 0.72f);
            if (ReplayController.AtEnd)
            {
                Text(scene, 49, 183, "Space/A: restart replay", controlsAlpha, 207);
                Text(scene, 49, 188, "Home: restart   Esc: menu", controlsAlpha, 207);
            }
            else if (ReplayController.State == ReplayState.Error)
            {
                Text(scene, 49, 183, "Replay playback error", controlsAlpha, 207);
                Text(scene, 49, 188, "Esc: menu, then Replay Studio", controlsAlpha, 207);
            }
            else if (InputSourceTracker.Current == InputSource.Gamepad)
            {
                Text(scene, 49, 183, "A: play/pause   D-pad L/R: seek", controlsAlpha, 207);
                Text(scene, 49, 188, "D-pad U/D: speed   X: step", controlsAlpha, 207);
            }
            else if (InputSourceTracker.Current == InputSource.Touch)
            {
                Text(scene, 49, 183, "PLAY/PAUSE   -5S / +5S", controlsAlpha, 207);
                Text(scene, 49, 188, "MENU: full Replay Studio", controlsAlpha, 207);
            }
            else
            {
                Text(scene, 49, 183, "Space: play/pause   Left/Right: seek", controlsAlpha, 207);
                Text(scene, 49, 188, "[/]: speed   .: step   Home: restart", controlsAlpha, 207);
            }

            if (ShowAnalytics || ShowNetworkDebug)
                RefreshDiagnostics();

            float nextY = 6;
            if (ShowAnalytics && _analyticsLines.Length > 0)
            {
                float height = 7 + _analyticsLines.Length * 7;
                scene.DrawHudFlatBox(4, nextY - 2, 154, nextY + height,
                    new Vector4(0, 0, 0, 0.72f));
                foreach (string line in _analyticsLines)
                {
                    Text(scene, 7, nextY, line, 0.95f, 151);
                    nextY += 7;
                }
                nextY += 5;
            }

            if (ShowNetworkDebug && _networkLines.Length > 0)
            {
                float height = 7 + _networkLines.Length * 7;
                scene.DrawHudFlatBox(4, nextY - 2, 205, nextY + height,
                    new Vector4(0, 0, 0, 0.72f));
                foreach (string line in _networkLines)
                {
                    Text(scene, 7, nextY, line, 0.95f, 202);
                    nextY += 7;
                }
            }

            if (ReplayVideoExporter.Active)
            {
                scene.DrawHudFlatBox(46, 149, 210, 158,
                    new Vector4(0, 0, 0, 0.72f));
                Text(scene, 49, 151, ReplayVideoExporter.Status, 1, 207);
            }
        }

        private static void RefreshDiagnostics()
        {
            if (Environment.TickCount64 - _diagnosticsAt < 500) return;
            _diagnosticsAt = Environment.TickCount64;

            ReplayAnalyticsSnapshot analytics = ReplayStudio.Analytics();
            var lines = analytics.Players
                .OrderByDescending(p => p.Kills)
                .ThenByDescending(p => p.Damage)
                .Take(4)
                .Select(p =>
                {
                    string name = p.Slot < GameState.Nicknames.Length
                        && !String.IsNullOrWhiteSpace(GameState.Nicknames[p.Slot])
                            ? GameState.Nicknames[p.Slot] : $"P{p.Slot + 1}";
                    return $"{name}: {p.Kills}K/{p.Deaths}D  {p.Damage} dmg  {p.ObjectiveEvents} obj";
                }).ToList();
            lines.Insert(0, $"ANALYTICS  {analytics.TotalKills} kills  "
                + $"{analytics.TotalDamage} dmg  {analytics.ObjectiveEvents} obj");
            _analyticsLines = lines.ToArray();

            ReplayNetworkSnapshot net = ReplayNetworkDiagnostics.Snapshot();
            _networkLines = new[]
            {
                $"REPLAY NET  {net.Packets} packets  {net.Bytes / 1024d:0.0} KiB",
                $"{net.Snapshots} snapshots  {net.Intents} intents  "
                    + $"max burst {net.MaxPacketsInFrame}",
                $"max snapshot gap {net.MaxSnapshotGapFrames}f  "
                    + $"last snapshot {net.LastSnapshotFrame}f",
                $"checkpoints {ReplayCheckpointManager.Count}  "
                    + $"director {ReplayDirector.Reason}"
            };
        }

        private static void Text(Scene scene, float x, float y, string text,
            float alpha, float maxX)
        {
            var font = _font!;
            font.Alpha = alpha;
            foreach (char ch in text)
            {
                int index = ChatFont.Index(ch);
                if (index < 0) continue;
                font.PositionX = x / 256;
                font.PositionY = y / 192;
                font.SetData(index, new ColorRgba(210, 255, 225, 255), scene);
                scene.DrawHudObject(font, mode: 1, scale: 0.55f);
                x += ChatFont.Widths[index] * 0.55f;
                if (x > maxX) break;
            }
        }
    }
}
