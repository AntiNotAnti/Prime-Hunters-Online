#if MPHREAD_AVALONIA
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// A box you type in, sunk into the panel: the reference's <c>.field</c>.
    ///
    /// <para>
    /// A well, not a card. Everything else on these screens sticks <i>up</i>
    /// off the panel -- a face on a solid edge, lit along its top -- and a
    /// field is the one control that goes the other way: a two-point bar of
    /// shadow along the inside of its top edge and a hairline all the way
    /// round. That inversion is the whole of how a screen says "this one takes
    /// something from you" without a label saying so.
    /// </para>
    ///
    /// <para>
    /// The editing is Avalonia's <see cref="TextBox"/>, because writing a
    /// caret, a selection, an IME and a clipboard from scratch to match a
    /// rectangle is not a trade anybody should take. What is replaced is
    /// everything the Fluent theme paints: the template's own background is
    /// pushed to transparent in <i>every</i> state -- pointer-over and focused
    /// included, since those are separate setters and leaving one behind is a
    /// field that turns pale grey when the mouse crosses it -- and the well
    /// under it is drawn here.
    /// </para>
    ///
    /// <para>
    /// In the body face, and that is not a detail: an address is read
    /// character by character, and <c>192.168.1.42</c> set in a pixel display
    /// face is a row of identical smudges.
    /// </para>
    /// </summary>
    internal sealed class DeckField : Decorator
    {
        public TextBox Box { get; }

        public string Value
        {
            get => Box.Text ?? "";
            set => Box.Text = value;
        }

        /// <summary>Font size, in frame ems. The reference's <c>.95em</c>.</summary>
        private const double SizeEms = 0.95;

        /// <summary>Padding, in this field's own ems.</summary>
        private const double PadXEms = 0.7, PadYEms = 0.4;

        private const double RadiusEms = 0.35;

        /// <summary>Its natural width, in its own ems, or 0 to take what it is given.</summary>
        private readonly double _widthEms;

        public DeckField(string value, double widthEms = 11, string watermark = "")
        {
            _widthEms = widthEms;
            Box = new TextBox
            {
                Text = value,
                PlaceholderText = watermark,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = GuiTheme.TextBrush,
                CaretBrush = GuiTheme.AccentBrush,
                SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 255, 179, 71)),
                FontFamily = Deck.Mono,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                MinHeight = 0,
                MinWidth = 0
            };
            // The Fluent theme paints the box from its own resources in four
            // states and a Background set on the control only wins in one of
            // them. These are the other three.
            foreach (string state in new[] { "", ":pointerover", ":focus", ":focus-within" })
            {
                Styles.Add(new Style(x => state.Length == 0
                    ? x.OfType<TextBox>().Template().OfType<Border>()
                    : x.OfType<TextBox>().Class(state[1..]).Template().OfType<Border>())
                {
                    Setters =
                    {
                        new Setter(Border.BackgroundProperty, Brushes.Transparent),
                        new Setter(Border.BorderThicknessProperty, new Thickness(0))
                    }
                });
            }
            Child = Box;
            Avalonia.Media.RenderOptions.SetEdgeMode(this, EdgeMode.Antialias);
        }

        private double Size => Deck.GetEm(this) * SizeEms;

        protected override Size MeasureOverride(Size availableSize)
        {
            double size = Size;
            Box.FontSize = size;
            double padX = Math.Round(size * PadXEms);
            double padY = Math.Round(size * PadYEms);
            var want = new Thickness(padX, padY, padX, padY);
            if (Padding != want)
            {
                Padding = want;
            }
            Size measured = base.MeasureOverride(availableSize);
            double width = _widthEms > 0
                ? Math.Min(availableSize.Width, Math.Round(size * _widthEms))
                : measured.Width;
            return new Size(width, measured.Height);
        }

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0)
            {
                return;
            }
            double radius = Size * RadiusEms;
            var box = new RoundedRect(new Rect(0, 0, w, h), radius);

            // `box-shadow: inset 0 2px 0 rgba(0,0,0,.4), inset 0 0 0 1px
            // var(--edge)` -- both of them inset, which is the whole of why
            // this control reads as a hole and every other one reads as a
            // slab. Avalonia has inset shadows, so the two declarations go
            // across as they are written; the first used to be a clipped
            // two-point rectangle drawn along the top, which is the same
            // picture on a square corner and a wrong one on a round.
            //
            // Focused, the hairline becomes the accent at two points --
            // `outline: 2px solid var(--accent)`, which on a box this size is
            // the same object as the ring.
            bool hot = Box.IsFocused;
            var shadows = new BoxShadows(
                Deck.Shadow(0, 2, 0, 0, Deck.Fade(0, 0.4), inset: true),
                new[]
                {
                    Deck.Shadow(0, 0, 0, hot ? 2 : 1,
                        hot ? GuiTheme.Accent : GuiTheme.Edge, inset: true)
                });
            context.DrawRectangle(new SolidColorBrush(GuiTheme.PanelLight), null,
                box, shadows);
        }
    }
}
#endif
