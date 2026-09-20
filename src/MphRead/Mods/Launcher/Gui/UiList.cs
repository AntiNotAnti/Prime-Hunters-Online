using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// One line of a list: what it is on the left, what is worth knowing about
    /// it on the right.
    ///
    /// Deliberately not a tile. The map grid drew every room as a picture,
    /// which is the right answer to "which map is that" and the wrong one to
    /// "how many maps are there" -- twenty-seven pictures is a page to scroll
    /// and a column of names is a glance. The picture has not gone: it is what
    /// the chosen line shows beside the list.
    /// </summary>
    internal sealed class UiListRow : Control
    {
        public event EventHandler? Clicked;

        /// <summary>
        /// Chosen rather than merely pointed at: Enter, Space, or a second
        /// click on the row that is already selected.
        ///
        /// Separate from <see cref="Clicked"/> on purpose. A single click
        /// selects -- it fills the preview in beside the list and, for a
        /// server, the address box -- and starting the match is the tick in
        /// the corner. Pressing a row *was* the whole choice, which put a
        /// player in a match they had only meant to look at.
        /// </summary>
        public event EventHandler? Activated;

        /// <summary>What choosing this line means, for the screen holding the list.</summary>
        public object? Choice { get; init; }

        private readonly string _title;
        private string _detail;
        private bool _hot;
        private bool _selected;

        /// <summary>
        /// This is the row the list is talking about.
        ///
        /// Set by <see cref="UiList"/>, and drawn exactly like hover and focus
        /// are. It has to be its own thing because the other two are not
        /// available on a touchscreen: a finger hovers nothing, and a tap does
        /// not reliably leave focus behind, so on a phone the selected row was
        /// drawn like every other row while the tick at the foot and the
        /// details beside the list were all about it. The list always has a
        /// selection -- the first row to arrive takes it -- so there is always
        /// exactly one row lit.
        /// </summary>
        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }
                _selected = value;
                InvalidateVisual();
            }
        }

        private readonly Tap _tap = new();

        public UiListRow(string title, string detail = "")
        {
            _title = title;
            _detail = detail;
            Height = 30;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            // A second click on the same row means "this one, go": the
            // shortcut for somebody who already knows which line they want,
            // and the gesture every list in every file manager has.
            DoubleTapped += (_, _) =>
            {
                Clicked?.Invoke(this, EventArgs.Empty);
                Activated?.Invoke(this, EventArgs.Empty);
            };
        }

        public string Detail
        {
            get => _detail;
            set
            {
                _detail = value;
                InvalidateVisual();
            }
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _hot = true;
            InvalidateVisual();
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _hot = false;
            _tap.Cancel();
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
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
            // A release is not a choice on its own: a list is the thing most
            // likely to be scrolled, and a flick that ends over a row used to
            // pick it. See Tap -- the press has to have landed here and stayed.
            if (_tap.Release(e, this))
            {
                Focus();
                Clicked?.Invoke(this, EventArgs.Empty);
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            _tap.Cancel();
            base.OnPointerCaptureLost(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                Clicked?.Invoke(this, EventArgs.Empty);
                Activated?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }



        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            InvalidateVisual();
            base.OnLostFocus(e);
        }

        // The two pieces of text this row draws, laid out once and kept.
        //
        // Building a FormattedText shapes and lays out the string -- the
        // expensive half of drawing text -- and this row was doing it twice
        // every time it was painted, for a title and a detail that had not
        // changed since the list was built. Scrolling repaints every visible
        // row every frame, so a fourteen-row list was shaping twenty-eight
        // strings a frame, several thousand a second, all of them the same
        // strings as the frame before.
        //
        // Nothing here depends on time, so the cache only has to notice the
        // four things that do change: the row's width, its detail text, and
        // whether it is lit (which recolours the title).
        private FormattedText? _titleText;
        private FormattedText? _detailText;
        private double _laidOutAt = -1;
        private bool _laidOutLit;
        private string _laidOutDetail = "";

        private void LayOut(bool lit)
        {
            if (_titleText != null && _laidOutAt == Bounds.Width
                && _laidOutLit == lit && _laidOutDetail == _detail)
            {
                return;
            }
            _laidOutAt = Bounds.Width;
            _laidOutLit = lit;
            _laidOutDetail = _detail;
            double right = Bounds.Width;
            if (_detail.Length > 0)
            {
                _detailText = new FormattedText(_detail, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, GuiTheme.Face(bold: false), 12,
                    GuiTheme.TextDimBrush)
                {
                    MaxTextWidth = Math.Max(40, Bounds.Width * 0.45),
                    MaxTextHeight = 20,
                    Trimming = TextTrimming.CharacterEllipsis
                };
                right = Bounds.Width - _detailText.Width - 14;
            }
            else
            {
                _detailText = null;
            }
            _titleText = new FormattedText(_title, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GuiTheme.Face(bold: true), 14,
                new SolidColorBrush(lit ? GuiTheme.Accent : GuiTheme.Text))
            {
                MaxTextWidth = Math.Max(40, right - 14),
                MaxTextHeight = 22,
                Trimming = TextTrimming.CharacterEllipsis
            };
        }

        public override void Render(DrawingContext context)
        {
            var full = new Rect(0, 0, Bounds.Width, Bounds.Height);
            context.FillRectangle(Brushes.Transparent, full);
            bool lit = _hot || IsFocused || _selected;
            if (lit)
            {
                // A caret rather than a fill: the list sits on a photograph,
                // and a row of panel colour over it is a box -- which is the
                // one thing none of these screens has any more.
                context.FillRectangle(GuiTheme.AccentBrush,
                    new Rect(0, 6, 3, Bounds.Height - 12));
            }
            LayOut(lit);
            if (_detailText != null)
            {
                context.DrawText(_detailText,
                    new Point(Bounds.Width - _detailText.Width - 4,
                        (Bounds.Height - _detailText.Height) / 2));
            }
            context.DrawText(_titleText!,
                new Point(14, (Bounds.Height - _titleText!.Height) / 2));
        }
    }

    /// <summary>
    /// The one list in the launcher.
    ///
    /// Servers, maps, save slots, recordings and the maps a vote can be called
    /// on were five lists with five layouts, four of which were built once and
    /// never looked at again. This is the column all five are shown in: rows
    /// are whatever control suits them -- <see cref="UiListRow"/> for a name,
    /// <see cref="ServerRow"/> for five columns that have to line up -- and
    /// what belongs here is only the part they share, which is the scrolling
    /// and the up and down keys.
    /// </summary>
    internal sealed class UiList : Decorator
    {
        /// <summary>
        /// The rows, with room either side for the ring a selected one wears.
        ///
        /// `box-shadow: 0 0 0 2px` spreads *outward* from the box, and the
        /// scroller above clips to its own width (it has to: horizontal
        /// scrolling is disabled). A row stretched to that width therefore had
        /// the left and right of its accent ring cut off while the top and
        /// bottom survived in the gap between rows -- which is exactly what
        /// "le surlignage jaune est croppé sur les côtés" was. Three points is
        /// the two the ring spreads plus one for the rounding.
        /// </summary>
        private readonly StackPanel _rows = new()
        {
            Spacing = 1,
            Margin = new Thickness(3, 0, 3, 0)
        };
        private readonly Panel _header = new();
        private readonly ScrollViewer _scroll;
        private readonly List<Control> _focusable = new();

        /// <summary>The row the keyboard is on, or the last one pressed.</summary>
        public Control? Selected { get; private set; }

        /// <summary>
        /// The gap between rows, in frame ems, or zero to keep the flat one
        /// point a plain list uses.
        ///
        /// The browser sets `.32em`. A row's own solid edge is drawn *into*
        /// this gap rather than inside the row's box, the way a box-shadow
        /// falls, so the pitch is the slab plus this and nothing else.
        /// </summary>
        public double SpacingEms { get; set; }

        /// <summary>
        /// Whether the first row to arrive becomes the selection.
        ///
        /// True for a list of things you are choosing between, where starting
        /// somewhere is better than starting nowhere. False for the server
        /// browser, where the first row is an accident of who replied first.
        /// </summary>
        public bool AutoSelectFirst { get; set; } = true;

        /// <summary>Raised when a row is pressed or Enter is taken on it.</summary>
        public event EventHandler<Control>? Activated;

        /// <summary>
        /// Raised when the keyboard merely lands on a different row. What a
        /// screen showing a picture of the chosen thing listens to -- the
        /// picture should follow the arrow keys, not wait for a press.
        /// </summary>
        public event EventHandler<Control>? SelectionChanged;

        public UiList()
        {
            _scroll = new ScrollViewer
            {
                Content = _rows,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            // `scrollbar-width: thin; scrollbar-color: var(--edge) transparent`.
            // Six points of the panel's own edge colour on nothing, rather
            // than the Fluent theme's pale grey rail -- which is the one
            // control on these screens that still said "toolkit" out loud.
            Styles.Add(new Style(x => x.OfType<ScrollBar>().Class("vertical"))
            {
                Setters =
                {
                    new Setter(ScrollBar.WidthProperty, 6.0),
                    new Setter(ScrollBar.MinWidthProperty, 6.0),
                    new Setter(ScrollBar.BackgroundProperty, Brushes.Transparent)
                }
            });
            Styles.Add(new Style(x => x.OfType<ScrollBar>().Template().OfType<Thumb>())
            {
                Setters =
                {
                    new Setter(Thumb.BackgroundProperty, GuiTheme.EdgeBrush),
                    new Setter(Thumb.CornerRadiusProperty, new CornerRadius(3)),
                    new Setter(Thumb.MinWidthProperty, 6.0)
                }
            });
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_header, Dock.Top);
            dock.Children.Add(_header);
            dock.Children.Add(_scroll);
            // A Decorator rather than a Panel: Panel seals Render, and this
            // has wanted a hook there before. The list only ever had one
            // child anyway.
            Child = dock;
        }

        /// <summary>
        /// Column headings, outside the scroller so they stay put while it
        /// scrolls -- which is the whole point of having them.
        /// </summary>
        public void SetHeader(Control? header)
        {
            _header.Children.Clear();
            if (header != null)
            {
                _header.Children.Add(header);
            }
        }

        public void Clear()
        {
            _rows.Children.Clear();
            _focusable.Clear();
            Selected = null;
        }

        /// <summary>
        /// Add a row and say what pressing it means. The row keeps its own
        /// drawing and its own click; what is added here is that pressing it
        /// also makes it the selection, so the tick in the corner and the
        /// options beside the list are talking about the same line.
        /// </summary>
        public void Add(Control row, Action<Control>? activate = null)
        {
            _rows.Children.Add(row);
            if (!row.Focusable)
            {
                return;
            }
            _focusable.Add(row);
            if (Selected == null && AutoSelectFirst)
            {
                // The first row to arrive is the selection, and it has to take
                // the selection's side effect with it. It did not: `activate`
                // only ran from a press, so the browser opened with the top
                // server highlighted and the address box still holding
                // whatever was last typed -- you would read one server's map
                // and its ping, press JOIN, and connect to another. Invisible
                // until the preview started following the selection, and
                // wrong before that.
                Selected = row;
                MarkSelection();
                activate?.Invoke(row);
                SelectionChanged?.Invoke(this, row);
            }
            row.GotFocus += (_, _) => Select(row);
            if (row is UiListRow line)
            {
                line.Clicked += (_, _) => Fire(row, activate, activated: false);
                line.Activated += (_, _) => Fire(row, activate, activated: true);
            }
            else if (row is ServerRow server)
            {
                server.Clicked += (_, _) => Fire(row, activate, activated: false);
                server.Activated += (_, _) => Fire(row, activate, activated: true);
            }
        }

        /// <summary>A line that is not a choice: an empty list saying so.</summary>
        public void AddNote(string text, Color? colour = null)
        {
            _rows.Children.Add(new Note(text, colour));
        }

        /// <summary>
        /// A row was pressed. Selecting it and *choosing* it are two different
        /// events now: a click does the first, Enter or a second click does
        /// both. <paramref name="activate"/> is the row's own side effect --
        /// filling the address box in for a server -- and belongs to
        /// selecting, since it is what makes the choice visible before it is
        /// taken.
        /// </summary>
        private void Fire(Control row, Action<Control>? activate, bool activated)
        {
            Select(row);
            activate?.Invoke(row);
            if (activated)
            {
                Activated?.Invoke(this, row);
            }
        }

        private void Select(Control row)
        {
            if (ReferenceEquals(Selected, row))
            {
                return;
            }
            Selected = row;
            MarkSelection();
            SelectionChanged?.Invoke(this, row);
        }

        /// <summary>
        /// Tell the rows which of them is the selection.
        ///
        /// The rows used to work it out for themselves, out of hover and
        /// focus, and that is right on a desktop and wrong everywhere else: a
        /// touchscreen has no hover at all and a tap does not reliably leave
        /// focus behind, so on a phone nothing was lit and there was no way to
        /// see which server the address box and JOIN were about. Pushed rather
        /// than pulled because the list is the only thing that knows.
        /// </summary>
        private void MarkSelection()
        {
            for (int i = 0; i < _focusable.Count; i++)
            {
                Control row = _focusable[i];
                bool on = ReferenceEquals(row, Selected);
                if (row is UiListRow line)
                {
                    line.IsSelected = on;
                }
                else if (row is ServerRow server)
                {
                    server.IsSelected = on;
                }
            }
        }

        /// <summary>
        /// Up and down, wherever they were pressed on the screen.
        ///
        /// Offered to the host rather than handled here for the same reason
        /// <see cref="UiTabs.HandleKey"/> is: the keyboard may be on a setting
        /// beside the list, and up and down should still walk the list --
        /// which is the thing the screen is for.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            if (SpacingEms > 0)
            {
                // Not rounded: a gap of 3.63 against one of 4 is three
                // quarters of a point per row, and a list is where that
                // compounds.
                // And no layout rounding on the column. Avalonia rounds every
                // arranged box to a whole point by default, which turns a
                // 29.6-point row and a 3.63-point gap into 30 and 4 -- eight
                // tenths of a point per row, and over thirteen rows the ten
                // points that had a phone's panel sitting too high. The rows
                // still *draw* on whole pixels; it is only where they are that
                // is allowed a fraction, which is what the layout this is a
                // port of does.
                _rows.UseLayoutRounding = false;
                double gap = Deck.GetEm(this) * SpacingEms;
                if (Math.Abs(gap - _rows.Spacing) > 0.01)
                {
                    _rows.Spacing = gap;
                }
            }
            return base.MeasureOverride(availableSize);
        }

        public bool HandleKey(Key key)
        {
            if (_focusable.Count == 0)
            {
                return false;
            }
            int step = key == Key.Down ? 1 : key == Key.Up ? -1 : 0;
            if (step == 0)
            {
                return false;
            }
            int at = Selected == null ? -1 : _focusable.IndexOf(Selected);
            int next = at < 0 ? (step > 0 ? 0 : _focusable.Count - 1)
                : Math.Clamp(at + step, 0, _focusable.Count - 1);
            Focus(_focusable[next]);
            return true;
        }

        /// <summary>Put the keyboard on a row, and scroll it into view.</summary>
        public void Focus(Control row)
        {
            Select(row);
            row.Focus();
            row.BringIntoView();
        }

        /// <summary>The row the list opens on, once the tree exists to focus in.</summary>
        public void FocusFirst()
        {
            if (_focusable.Count == 0)
            {
                return;
            }
            Control row = Selected != null && _focusable.Contains(Selected)
                ? Selected : _focusable[0];
            Dispatcher.UIThread.Post(() => Focus(row), DispatcherPriority.Background);
        }

        /// <summary>Choose by what a row stands for, rather than by which control it is.</summary>
        public void SelectTag(object? choice)
        {
            if (choice == null)
            {
                return;
            }
            foreach (Control row in _focusable)
            {
                if (row is UiListRow line && Equals(line.Choice, choice))
                {
                    Select(row);
                    return;
                }
            }
        }
    }
}
