using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace MphRead.Droid
{
    /// <summary>
    /// The controls a thumb sees, drawn over the game.
    ///
    /// A plain Android view rather than anything in GL: it is a dozen circles
    /// that change when they are touched, and putting them through the engine's
    /// renderer would mean HUD geometry, a second projection and a redraw every
    /// frame for something that changes when a finger moves. Here it redraws
    /// only when the touch state changes, and the game underneath is untouched.
    ///
    /// Its other job is to be the only thing receiving touches: it covers the
    /// surface, so every finger arrives here and is handed to
    /// <see cref="TouchControls"/>, which is where the meaning is.
    ///
    /// The palette is the launcher's (<c>GuiTheme</c>), by value -- the same
    /// reason that file gives for repeating LauncherTheme's numbers.
    /// </summary>
    internal sealed class TouchOverlayView : View
    {
        private readonly TouchControls _controls;
        private readonly Paint _fill = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _stroke = new Paint(PaintFlags.AntiAlias);
        private readonly Paint _text = new Paint(PaintFlags.AntiAlias);

        private static readonly Color _edge = Color.Argb(150, 38, 46, 60);
        private static readonly Color _panel = Color.Argb(70, 26, 31, 41);
        private static readonly Color _accent = Color.Argb(210, 41, 197, 255);
        private static readonly Color _accentFill = Color.Argb(90, 41, 197, 255);
        private static readonly Color _label = Color.Argb(190, 138, 147, 166);

        public TouchOverlayView(Context context, TouchControls controls) : base(context)
        {
            _controls = controls;
            // FIRE becoming SCAN is decided by the game thread, not by a
            // touch, so it has to ask for the repaint. PostInvalidate is the
            // one that may be called from off the UI thread.
            controls.Invalidated = PostInvalidate;
            SetWillNotDraw(false);
            _stroke.SetStyle(Paint.Style.Stroke);
            _fill.SetStyle(Paint.Style.Fill);
            _text.SetStyle(Paint.Style.Fill);
            _text.TextAlign = Paint.Align.Center;
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            _controls.Layout(w, h, Resources?.DisplayMetrics?.Density ?? 1f);
            Invalidate();
        }

        /// <summary>
        /// Lay the controls out again for the size this view already has, and
        /// repaint.
        ///
        /// For the rotation that does not resize anything -- see
        /// <c>MainActivity.OnDisplayChanged</c>. <see cref="OnSizeChanged"/>
        /// is the only other thing that calls <c>Layout</c>, and it does not
        /// fire when a phone is turned end for end.
        /// </summary>
        public void Refresh()
        {
            if (Width > 0 && Height > 0)
            {
                _controls.Layout(Width, Height, Resources?.DisplayMetrics?.Density ?? 1f);
            }
            RequestLayout();
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            if (AndroidUiSurface.Current?.Visible == true)
            {
                // A screen is drawn into the frame under this one and every
                // touch belongs to it -- see OnTouchEvent. Buttons a finger
                // cannot reach are worse than no buttons.
                return;
            }
            if (_controls.PadDriving)
            {
                // A pad is being held: nothing is drawn, and the view stays
                // where it is rather than going away. It is still the only
                // thing receiving touches, and the first one brings the
                // controls back -- see TouchControls.PointerDown.
                return;
            }
            float unit = Math.Max(1f, Height / 100f);
            _stroke.StrokeWidth = Math.Max(2f, unit * 0.22f);
            foreach (TouchButton button in _controls.Buttons)
            {
                if (!button.Visible)
                {
                    continue;
                }
                bool held = _controls.IsHeld(button.Action);
                _fill.Color = held ? _accentFill : _panel;
                _stroke.Color = held ? _accent : _edge;
                canvas.DrawCircle(button.CentreX, button.CentreY, button.Radius, _fill);
                canvas.DrawCircle(button.CentreX, button.CentreY, button.Radius, _stroke);
                _text.Color = held ? _accent : _label;
                _text.TextSize = button.Radius * 0.42f;
                canvas.DrawText(button.Label, button.CentreX,
                    button.CentreY + _text.TextSize * 0.35f, _text);
            }
            if (_controls.StickActive)
            {
                _stroke.Color = _edge;
                _fill.Color = _panel;
                canvas.DrawCircle(_controls.StickX, _controls.StickY, _controls.StickRadius, _fill);
                canvas.DrawCircle(_controls.StickX, _controls.StickY, _controls.StickRadius, _stroke);
                _fill.Color = _accentFill;
                _stroke.Color = _accent;
                canvas.DrawCircle(_controls.StickKnobX, _controls.StickKnobY,
                    _controls.StickKnobRadius, _fill);
                canvas.DrawCircle(_controls.StickKnobX, _controls.StickKnobY,
                    _controls.StickKnobRadius, _stroke);
            }
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null)
            {
                return false;
            }
            if (AndroidUiSurface.Current is AndroidUiSurface surface && surface.Visible)
            {
                // The results panel is the one moment in a match when nobody
                // is aiming, so while it is up every touch is the screen's.
                // The desktop says the same thing by releasing the cursor for
                // as long as the panel is drawn.
                return HandUp(surface, e);
            }
            switch (e.ActionMasked)
            {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                {
                    int index = e.ActionIndex;
                    _controls.PointerDown(e.GetPointerId(index), e.GetX(index), e.GetY(index));
                }
                break;
            case MotionEventActions.Move:
                for (int i = 0; i < e.PointerCount; i++)
                {
                    _controls.PointerMove(e.GetPointerId(i), e.GetX(i), e.GetY(i));
                }
                break;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                _controls.PointerUp(e.GetPointerId(e.ActionIndex));
                break;
            case MotionEventActions.Cancel:
                _controls.ReleaseEverything();
                break;
            default:
                return false;
            }
            Invalidate();
            return true;
        }

        /// <summary>
        /// One finger, as the pointer the toolkit would have been given by a
        /// windowing system. The first only: these screens are a menu, and a
        /// second finger on a menu is a finger resting on the glass.
        /// </summary>
        private static bool HandUp(AndroidUiSurface surface, MotionEvent e)
        {
            if (e.ActionMasked == MotionEventActions.PointerDown
                || e.ActionMasked == MotionEventActions.PointerUp)
            {
                return true;
            }
            float x = e.GetX(0);
            float y = e.GetY(0);
            switch (e.ActionMasked)
            {
            case MotionEventActions.Down:
                // No preparatory move: a touch contact carries its own
                // position and TouchBegin is the first thing Avalonia may
                // hear about this id. An update ahead of it is an update for
                // a contact that does not exist yet, and the press that
                // followed landed wherever that resolved to.
                surface.TouchDown(x, y);
                break;
            case MotionEventActions.Move:
                surface.TouchMove(x, y);
                break;
            case MotionEventActions.Up:
                surface.TouchUp(x, y);
                break;
            case MotionEventActions.Cancel:
                surface.TouchUp(x, y);
                break;
            default:
                return false;
            }
            return true;
        }
    }
}
