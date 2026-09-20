namespace MphRead.Mods
{
    /// <summary>
    /// Global switches that put the engine into "render me a clean preview"
    /// mode.
    ///
    /// A static flag rather than parameters threaded through the renderer:
    /// the HUD draw sites are deep inside PlayerHud and Renderer, and
    /// plumbing an argument down to them would touch far more upstream code
    /// than a single condition does. Everything here is inert unless a
    /// thumbnail capture turns it on.
    /// </summary>
    public static class ThumbnailMode
    {
        /// <summary>
        /// True while capturing. Suppresses HUD drawing -- the intro camera
        /// still paints mode rules, queued messages, and a darkening filter
        /// model over the scene, all of which belong to a live match rather
        /// than to a preview image.
        /// </summary>
        public static bool Active { get; private set; }

        private static float _sfxVolume = 0.35f;
        private static float _musicVolume = 1;

        public static void Enter()
        {
            if (Active)
            {
                return;
            }
            _sfxVolume = MphRead.Sound.Sfx.Volume;
            _musicVolume = Music.UserVolume;
            Active = true;
            // Silence rather than skip loading: the sound system is wired
            // into scene setup, and muting is the change with the smallest
            // blast radius. Batches spawn several processes at once, so
            // audible playback would also overlap into noise.
            MphRead.Sound.Sfx.Volume = 0;
            Music.UserVolume = 0;
        }

        /// <summary>
        /// Back to being a game.
        ///
        /// The desktop never needed this: every capture is a worker process
        /// that exits when its picture is written, so the flag dies with it.
        /// Android renders previews in the app's own process, where entering
        /// and never leaving means the next match runs with no HUD and no
        /// sound -- which is exactly how it was reported.
        /// </summary>
        public static void Exit()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            MphRead.Sound.Sfx.Volume = _sfxVolume;
            Music.SetUserVolume(_musicVolume);
        }
    }
}
