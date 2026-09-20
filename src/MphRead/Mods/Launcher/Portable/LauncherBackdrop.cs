using System;

namespace MphRead.Mods.Launcher
{
    public enum LauncherBackdropScene
    {
        Home,
        Play,
        Multiplayer,
        Offline,
        Adventure,
        ReplayStudio,
        Settings,
        MapEditor,
        CreateLobby,
        Lobby,
        Setup
    }

    /// <summary>
    /// Presentation-neutral description of the cinematic launcher ground.
    /// Desktop GL and Avalonia/Android consume the same state.
    /// </summary>
    public static class LauncherBackdrop
    {
        private static LauncherBackdropScene _scene = LauncherBackdropScene.Home;
        private static string _roomKey = DefaultRoom(LauncherBackdropScene.Home);

        public static event Action? Changed;

        public static LauncherBackdropScene Scene => _scene;
        public static string RoomKey => _roomKey;
        public static string CacheKey => $"{_scene}:{_roomKey}";

        /// <summary>
        /// Crop slightly inside the map render so the desktop GL layer has
        /// enough image outside the viewport for a slow cinematic drift.
        /// </summary>
        public static float Zoom => _scene switch
        {
            LauncherBackdropScene.Home => 0.91f,
            LauncherBackdropScene.Adventure => 0.90f,
            LauncherBackdropScene.ReplayStudio => 0.94f,
            LauncherBackdropScene.Settings => 0.96f,
            _ => 0.93f
        };

        public static void Set(LauncherBackdropScene scene, string? roomKey = null)
        {
            string nextRoom = String.IsNullOrWhiteSpace(roomKey)
                ? DefaultRoom(scene)
                : roomKey.Trim();
            if (_scene == scene
                && String.Equals(_roomKey, nextRoom, StringComparison.OrdinalIgnoreCase))
                return;

            _scene = scene;
            _roomKey = nextRoom;
            Changed?.Invoke();
        }

        public static void Refresh() => Changed?.Invoke();

        public static void SetRoom(string? roomKey)
        {
            if (String.IsNullOrWhiteSpace(roomKey))
                return;
            string next = roomKey.Trim();
            if (String.Equals(_roomKey, next, StringComparison.OrdinalIgnoreCase))
                return;
            _roomKey = next;
            Changed?.Invoke();
        }

        private static string DefaultRoom(LauncherBackdropScene scene) => scene switch
        {
            LauncherBackdropScene.Home => "MP3 PROVING GROUND",
            LauncherBackdropScene.Play => "MP1 SANCTORUS",
            LauncherBackdropScene.Multiplayer => "MP3 PROVING GROUND",
            LauncherBackdropScene.Offline => "MP3 PROVING GROUND",
            LauncherBackdropScene.Adventure => "UNIT1 ALINOS LANDFALL",
            LauncherBackdropScene.ReplayStudio => "MP11 BREAKTHROUGH",
            LauncherBackdropScene.Settings => "AD2 ALINOS PERCH",
            LauncherBackdropScene.CreateLobby => "MP3 PROVING GROUND",
            LauncherBackdropScene.Lobby => "MP3 PROVING GROUND",
            // Deliberately no game-derived image for setup/editor placeholders.
            _ => ""
        };
    }
}
