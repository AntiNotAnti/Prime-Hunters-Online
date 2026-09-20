using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>A small upper-case heading over a group of rows.</summary>
    internal sealed class Caption : Control
    {
        private readonly string _text;

        public Caption(string text)
        {
            _text = text;
            Height = 26;
        }

        /// <summary>
        /// Its own width when nothing constrains it, for the same reason
        /// <see cref="UiWord.MeasureOverride"/> has one: in a row, a
        /// control that measures to nothing is drawn on top of its neighbours.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            Size size = base.MeasureOverride(availableSize);
            return new Size(Math.Min(Label().Width + 8, availableSize.Width), size.Height);
        }

        private FormattedText Label()
        {
            return new FormattedText(_text.ToUpperInvariant(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(bold: true), 11,
                GuiTheme.TextDimBrush);
        }

        public override void Render(DrawingContext context)
        {
            FormattedText text = Label();
            context.DrawText(text, new Point(0, Bounds.Height - text.Height - 4));
            double y = Bounds.Height - 2;
            context.DrawLine(new Pen(GuiTheme.EdgeBrush, 1),
                new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    /// <summary>
    /// One setting with a fixed set of answers: a label, the current answer,
    /// and an arrow on each side.
    ///
    /// Cycling rather than a drop-down because every list here is short and a
    /// combo box is the one control WinForms would not draw dark -- which is
    /// the reason the original exists. Keeping the same shape here is not
    /// obligation but consistency: the two screens are one product.
    /// </summary>
    internal sealed class ChoiceRow : Control
    {
        private readonly string _label;
        private IReadOnlyList<string> _options;
        private int _index;
        private bool _leftHot;
        private bool _rightHot;
        private readonly Tap _tap = new();

        public event EventHandler? Changed;

        public int Index
        {
            get => _index;
            set
            {
                int clamped = _options.Count == 0 ? 0 : Math.Clamp(value, 0, _options.Count - 1);
                if (clamped != _index)
                {
                    _index = clamped;
                    InvalidateVisual();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string Value => _options.Count == 0 ? "" : _options[_index];

        public ChoiceRow(string label, IReadOnlyList<string> options, int index = 0)
        {
            _label = label;
            _options = options;
            _index = options.Count == 0 ? 0 : Math.Clamp(index, 0, options.Count - 1);
            Height = 34;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        /// <summary>Replace the options in place, e.g. after a room list changes.</summary>
        public void SetItems(IReadOnlyList<string> options, int index = 0)
        {
            _options = options;
            _index = options.Count == 0 ? 0 : Math.Clamp(index, 0, options.Count - 1);
            InvalidateVisual();
        }

        /// <summary>
        /// Width of the arrow buttons, and of the column the value is drawn
        /// in between them.
        /// </summary>
        private const double ArrowWidth = 28;
        private const double ValueColumn = 180;

        /// <summary>
        /// Drawn in a square at the right-hand end of the row, past the
        /// forward arrow, when the answer is a thing better shown than named
        /// -- a crosshair, at the size and shape the row has just picked.
        /// Everything else in the row shifts left to make room for it.
        /// </summary>
        public Action<DrawingContext, Rect>? Preview
        {
            get => _preview;
            set
            {
                _preview = value;
                // Room for the picture. A crosshair at its largest is 36
                // points across, and a row of the ordinary height cannot show
                // that without shrinking it -- which would defeat a preview
                // whose job is partly to answer "how big is Big".
                Height = value == null ? 34 : 48;
                InvalidateVisual();
            }
        }

        private Action<DrawingContext, Rect>? _preview;

        private const double PreviewWidth = 52;

        private double PreviewRoom => _preview == null ? 0 : PreviewWidth;

        /// <summary>
        /// Both arrows sit still.
        ///
        /// The back arrow used to be placed from the width of the *value* --
        /// it slid left to make room for a long room name and back again for a
        /// short one. Which meant it moved as you stepped: the button jumped
        /// out from under the pointer between one map and the next, so a
        /// second click landed on the row instead of the arrow and stepped
        /// forward again. Picking a map by clicking became a thing you had to
        /// re-aim for every time.
        ///
        /// So the value gets a column of its own, of a fixed width, and is
        /// trimmed to it. The arrows never move, and the row is still one
        /// height on every card.
        /// </summary>
        private Rect LeftArrow
        {
            get
            {
                // The label keeps 110 points where there are 110 to spare and
                // a little under half the row where there are not. A flat
                // floor is what put the arrows 138 points apart inside the
                // play screen's drawer, which left the value about thirty
                // points of column -- and a value that does not fit does not
                // ellipsize, it *wraps*, so "Normal" came out as three
                // stacked syllables.
                double floor = Math.Min(110, Bounds.Width * 0.42);
                double x = Bounds.Width - PreviewRoom - ArrowWidth - ValueColumn - ArrowWidth;
                // Never over the label, on a card too narrow for the column.
                return new Rect(Math.Max(floor, x), 0, ArrowWidth, Bounds.Height);
            }
        }

        private Rect RightArrow =>
            new(Bounds.Width - PreviewRoom - ArrowWidth, 0, ArrowWidth, Bounds.Height);

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            Point p = e.GetPosition(this);
            bool left = LeftArrow.Contains(p);
            bool right = RightArrow.Contains(p);
            if (left != _leftHot || right != _rightHot)
            {
                _leftHot = left;
                _rightHot = right;
                InvalidateVisual();
            }
            // A finger on its way down the page is not answering this row.
            _tap.Moved(e, this);
            base.OnPointerMoved(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _leftHot = _rightHot = false;
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            // The press decides nothing: see Tap. This row used to step here,
            // which meant scrolling the settings page cycled every row the
            // drag began on.
            _tap.Press(e, this);
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_tap.Release(e, this))
            {
                // Anywhere that is not the back arrow steps forward, so the row
                // can be poked at without aiming.
                Step(LeftArrow.Contains(e.GetPosition(this)) ? -1 : 1);
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            // The scroll gesture above has taken the pointer.
            _tap.Cancel();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Left)
            {
                Step(-1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right || e.Key == Key.Enter || e.Key == Key.Space)
            {
                Step(1);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private void Step(int direction)
        {
            if (_options.Count == 0)
            {
                return;
            }
            // Wrapping, because the lists are short and running off the end of
            // one is more annoying than useful.
            _index = (_index + direction + _options.Count) % _options.Count;
            InvalidateVisual();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public override void Render(DrawingContext context)
        {
            // See UiWord.Render: hit testing follows the drawing.
            context.FillRectangle(Brushes.Transparent,
                new Rect(0, 0, Bounds.Width, Bounds.Height));
            if (IsFocused)
            {
                context.FillRectangle(GuiTheme.PanelLightBrush,
                    new Rect(0, 0, Bounds.Width, Bounds.Height), 4);
            }
            var label = new FormattedText(_label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(false), 13, GuiTheme.TextDimBrush);
            context.DrawText(label, new Point(4, (Bounds.Height - label.Height) / 2));

            // The value lives in the fixed column between the arrows, and is
            // trimmed to it rather than pushing them apart.
            var value = new FormattedText(Value, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(true), 13, GuiTheme.TextBrush);
            Rect left = LeftArrow;
            double room = RightArrow.X - left.Right - 8;
            if (value.Width > room)
            {
                value.MaxTextWidth = Math.Max(20, room);
                // One line, whatever the trimming decides. Without a height
                // limit Avalonia wraps at the first space rather than
                // ellipsizing -- see the same note in DeckText.Lay -- and a
                // wrapped value in a fixed-height row draws over the rows
                // either side of it.
                value.MaxTextHeight = 13 * 1.9;
                value.Trimming = TextTrimming.CharacterEllipsis;
            }
            double centre = (left.Right + RightArrow.X) / 2;
            context.DrawText(value, new Point(centre - value.Width / 2,
                (Bounds.Height - value.Height) / 2));

            Arrow(context, left, pointsLeft: true, _leftHot);
            Arrow(context, RightArrow, pointsLeft: false, _rightHot);
            if (_preview != null)
            {
                const double inset = 3;
                _preview(context, new Rect(Bounds.Width - PreviewWidth + inset, inset,
                    PreviewWidth - inset * 2, Bounds.Height - inset * 2));
            }
        }

        private static void Arrow(DrawingContext context, Rect area, bool pointsLeft, bool hot)
        {
            double cx = area.X + area.Width / 2;
            double cy = area.Y + area.Height / 2;
            const double w = 4.5;
            const double h = 6;
            var geometry = new StreamGeometry();
            using (StreamGeometryContext sink = geometry.Open())
            {
                if (pointsLeft)
                {
                    sink.BeginFigure(new Point(cx + w, cy - h), true);
                    sink.LineTo(new Point(cx - w, cy));
                    sink.LineTo(new Point(cx + w, cy + h));
                }
                else
                {
                    sink.BeginFigure(new Point(cx - w, cy - h), true);
                    sink.LineTo(new Point(cx + w, cy));
                    sink.LineTo(new Point(cx - w, cy + h));
                }
                sink.EndFigure(true);
            }
            context.DrawGeometry(hot ? GuiTheme.AccentBrush : GuiTheme.TextDimBrush,
                null, geometry);
        }
    }

    /// <summary>One setting that is on or off.</summary>
    internal sealed class ToggleRow : Control
    {
        private readonly string _label;
        private bool _on;
        private readonly Tap _tap = new();

        public event EventHandler? Changed;

        public bool On
        {
            get => _on;
            set
            {
                if (_on != value)
                {
                    _on = value;
                    InvalidateVisual();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public ToggleRow(string label, bool on)
        {
            _label = label;
            _on = on;
            Height = 34;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            // Not On = !On: a toggle flipped by the press is a toggle flipped
            // by every scroll that starts on it, and there is no taking it
            // back once it has happened. See Tap.
            _tap.Press(e, this);
            base.OnPointerPressed(e);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            _tap.Moved(e, this);
            base.OnPointerMoved(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_tap.Release(e, this))
            {
                On = !On;
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _tap.Cancel();
            base.OnPointerExited(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space
                || e.Key == Key.Left || e.Key == Key.Right)
            {
                On = !On;
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public override void Render(DrawingContext context)
        {
            // See UiWord.Render: hit testing follows the drawing.
            context.FillRectangle(Brushes.Transparent,
                new Rect(0, 0, Bounds.Width, Bounds.Height));
            if (IsFocused)
            {
                context.FillRectangle(GuiTheme.PanelLightBrush,
                    new Rect(0, 0, Bounds.Width, Bounds.Height), 4);
            }
            var label = new FormattedText(_label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(false), 13, GuiTheme.TextDimBrush);
            context.DrawText(label, new Point(4, (Bounds.Height - label.Height) / 2));

            const double w = 40;
            const double h = 20;
            var track = new Rect(Bounds.Width - w - 4, (Bounds.Height - h) / 2, w, h);
            context.DrawRectangle(
                new SolidColorBrush(_on ? GuiTheme.Accent : GuiTheme.Edge), null,
                new RoundedRect(track, h / 2));
            double knob = _on ? track.Right - h / 2 : track.X + h / 2;
            context.DrawEllipse(new SolidColorBrush(_on ? GuiTheme.Ink : GuiTheme.TextDim),
                null, new Point(knob, track.Y + h / 2), h / 2 - 3, h / 2 - 3);
        }
    }

    /// <summary>
    /// A labelled two-button boolean choice.
    ///
    /// Unlike <see cref="ToggleRow"/>, both answers are visible at once. Lobby
    /// rules use this form because seven switches in a configuration panel are
    /// faster to scan as explicit OFF / ON decisions than as tiny tracks.
    /// </summary>
    internal sealed class ButtonToggleRow : Grid
    {
        private readonly DeckButton _off;
        private readonly DeckButton _onButton;
        private bool _on;

        public event EventHandler? Changed;

        public bool On
        {
            get => _on;
            set
            {
                if (_on == value)
                {
                    return;
                }
                _on = value;
                Mark();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public ButtonToggleRow(string label, bool on = false)
        {
            _on = on;
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto");
            ColumnSpacing = 5;
            MinHeight = 32;

            var caption = new TextBlock
            {
                Text = label,
                FontFamily = GuiTheme.Display,
                FontSize = 12,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 8, 0)
            };
            Children.Add(caption);

            _off = new DeckButton("OFF", Deck.Face.Slate,
                sizeEms: 0.72, padXEms: 0.62, padYEms: 0.26, lip: 3);
            _onButton = new DeckButton("ON", Deck.Face.Slate,
                sizeEms: 0.72, padXEms: 0.72, padYEms: 0.26, lip: 3);
            _off.Click += (_, _) => On = false;
            _onButton.Click += (_, _) => On = true;

            Grid.SetColumn(_off, 1);
            Grid.SetColumn(_onButton, 2);
            Children.Add(_off);
            Children.Add(_onButton);
            Mark();
        }

        private void Mark()
        {
            _off.Wear(_on ? Deck.Face.Slate : Deck.Face.Rust, selected: !_on);
            _onButton.Wear(_on ? Deck.Face.Moss : Deck.Face.Slate, selected: _on);
        }
    }

    /// <summary>A label and something to type in.</summary>
    internal sealed class FieldRow : Panel
    {
        private readonly TextBlock _caption;
        public TextBox Box { get; }

        public string Label
        {
            get => _caption.Text ?? "";
            set => _caption.Text = value;
        }

        public string Value
        {
            get => Box.Text ?? "";
            set => Box.Text = value;
        }

        /// <param name="compact">
        /// A bare box in a bar rather than a labelled row in a column: 21
        /// points tall, which is what the layout this is a port of gives the
        /// two fields over its server list.
        /// </param>
        public FieldRow(string label, string value, double boxWidth = 150,
            bool compact = false)
        {
            Height = compact ? 21 : 36;
            _caption = new TextBlock
            {
                Text = label,
                FontFamily = GuiTheme.Display,
                FontSize = 13,
                Foreground = GuiTheme.TextDimBrush,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 0, 0)
            };
            // Colours are left to the Fluent dark theme rather than set here:
            // the text box's template paints from its own theme resources and
            // ignores a Background put on the control, so setting one would
            // look like it was doing something while the theme decided.
            Box = new TextBox
            {
                Text = value,
                Width = boxWidth,
                Height = compact ? 21 : Double.NaN,
                MinHeight = compact ? 21 : 0,
                FontFamily = GuiTheme.Display,
                FontSize = compact ? 11 : 13,
                CornerRadius = new CornerRadius(4),
                Padding = compact
                    ? new Thickness(6, 0, 6, 0)
                    : new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = compact
                    ? HorizontalAlignment.Left : HorizontalAlignment.Right
            };
            Children.Add(_caption);
            Children.Add(Box);
        }
    }

    /// <summary>A line of explanation, wrapped, under a group of rows.</summary>
    internal sealed class Note : TextBlock
    {
        /// <summary>How many lines it is held to, or 0 for as many as it takes.</summary>
        private readonly int _lines;

        /// <param name="lines">
        /// Two by default -- see <see cref="MeasureOverride"/>. Zero for a
        /// note that is a paragraph rather than a status line: the setup
        /// screen explains what it is about to do with somebody's cartridge
        /// dump, and two lines of that ended mid-sentence in an ellipsis. Its
        /// own log is the same shape, and was showing two lines of an
        /// extraction inside a box 160 points tall.
        /// </param>
        public Note(string text, Color? color = null, int lines = 2)
        {
            _lines = lines;
            Text = text;
            // The body face, not the display one. This is the one string on
            // the screen that is a *sentence* -- "12 of 13 answered. Click one
            // to pick your hunter and join." -- and a sentence set in a pixel
            // font at eight points is a texture.
            FontFamily = Deck.Mono;
            Foreground = new SolidColorBrush(color ?? GuiTheme.TextDim);
            TextWrapping = TextWrapping.Wrap;
            if (lines > 0)
            {
                MaxLines = lines;
                TextTrimming = TextTrimming.CharacterEllipsis;
            }
            Margin = new Thickness(0);
        }

        /// <summary>
        /// `.76em`, and a fixed `2.3em` of height whatever it says.
        ///
        /// Fixed on purpose: the note changes on every keystroke in the
        /// browser ("asking 13 servers... 4 answered") and a box that grew and
        /// shrank with it would walk the whole foot up and down the panel
        /// while the list was still answering.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            double em = Deck.GetEm(this);
            double size = em * 0.76;
            if (Math.Abs(size - FontSize) > 0.01)
            {
                FontSize = size;
                LineHeight = Math.Round(size * 1.15);
            }
            Size measured = base.MeasureOverride(availableSize);
            if (_lines <= 0)
            {
                return measured;
            }
            double height = Math.Round(size * 1.15 * _lines);
            return new Size(measured.Width, height);
        }
    }
}
