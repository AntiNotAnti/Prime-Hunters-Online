using System;
using OpenTK.Mathematics;

namespace MphRead.Mods.Render
{
    /// <summary>
    /// The hunter the launcher is asking about, drawn as the real model in the
    /// window the screens are drawn into.
    ///
    /// <para>
    /// The launcher's stand was boxes, and the reason it was boxes is in
    /// <c>HunterStand</c>'s own note: the hunters are an NDS display-list
    /// format only the engine reads, and the engine was not running while the
    /// launcher's screens were. That stopped being true when the launcher
    /// became a screen inside the game's own window -- the engine is right
    /// there, it simply has no match loaded.
    /// </para>
    ///
    /// <para>
    /// <b>So the launcher stands a scene up with nothing in it.</b> A
    /// <see cref="Scene"/> is what owns the shader, the toon table and the
    /// render-item machinery, and <c>HunterPreviewEntity</c> is an entity that
    /// is never inserted into one -- it only needs a scene to build its items
    /// against. Neither wants a room. `BeginScene` + `LoadScene` is the
    /// documented way to make one and is what a match already uses; this asks
    /// for the same thing and puts nothing in it.
    /// </para>
    ///
    /// <para>
    /// <b>Where it lands.</b> The screens publish the stand's rectangle in
    /// fractions of the window -- the same shape the results screen's HUD
    /// publishes, for the same reason -- and the model is drawn into the back
    /// buffer between the photograph and the screens' own texture. The screens
    /// then leave that rectangle transparent, and the overlay is composited
    /// premultiplied, so what shows through the hole is this.
    /// </para>
    ///
    /// <para>
    /// <b>Every failure is the boxes again.</b> A scene that will not stand
    /// up, a model that will not load, the frames before it has: all of them
    /// leave <see cref="Drawn"/> false, and the stand draws itself the way it
    /// always did rather than leaving a hole with nothing behind it. That is
    /// the whole safety argument for putting a renderer this deep under a
    /// launcher, and it is why the hole is decided by what was drawn rather
    /// than by what was asked for.
    /// </para>
    /// </summary>
    public static class LauncherHunter
    {
        /// <summary>Whether a screen is asking for one this frame.</summary>
        public static bool Wanted { get; set; }

        /// <summary>Who, and in which suit.</summary>
        public static Hunter Hunter { get; set; } = Hunter.Samus;

        public static int Suit { get; set; }

        /// <summary>
        /// Where it goes, in fractions of the window: left, top, right,
        /// bottom, measured down from the top the way a screen's own
        /// coordinates are.
        /// </summary>
        public static float Left { get; set; }
        public static float Top { get; set; }
        public static float Right { get; set; }
        public static float Bottom { get; set; }

        /// <summary>
        /// Whether the last frame actually put a model on the screen.
        ///
        /// What the stand reads to decide between a hole and its own boxes.
        /// </summary>
        public static bool Drawn { get; private set; }

        private static bool _failed;
        private static bool _said;

        /// <summary>
        /// The scene this owns. Kept for the life of the process rather than
        /// built per visit: standing one up means compiling the shaders and
        /// cutting a framebuffer, and the drawer it draws into is opened and
        /// closed a dozen times in a sitting.
        /// </summary>
        private static Scene? _scene;

        /// <summary>
        /// Forget everything: a match is starting or has ended, and the scene
        /// this was using is about to be replaced or has been.
        /// </summary>
        public static void Reset()
        {
            Wanted = false;
            Drawn = false;
            Scene.LauncherPreview = false;
        }

        /// <summary>
        /// Draw it, if a screen asked and there is somewhere to draw it.
        ///
        /// Called from the launcher's own frame, after the photograph and
        /// before the screens: the model has to be under the texture that
        /// leaves the hole for it.
        /// </summary>
        public static void Draw(RenderWindow window, int width, int height)
        {
            Drawn = false;
            if (_failed || !Wanted || width <= 0 || height <= 0)
            {
                Scene.LauncherPreview = false;
                return;
            }
            if (Right - Left <= 0.001f || Bottom - Top <= 0.001f)
            {
                Scene.LauncherPreview = false;
                return;
            }
            try
            {
                // The window's own scene when there is a match in it -- the
                // results screen is drawn over one -- and this object's
                // otherwise. Either way the model is drawn *over* the screens
                // rather than under: the panel it sits in is opaque, so a
                // model beneath the texture is a model behind a card. That
                // held on the launcher and it holds here.
                Scene? scene = window.HasScene ? window.Scene : _scene;
                if (scene == null)
                {
                    // Nothing in it: no room, no players, no entities. It
                    // exists to own a shader and a render-item list.
                    //
                    // And it is *ours*, not the window's. OnRenderFrame routes
                    // on the window's own scene being null -- that is what
                    // tells the launcher's frame apart from a match's -- so a
                    // scene handed to the window here would send it down the
                    // match path with no room in it, which is a crash on the
                    // first frame of the front screen.
                    _scene = window.NewSideScene();
                    _scene.OnLoad();
                    _scene.OnResize();
                    scene = _scene;
                }
                Scene.LauncherPreview = true;
                Scene.LauncherHunter = Hunter;
                Scene.LauncherSuit = Math.Clamp(Suit, 0, 3);
                Scene.PreviewWanted = true;
                Scene.PreviewLeft = Left;
                Scene.PreviewTop = Top;
                Scene.PreviewRight = Right;
                Scene.PreviewBottom = Bottom;
                Drawn = scene!.ModDrawPreviewAlone(new Vector2i(width, height));
                if (Drawn && !_said)
                {
                    // Once, when it first works. The rectangle is on the line
                    // because it is the thing that goes wrong -- it crosses
                    // two coordinate systems (see HunterStand.Publish) and a
                    // wrong one is a model scissored away to nothing, which
                    // looks exactly like a model that failed to load.
                    _said = true;
                    Mods.DebugLog.Line("ui", "the hunter preview is on, at "
                        + $"({Left:0.###},{Top:0.###})-({Right:0.###},{Bottom:0.###}) "
                        + $"of {width}x{height}");
                }
            }
            catch (Exception ex)
            {
                _failed = true;
                Drawn = false;
                Scene.LauncherPreview = false;
                Mods.DebugLog.Line("ui",
                    $"the hunter preview could not be set up: {ex.Message}");
            }
        }
    }
}
