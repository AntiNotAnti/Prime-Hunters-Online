#if MPHREAD_AVALONIA
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MphRead.Mods.Network;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// Everything behind the front screen, laid out six ways: `-uidesign DIR`.
    ///
    /// Nothing here ships. It exists because "what should this look like" is a
    /// question nobody can answer from a description, and answering it the
    /// long way round -- build the layout for real, run it, look at it, throw
    /// it away -- is how the last four went.
    ///
    /// **The front screen is not in here.** It is settled and it stays as it
    /// is. What these pictures are about is every screen reached *from* it:
    /// Play with a list of maps and a list of servers, the settings, and the
    /// pause menu. Those are the screens that actually hold something, and the
    /// question they raise is not where to anchor a column of three words --
    /// it is how a list, a picture of the thing selected, a column of settings
    /// about it and a way to commit share one frame.
    ///
    /// So the six differ in **information architecture**, not in where a menu
    /// is pinned. That is the point: moving anchors around produces six
    /// pictures of the same design and there is nothing to choose between them.
    ///
    /// Four things are held constant, because they are what the choice is
    /// *between* rather than part of it:
    ///
    /// - the photograph. Every style is drawn over
    ///   <see cref="UiLayout.Backdrop"/>, and every pause menu over a real
    ///   room with the scrim on top, because a menu photographed against flat
    ///   black is a menu that looks equally readable in all six.
    /// - the content, in full. Twelve maps, five servers with their real
    ///   columns, fourteen settings rows and all eight pause entries, at their
    ///   real heights. A layout that only works with three rows is not a
    ///   layout.
    /// - the palette and the faces, which are <see cref="GuiTheme"/>'s.
    ///   Changing those is a separate question and mixing the two would make
    ///   both unanswerable.
    /// - the two-press rule: a click selects a row, the tick commits. Every
    ///   style has to have somewhere for that tick to live.
    ///
    /// Style A was what shipped when these were drawn, photographed alongside
    /// the rest so there was something to prefer the others to. **F was
    /// chosen**, and the launcher is built around it now
    /// (<see cref="UiLayout.Page"/>) -- so A is a record of what it replaced
    /// rather than a picture of the program.
    /// </summary>
    internal static class UiDesigns
    {
        /// <summary>
        /// 16:9, and bigger than <c>UiCapture</c>'s. That one is sized to the
        /// game window's own startup shape because it is checking a layout;
        /// this is checking how something looks, and a picture to judge type,
        /// density and spacing by wants the room.
        /// </summary>
        private static readonly Size _size = new Size(1280, 720);

        private static readonly Design[] _designs =
        {
            new CurrentDesign(),
            new SheetDesign(),
            new SplitDesign(),
            new StackDesign(),
            new ShellDesign(),
            new CentreDesign()
        };

        public static int Run(string directory)
        {
            if (!GuiLauncher.EnsureSetup())
            {
                Console.WriteLine("[uidesign] no Avalonia backend on this machine; nothing captured");
                return 1;
            }
            Directory.CreateDirectory(directory);
            int written = 0;
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                foreach (Design design in _designs)
                {
                    Console.WriteLine($"[uidesign] {design.Id} -- {design.Title}");
                    foreach ((string screen, Control view) in design.Screens())
                    {
                        string path = Path.Combine(directory, $"{design.Id}-{screen}.png");
                        if (UiCapture.Capture(view, path, _size))
                        {
                            written++;
                            Console.WriteLine($"[uidesign]   {path}");
                        }
                    }
                }
            });
            Console.WriteLine($"[uidesign] {written} picture(s) written to {directory}");
            return written > 0 ? 0 : 1;
        }

        private abstract class Design
        {
            public abstract string Id { get; }

            /// <summary>One line, printed beside the pictures.</summary>
            public abstract string Title { get; }

            /// <summary>Picking a map: a list, a picture of it, settings about it.</summary>
            public abstract Control PlayOffline();

            /// <summary>Picking a server: five columns that have to line up.</summary>
            public abstract Control PlayOnline();

            /// <summary>Fourteen rows, which is the density test.</summary>
            public abstract Control Settings();

            /// <summary>Eight entries over a running match.</summary>
            public abstract Control Pause();

            public IEnumerable<(string, Control)> Screens()
            {
                yield return ("play-offline", PlayOffline());
                yield return ("play-online", PlayOnline());
                yield return ("settings", Settings());
                yield return ("pause", Pause());
            }
        }

        // =====================================================================
        // A -- what ships
        // =====================================================================

        /// <summary>
        /// A. The layout F replaced: the list on the left, a picture and a
        /// column of settings on the right, a heading and a strip of sources
        /// over both, a cross and a tick in the bottom corners. Over the
        /// photograph, with no ground of its own anywhere.
        /// </summary>
        private sealed class CurrentDesign : Design
        {
            public override string Id => "a-current";
            public override string Title =>
                "what F replaced: list left, preview and options right, marks in the bottom corners";

            private static Panel Body(string heading, string[]? tabs, int tab, Control content)
            {
                Panel root = UiLayout.Backdrop();
                root.Children.Add(UiLayout.Heading(heading));
                if (tabs != null)
                {
                    root.Children.Add(new UiTabs(tabs, tab)
                    {
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = UiLayout.TabMargin
                    });
                }
                content.Margin = UiLayout.BodyMargin;
                root.Children.Add(content);
                return root;
            }

            private static Control ListAndSide(Control list, Control? preview, Control options)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(list, 0);
                grid.Children.Add(list);
                var column = new StackPanel { Spacing = 2, Width = 300 };
                if (preview != null)
                {
                    column.Children.Add(preview);
                }
                column.Children.Add(options);
                var side = new ScrollViewer
                {
                    Content = column,
                    Margin = new Thickness(30, 0, 0, 0),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                Grid.SetColumn(side, 1);
                grid.Children.Add(side);
                return grid;
            }

            public override Control PlayOffline()
            {
                Panel root = Body("play", _playTabs, 1,
                    ListAndSide(MapList(), Preview(172), OfflineOptions()));
                root.Children.Add(Corner(UiMark.Shape.Cancel, "back"));
                root.Children.Add(Corner(UiMark.Shape.Accept, "start"));
                return root;
            }

            public override Control PlayOnline()
            {
                Panel root = Body("play", _playTabs, 0,
                    ListAndSide(ServerTable(), Preview(172), OnlineOptions()));
                root.Children.Add(Corner(UiMark.Shape.Cancel, "back"));
                root.Children.Add(Corner(UiMark.Shape.Accept, "join"));
                return root;
            }

            public override Control Settings()
            {
                Panel root = Body("settings", _settingsTabs, 0, new ScrollViewer
                {
                    Content = SettingRows(560),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                });
                root.Children.Add(Corner(UiMark.Shape.Cancel, "cancel"));
                root.Children.Add(Corner(UiMark.Shape.Accept, "save"));
                return root;
            }

            public override Control Pause()
            {
                Panel root = MatchBehind();
                StackPanel menu = UiLayout.Column(12);
                foreach (string entry in _pause)
                {
                    menu.Children.Add(Word(entry));
                }
                root.Children.Add(menu);
                root.Children.Add(Footer(_match, UiLayout.ColumnLeft - 50));
                return root;
            }
        }

        // =====================================================================
        // B -- Sheet
        // =====================================================================

        /// <summary>
        /// B. Sheet: the screen is a page, and the page has a ground.
        ///
        /// The photograph stops being what the screen is drawn *on* and
        /// becomes what it is drawn *over*: a sheet of near-solid ink covers
        /// the middle of the frame edge to edge, with the picture left showing
        /// as a band top and bottom. Everything sits on that sheet, with a
        /// rule under the heading and a rule over the footer, so the screen
        /// has a top and a bottom instead of floating.
        ///
        /// The argument is density. These screens carry fourteen settings
        /// rows, twelve maps and a five-column table, and the current design
        /// asks all of it to be legible over whatever the photograph happens
        /// to be doing behind it -- which it manages by luck, because the
        /// launcher background is dark on the left. Put a bright map preview
        /// behind the server table and it does not. A sheet makes that a
        /// non-question for ever.
        ///
        /// The footer rail is the other half: the tick and the cross stop
        /// being two marks hiding in two corners of a photograph and become a
        /// bar that says what the screen does, with the primary action on the
        /// right where a dialog puts it.
        /// </summary>
        private sealed class SheetDesign : Design
        {
            public override string Id => "b-sheet";
            public override string Title =>
                "a sheet of ink over the photo, ruled top and bottom, actions on a footer rail";

            private const double Pad = 56;

            private static Panel Page(string heading, Control? nav, Control content,
                string cancel, string accept, bool overGame = false)
            {
                Panel root = overGame ? MatchBehind() : UiLayout.Backdrop();
                var sheet = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(0, 44, 0, 44)
                };

                var head = new StackPanel { Spacing = 0, Margin = new Thickness(Pad, 22, Pad, 14) };
                head.Children.Add(new TextBlock
                {
                    Text = heading.ToUpperInvariant(),
                    FontFamily = GuiTheme.Display,
                    FontSize = 19,
                    FontWeight = FontWeight.Black,
                    Foreground = new SolidColorBrush(GuiTheme.Text),
                    Margin = new Thickness(0, 0, 0, nav == null ? 0 : 12)
                });
                if (nav != null)
                {
                    head.Children.Add(nav);
                }
                Grid.SetRow(head, 0);
                sheet.Children.Add(head);

                content.Margin = new Thickness(Pad, 0, Pad, 0);
                var body = new Border
                {
                    BorderBrush = GuiTheme.EdgeBrush,
                    BorderThickness = new Thickness(0, 1, 0, 1),
                    Padding = new Thickness(0, 18, 0, 18),
                    Child = content
                };
                Grid.SetRow(body, 1);
                sheet.Children.Add(body);

                var rail = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(Pad, 16, Pad, 20)
                };
                var back = new UiMark(UiMark.Shape.Cancel, cancel);
                Grid.SetColumn(back, 0);
                rail.Children.Add(back);
                var go = new UiMark(UiMark.Shape.Accept, accept)
                {
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(go, 2);
                rail.Children.Add(go);
                Grid.SetRow(rail, 2);
                sheet.Children.Add(rail);

                root.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb((byte)(overGame ? 242 : 235),
                        GuiTheme.Ink.R, GuiTheme.Ink.G, GuiTheme.Ink.B)),
                    Child = sheet
                });
                return root;
            }

            private static Control Split(Control list, Control? preview, Control options)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(list, 0);
                grid.Children.Add(list);
                var column = new StackPanel { Spacing = 4, Width = 320 };
                if (preview != null)
                {
                    column.Children.Add(preview);
                }
                column.Children.Add(options);
                var side = new ScrollViewer
                {
                    Content = column,
                    Margin = new Thickness(34, 0, 0, 0),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                Grid.SetColumn(side, 1);
                grid.Children.Add(side);
                return grid;
            }

            private static UiTabs Nav(string[] items, int index)
            {
                return new UiTabs(items, index) { HorizontalAlignment = HorizontalAlignment.Left };
            }

            public override Control PlayOffline() =>
                Page("play", Nav(_playTabs, 1),
                    Split(MapList(), Preview(180), OfflineOptions()), "back", "start");

            public override Control PlayOnline() =>
                Page("play", Nav(_playTabs, 0),
                    Split(ServerTable(), Preview(180), OnlineOptions()), "back", "join");

            public override Control Settings() =>
                Page("settings", Nav(_settingsTabs, 0), new ScrollViewer
                {
                    Content = SettingRows(620),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }, "cancel", "save");

            public override Control Pause()
            {
                var menu = new StackPanel { Spacing = 11, Width = 320 };
                foreach (string entry in _pause)
                {
                    menu.Children.Add(Word(entry));
                }
                var holder = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                Grid.SetColumn(menu, 0);
                holder.Children.Add(menu);
                var note = new TextBlock
                {
                    Text = _match,
                    FontFamily = GuiTheme.Display,
                    FontSize = 12,
                    Foreground = GuiTheme.TextDimBrush,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Grid.SetColumn(note, 1);
                holder.Children.Add(note);
                return Page("paused", null, holder, "leave match", "resume", overGame: true);
            }
        }

        // =====================================================================
        // C -- Split
        // =====================================================================

        /// <summary>
        /// C. Split: the preview *is* the background.
        ///
        /// One idea, and everything else follows from it. The map picture is
        /// currently a 300x172 thumbnail in the corner of a column of
        /// settings, next to a photograph of a completely different room -- so
        /// the screen shows two pictures of two places and the small one is the
        /// one that matters. Here the chosen map fills the right two thirds of
        /// the frame at full size and the launcher photograph is simply not
        /// drawn: what you are looking at *is* the thing you are about to load.
        ///
        /// The left third becomes a solid column carrying the heading, the
        /// sources, the list and the tick -- everything you press -- and the
        /// right side carries the map's name over its own picture with the
        /// settings about it down in the wash, where they do not cover it.
        ///
        /// Where it gets hard is the screens with nothing to preview. Settings
        /// has no picture, so the right side keeps the launcher photograph and
        /// fourteen rows run down a 360-point column. That is this style's
        /// bill, and the picture is there to show the size of it.
        /// </summary>
        private sealed class SplitDesign : Design
        {
            public override string Id => "c-split";
            public override string Title =>
                "the chosen map fills the frame, launcher photo dropped; everything you press in a solid left column";

            private const double ColumnWidth = 430;
            private const double Pad = 44;

            private static Panel Frame(Control? rightPicture, Control? rightOverlay,
                Control column, bool overGame = false)
            {
                var root = new Panel();
                if (rightPicture != null)
                {
                    root.Children.Add(rightPicture);
                }
                else if (overGame)
                {
                    root.Children.Add(MatchBehind());
                }
                else
                {
                    root.Children.Add(UiLayout.Backdrop());
                }
                if (rightOverlay != null)
                {
                    root.Children.Add(rightOverlay);
                }
                // A hard column, and a short gradient off its edge so it does
                // not read as a rectangle stuck onto a photograph.
                root.Children.Add(new Border
                {
                    Width = ColumnWidth + 70,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(252, 10, 12, 16), 0),
                            new GradientStop(Color.FromArgb(252, 10, 12, 16),
                                ColumnWidth / (ColumnWidth + 70)),
                            new GradientStop(Color.FromArgb(0, 10, 12, 16), 1)
                        }
                    }
                });
                column.Width = ColumnWidth;
                column.HorizontalAlignment = HorizontalAlignment.Left;
                root.Children.Add(column);
                return root;
            }

            /// <summary>Heading, sources, the list, and the marks under it.</summary>
            private static Grid Column(string heading, Control? nav, Control content,
                string cancel, string accept)
            {
                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
                    Margin = new Thickness(Pad, 40, 26, 34)
                };
                var title = new TextBlock
                {
                    Text = heading.ToUpperInvariant(),
                    FontFamily = GuiTheme.Display,
                    FontSize = 18,
                    FontWeight = FontWeight.Black,
                    Foreground = new SolidColorBrush(GuiTheme.Text),
                    Margin = new Thickness(0, 0, 0, nav == null ? 16 : 12)
                };
                Grid.SetRow(title, 0);
                grid.Children.Add(title);
                if (nav != null)
                {
                    nav.Margin = new Thickness(0, 0, 0, 18);
                    Grid.SetRow(nav, 1);
                    grid.Children.Add(nav);
                }
                Grid.SetRow(content, 2);
                grid.Children.Add(content);
                var rail = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(0, 20, 0, 0)
                };
                var back = new UiMark(UiMark.Shape.Cancel, cancel);
                Grid.SetColumn(back, 0);
                rail.Children.Add(back);
                var go = new UiMark(UiMark.Shape.Accept, accept)
                {
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(go, 2);
                rail.Children.Add(go);
                Grid.SetRow(rail, 3);
                grid.Children.Add(rail);
                return grid;
            }

            private static Control? BigPreview()
            {
                Bitmap? picture = RoomPicture(_room);
                return picture == null ? null
                    : new Image { Source = picture, Stretch = Stretch.UniformToFill };
            }

            private static Control NameOver(string name, string detail, Control? extra)
            {
                var stack = new StackPanel
                {
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, Pad, 34),
                    Width = 360
                };
                stack.Children.Add(new TextBlock
                {
                    Text = name,
                    FontFamily = GuiTheme.Display,
                    FontSize = 26,
                    FontWeight = FontWeight.Black,
                    Foreground = new SolidColorBrush(GuiTheme.Text),
                    TextAlignment = TextAlignment.Right
                });
                stack.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontFamily = GuiTheme.Display,
                    FontSize = 12.5,
                    Foreground = GuiTheme.TextDimBrush,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 0, 0, extra == null ? 0 : 18)
                });
                if (extra != null)
                {
                    stack.Children.Add(extra);
                }
                var panel = new Panel();
                // Enough wash at the foot of the picture to read a name on.
                panel.Children.Add(new Border
                {
                    Height = 320,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0),
                            new GradientStop(Color.FromArgb(225, 0, 0, 0), 1)
                        }
                    }
                });
                panel.Children.Add(stack);
                return panel;
            }

            public override Control PlayOffline()
            {
                return Frame(BigPreview(),
                    NameOver("Combat Hall", "MP3 PROVING GROUND  ·  battle  ·  3 bots",
                        OfflineOptions(300)),
                    Column("play", new UiTabs(_playTabs, 1)
                    {
                        HorizontalAlignment = HorizontalAlignment.Left
                    }, MapList(), "back", "start"));
            }

            public override Control PlayOnline()
            {
                return Frame(BigPreview(),
                    NameOver("51.161.113.128", "MP3 PROVING GROUND  ·  battle  ·  3/8  ·  41 ms",
                        OnlineOptions(300)),
                    Column("play", new UiTabs(_playTabs, 0)
                    {
                        HorizontalAlignment = HorizontalAlignment.Left
                    }, ServerTable(compact: true), "back", "join"));
            }

            public override Control Settings()
            {
                return Frame(null, null,
                    Column("settings", new UiTabs(_settingsTabs, 0)
                    {
                        HorizontalAlignment = HorizontalAlignment.Left
                    }, new ScrollViewer
                    {
                        Content = SettingRows(360),
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                    }, "cancel", "save"));
            }

            public override Control Pause()
            {
                var menu = new StackPanel { Spacing = 12 };
                foreach (string entry in _pause)
                {
                    menu.Children.Add(Word(entry));
                }
                return Frame(null, null,
                    Column("paused", null, menu, "leave match", "resume"), overGame: true);
            }
        }

        // =====================================================================
        // D -- Stack
        // =====================================================================

        /// <summary>
        /// D. Stack: one column, full width, and the row you chose opens.
        ///
        /// No side column at all. The list runs the width of the frame and the
        /// selected row expands in place to show its own picture and its own
        /// settings, the way a list of anything on a phone does. Nothing is
        /// ever off to one side being about something else.
        ///
        /// What it buys is the thing none of the other four have: **the same
        /// layout works in portrait.** Android draws these exact controls, and
        /// every other style here puts a 300-point column beside a list, which
        /// a phone held upright has nowhere to put -- so each of them is one
        /// desktop layout plus a phone layout to be invented later, and the two
        /// then drift, which is the arrangement this whole launcher was
        /// rewritten to get rid of.
        ///
        /// What it costs is comparison. A table of servers is a table because
        /// you are choosing between rows on their ping and their player count,
        /// and pushing those rows apart to open one is exactly the wrong move
        /// -- so the online face keeps its columns and puts the detail in a
        /// strip under the selected row instead. One style, two behaviours.
        /// </summary>
        private sealed class StackDesign : Design
        {
            public override string Id => "d-stack";
            public override string Title =>
                "one full-width column, the chosen row opens in place -- the only one that also works in portrait";

            private const double Pad = 96;

            private static Panel Page(string heading, Control? nav, Control content,
                string cancel, string accept, bool overGame = false)
            {
                Panel root = overGame ? MatchBehind() : UiLayout.Backdrop();
                // A wash across the whole frame rather than a sheet: the
                // column is the width of the screen, so there is no edge to
                // put one against.
                root.Children.Add(new Border
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0.4, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(240, 10, 12, 16), 0),
                            new GradientStop(Color.FromArgb(205, 10, 12, 16), 0.7),
                            new GradientStop(Color.FromArgb(150, 10, 12, 16), 1)
                        }
                    }
                });
                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(Pad, 38, Pad, 30)
                };
                var head = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 16) };
                head.Children.Add(new TextBlock
                {
                    Text = heading.ToUpperInvariant(),
                    FontFamily = GuiTheme.Display,
                    FontSize = 18,
                    FontWeight = FontWeight.Black,
                    Foreground = new SolidColorBrush(GuiTheme.Text)
                });
                if (nav != null)
                {
                    head.Children.Add(nav);
                }
                Grid.SetRow(head, 0);
                grid.Children.Add(head);
                Grid.SetRow(content, 1);
                grid.Children.Add(content);
                var rail = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(0, 18, 0, 0)
                };
                var back = new UiMark(UiMark.Shape.Cancel, cancel);
                Grid.SetColumn(back, 0);
                rail.Children.Add(back);
                var go = new UiMark(UiMark.Shape.Accept, accept)
                {
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(go, 2);
                rail.Children.Add(go);
                Grid.SetRow(rail, 2);
                grid.Children.Add(rail);
                root.Children.Add(grid);
                return root;
            }

            /// <summary>
            /// The opened row: its picture and its settings, inset under the
            /// name behind a rule of accent, so it reads as belonging to the
            /// row above rather than as the next one down.
            /// </summary>
            private static Control Opened(Control? picture, Control options)
            {
                var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                if (picture != null)
                {
                    picture.Margin = new Thickness(0, 0, 28, 0);
                    Grid.SetColumn(picture, 0);
                    inner.Children.Add(picture);
                }
                Grid.SetColumn(options, 1);
                inner.Children.Add(options);
                return new Border
                {
                    BorderBrush = GuiTheme.AccentBrush,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(22, 12, 0, 18),
                    Margin = new Thickness(0, 2, 0, 10),
                    Child = inner
                };
            }

            public override Control PlayOffline()
            {
                var stack = new StackPanel { Spacing = 1 };
                stack.Children.Add(Chosen("Combat Hall", "MP3 PROVING GROUND"));
                Control preview = Preview(150, 266);
                preview.Margin = new Thickness(0);
                stack.Children.Add(Opened(preview, OfflineOptions(320)));
                foreach ((string title, string key) in _moreRooms)
                {
                    stack.Children.Add(new UiListRow(title, key));
                }
                return Page("play", new UiTabs(_playTabs, 1)
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                }, new ScrollViewer
                {
                    Content = stack,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }, "back", "start");
            }

            public override Control PlayOnline()
            {
                var stack = new StackPanel { Spacing = 1 };
                for (int i = 0; i < _servers.Length; i++)
                {
                    stack.Children.Add(Server(i));
                    if (i != 0)
                    {
                        continue;
                    }
                    var strip = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 28,
                        Margin = new Thickness(22, 12, 0, 14)
                    };
                    Control preview = Preview(104, 184);
                    preview.Margin = new Thickness(0);
                    strip.Children.Add(preview);
                    strip.Children.Add(OnlineOptions(330));
                    stack.Children.Add(new Border
                    {
                        BorderBrush = GuiTheme.AccentBrush,
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Child = strip
                    });
                }
                return Page("play", new UiTabs(_playTabs, 0)
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                }, new ScrollViewer
                {
                    Content = stack,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }, "back", "join");
            }

            public override Control Settings()
            {
                return Page("settings", new UiTabs(_settingsTabs, 0)
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                }, new ScrollViewer
                {
                    Content = SettingRows(double.NaN),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }, "cancel", "save");
            }

            public override Control Pause()
            {
                var menu = new StackPanel { Spacing = 12 };
                foreach (string entry in _pause)
                {
                    menu.Children.Add(Word(entry));
                }
                return Page("paused", null, menu, "leave match", "resume", overGame: true);
            }

            /// <summary>
            /// The open row's own name, drawn rather than left to
            /// <see cref="UiListRow"/>: the row is open because it is chosen,
            /// and a capture focuses nothing, so nothing would say so.
            /// </summary>
            private static Control Chosen(string title, string detail)
            {
                var grid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Height = 32
                };
                var caret = new Border
                {
                    Width = 3,
                    Background = GuiTheme.AccentBrush,
                    Margin = new Thickness(0, 6, 11, 6)
                };
                Grid.SetColumn(caret, 0);
                grid.Children.Add(caret);
                var name = new TextBlock
                {
                    Text = title,
                    FontFamily = GuiTheme.Display,
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = GuiTheme.AccentBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                var key = new TextBlock
                {
                    Text = detail,
                    FontFamily = GuiTheme.Display,
                    FontSize = 12,
                    Foreground = GuiTheme.TextDimBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(key, 2);
                grid.Children.Add(key);
                return grid;
            }
        }

        // =====================================================================
        // E -- Shell
        // =====================================================================

        /// <summary>
        /// E. Shell: one window with a permanent rail, and the screens live
        /// inside it.
        ///
        /// The only style here that is not a page layout at all. Play,
        /// Settings and Profile stop being separate screens you open and close
        /// and become panes of one thing, reached from a rail down the left
        /// that never goes away and always says where you are. Which is Steam,
        /// and Battle.net, and every launcher anybody has actually used.
        ///
        /// The claim is about presses, not looks. Today Settings is somewhere
        /// you go *back* from -- front screen, settings, back, play -- and the
        /// rail collapses that: the settings and a map list are one press from
        /// each other, in either direction, at any time. It also gives the
        /// pause menu somewhere honest to be, since the pause menu is this
        /// same rail with the match still running behind it.
        ///
        /// The bill is the front screen. A rail that is always there is a rail
        /// on the front screen too -- and the front screen is the one thing
        /// that is settled and staying. So this style is either a second shell
        /// that begins the moment Play is pressed, or it is a change to the
        /// one screen this exercise was told not to touch.
        /// </summary>
        private sealed class ShellDesign : Design
        {
            public override string Id => "e-shell";
            public override string Title =>
                "a permanent left rail; Play and Settings become panes of one window, one press apart";

            private const double RailWidth = 215;

            private static Panel Frame(int railIndex, Control content, string cancel,
                string accept, bool overGame = false)
            {
                Panel root = overGame ? MatchBehind() : UiLayout.Backdrop();
                root.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(
                        (byte)(overGame ? 120 : 196),
                        GuiTheme.Ink.R, GuiTheme.Ink.G, GuiTheme.Ink.B))
                });
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

                var rail = new StackPanel
                {
                    Spacing = 0,
                    Width = RailWidth,
                    Margin = new Thickness(38, 44, 0, 0)
                };
                Control mark = new DeckWordmark(1.75);
                mark.Width = 132;
                mark.HorizontalAlignment = HorizontalAlignment.Left;
                mark.VerticalAlignment = VerticalAlignment.Top;
                mark.Margin = new Thickness(0, 0, 0, 42);
                rail.Children.Add(mark);
                string[] places = { "Play", "Settings", "Profile" };
                for (int i = 0; i < places.Length; i++)
                {
                    var line = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(0, 0, 0, 15)
                    };
                    var tick = new Border
                    {
                        Width = 2,
                        Background = i == railIndex ? GuiTheme.AccentBrush : Brushes.Transparent,
                        Margin = new Thickness(0, 2, 16, 2)
                    };
                    Grid.SetColumn(tick, 0);
                    line.Children.Add(tick);
                    UiWord word = Word(places[i], 19);
                    word.Selected = i == railIndex;
                    Grid.SetColumn(word, 1);
                    line.Children.Add(word);
                    rail.Children.Add(line);
                }
                rail.Children.Add(new Border
                {
                    Height = 1,
                    Background = GuiTheme.EdgeBrush,
                    Margin = new Thickness(18, 16, 30, 20)
                });
                var last = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };
                last.Children.Add(Word(overGame ? "Leave match" : "Quit", 16, GuiTheme.TextDim));
                rail.Children.Add(last);
                Grid.SetColumn(rail, 0);
                grid.Children.Add(rail);

                var pane = new Grid
                {
                    RowDefinitions = new RowDefinitions("*,Auto"),
                    Margin = new Thickness(34, 44, 52, 32)
                };
                Grid.SetRow(content, 0);
                pane.Children.Add(content);
                var bar = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                    Margin = new Thickness(0, 16, 0, 0)
                };
                var back = new UiMark(UiMark.Shape.Cancel, cancel);
                Grid.SetColumn(back, 0);
                bar.Children.Add(back);
                var go = new UiMark(UiMark.Shape.Accept, accept)
                {
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(go, 2);
                bar.Children.Add(go);
                Grid.SetRow(bar, 1);
                pane.Children.Add(bar);
                var ground = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(216,
                        GuiTheme.Panel.R, GuiTheme.Panel.G, GuiTheme.Panel.B)),
                    BorderBrush = GuiTheme.EdgeBrush,
                    BorderThickness = new Thickness(1, 0, 0, 0),
                    Child = pane
                };
                Grid.SetColumn(ground, 1);
                grid.Children.Add(ground);
                root.Children.Add(grid);
                return root;
            }

            private static Control Pane(Control? nav, Control content)
            {
                var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
                if (nav != null)
                {
                    nav.Margin = new Thickness(0, 0, 0, 18);
                    Grid.SetRow(nav, 0);
                    grid.Children.Add(nav);
                }
                Grid.SetRow(content, 1);
                grid.Children.Add(content);
                return grid;
            }

            private static Control Split(Control list, Control? preview, Control options)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(list, 0);
                grid.Children.Add(list);
                var column = new StackPanel { Spacing = 4, Width = 290 };
                if (preview != null)
                {
                    column.Children.Add(preview);
                }
                column.Children.Add(options);
                var side = new ScrollViewer
                {
                    Content = column,
                    Margin = new Thickness(30, 0, 0, 0),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };
                Grid.SetColumn(side, 1);
                grid.Children.Add(side);
                return grid;
            }

            public override Control PlayOffline() =>
                Frame(0, Pane(new UiTabs(_playTabs, 1)
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                }, Split(MapList(), Preview(158), OfflineOptions())), "back", "start");

            public override Control PlayOnline() =>
                Frame(0, Pane(new UiTabs(_playTabs, 0)
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                }, Split(ServerTable(), Preview(158), OnlineOptions())), "back", "join");

            public override Control Settings() =>
                Frame(1, Pane(new UiTabs(_settingsTabs, 0)
                {
                    HorizontalAlignment = HorizontalAlignment.Left
                }, new ScrollViewer
                {
                    Content = SettingRows(560),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }), "cancel", "save");

            public override Control Pause()
            {
                var menu = new StackPanel { Spacing = 13 };
                foreach (string entry in new[]
                    { "Resume", "Vote map", "Spectate", "Fullscreen", "Record replay" })
                {
                    menu.Children.Add(Word(entry));
                }
                menu.Children.Add(new TextBlock
                {
                    Text = _match,
                    FontFamily = GuiTheme.Display,
                    FontSize = 12,
                    Foreground = GuiTheme.TextDimBrush,
                    Margin = new Thickness(0, 28, 0, 0)
                });
                return Frame(-1, Pane(null, menu), "quit", "resume", overGame: true);
            }
        }

        // =====================================================================
        // F -- Centre
        // =====================================================================

        /// <summary>
        /// F. Centre: one well down the middle of the frame, and the two marks
        /// together under it.
        ///
        /// Asked for by name off the first round's settings page, so this is
        /// that treatment carried across all four screens rather than the one
        /// it was drawn on. The content sits in a fixed well down the centre,
        /// the heading and the sources are centred over it, and the cross and
        /// the tick sit side by side at the foot -- which is the part that
        /// makes it read: "no" and "yes" as a pair, in the order they are read
        /// in, instead of two marks hunted for in opposite corners.
        ///
        /// It behaves differently from the other five in one way worth
        /// knowing: the well has a fixed width, so making the window wider
        /// gives the *photograph* more room and the content none. That is the
        /// argument for it -- fourteen rows never stretch to 1800 points on an
        /// ultrawide the way a full-width layout does, and a settings row 1800
        /// points wide is a label and a control with a metre of nothing
        /// between them. It is also the argument against: on a small window
        /// the well is most of the frame and the symmetry stops being visible
        /// at all.
        ///
        /// The Play screens are where it is tested rather than flattered. A
        /// list *and* a picture *and* a column of settings inside one centred
        /// well is a lot to fit, so the well widens there and the preview goes
        /// over the list rather than beside it -- which is a second
        /// arrangement, and whether that still counts as one style is the
        /// question these two pictures are for.
        /// </summary>
        private sealed class CentreDesign : Design
        {
            public override string Id => "f-centre";
            public override string Title =>
                "a centred well with the two marks together beneath it -- CHOSEN, and now what the launcher does";

            private static Panel Page(double wellWidth, string heading, Control? nav,
                Control content, string cancel, string accept, bool overGame = false)
            {
                Panel root = overGame ? MatchBehind() : UiLayout.Backdrop();
                // A soft vertical wash rather than a panel: the well has no
                // edge, so nothing may have one.
                root.Children.Add(new Border
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(150, 10, 12, 16), 0),
                            new GradientStop(Color.FromArgb(225, 10, 12, 16), 0.45),
                            new GradientStop(Color.FromArgb(225, 10, 12, 16), 0.62),
                            new GradientStop(Color.FromArgb(150, 10, 12, 16), 1)
                        }
                    }
                });
                var well = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,Auto,*"),
                    Width = wellWidth,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 44, 0, 84)
                };
                var title = new TextBlock
                {
                    Text = heading.ToLowerInvariant(),
                    FontFamily = GuiTheme.Display,
                    FontSize = UiLayout.HeadingSize,
                    Foreground = GuiTheme.TextDimBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, nav == null ? 22 : 10)
                };
                Grid.SetRow(title, 0);
                well.Children.Add(title);
                if (nav != null)
                {
                    nav.HorizontalAlignment = HorizontalAlignment.Center;
                    nav.Margin = new Thickness(0, 0, 0, 22);
                    Grid.SetRow(nav, 1);
                    well.Children.Add(nav);
                }
                Grid.SetRow(content, 2);
                well.Children.Add(content);
                root.Children.Add(well);

                var pair = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 64,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 28)
                };
                pair.Children.Add(new UiMark(UiMark.Shape.Cancel, cancel));
                pair.Children.Add(new UiMark(UiMark.Shape.Accept, accept));
                root.Children.Add(pair);
                return root;
            }

            /// <summary>
            /// The picture over the list rather than beside it: a centred well
            /// has no side to put a column at without stopping being centred.
            /// </summary>
            private static Control Over(Control preview, Control list, Control options)
            {
                var grid = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,*"),
                    ColumnDefinitions = new ColumnDefinitions("*,Auto")
                };
                preview.Margin = new Thickness(0, 0, 24, 16);
                Grid.SetRow(preview, 0);
                Grid.SetColumn(preview, 0);
                grid.Children.Add(preview);
                Grid.SetRow(options, 0);
                Grid.SetColumn(options, 1);
                grid.Children.Add(options);
                Grid.SetRow(list, 1);
                Grid.SetColumnSpan(list, 2);
                grid.Children.Add(list);
                return grid;
            }

            public override Control PlayOffline() =>
                Page(840, "play", new UiTabs(_playTabs, 1),
                    Over(Preview(168, 420), MapList(), OfflineOptions(340)), "back", "start");

            public override Control PlayOnline() =>
                Page(880, "play", new UiTabs(_playTabs, 0),
                    Over(Preview(150, 300), ServerTable(), OnlineOptions(320)), "back", "join");

            public override Control Settings() =>
                Page(660, "settings", new UiTabs(_settingsTabs, 0), new ScrollViewer
                {
                    Content = SettingRows(double.NaN),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                }, "cancel", "save");

            public override Control Pause()
            {
                var menu = new StackPanel { Spacing = 13 };
                foreach (string entry in _pause)
                {
                    UiWord word = Word(entry);
                    word.HorizontalAlignment = HorizontalAlignment.Center;
                    menu.Children.Add(word);
                }
                var holder = new StackPanel { Spacing = 0 };
                holder.Children.Add(menu);
                holder.Children.Add(new TextBlock
                {
                    Text = _match,
                    FontFamily = GuiTheme.Display,
                    FontSize = 12,
                    Foreground = GuiTheme.TextDimBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 26, 0, 0)
                });
                return Page(460, "paused", null, holder, "leave match", "resume",
                    overGame: true);
            }
        }

        // =====================================================================
        // the content every style has to carry
        // =====================================================================

        private static readonly string[] _playTabs = { "Online", "Offline", "Story", "Replays" };
        private static readonly string[] _settingsTabs = { "Game", "Controls", "Player" };

        private static readonly string[] _pause =
        {
            "Resume", "Vote map", "Spectate", "Fullscreen",
            "Record replay", "Settings", "Leave match", "Quit"
        };

        private const string _match = "online match -- MP3 PROVING GROUND";
        private const string _room = "MP3 PROVING GROUND";

        private static readonly (string Title, string Key)[] _rooms =
        {
            ("Combat Hall", "MP3 PROVING GROUND"),
            ("High Ground", "MP4 HIGHGROUND"),
            ("Elder Passage", "MP4 HIGHGROUND - EXPANDED"),
            ("Compression Chamber", "MP5 FUEL SLUICE"),
            ("Head Shot", "MP6 HEADSHOT"),
            ("Processor Core", "MP7 PROCESSOR CORE"),
            ("Weapons Complex", "MP8 FIRE CONTROL"),
            ("Ice Hive", "MP9 CRYOCHASM"),
            ("Test Arena", "TEST ARENA"),
            ("VDO Gateway", "UNIT 3 VESPER STARPORT"),
            ("Arcterra Gateway", "UNIT 4 ARCTERRA BASE"),
            ("Sanctorus", "MP1 SANCTORUS")
        };

        /// <summary>The rooms under the opened one, in the Stack study.</summary>
        private static readonly (string Title, string Key)[] _moreRooms =
        {
            ("High Ground", "MP4 HIGHGROUND"),
            ("Elder Passage", "MP4 HIGHGROUND - EXPANDED"),
            ("Compression Chamber", "MP5 FUEL SLUICE"),
            ("Head Shot", "MP6 HEADSHOT"),
            ("Processor Core", "MP7 PROCESSOR CORE"),
            ("Weapons Complex", "MP8 FIRE CONTROL")
        };

        private static readonly (string Name, string Room, GameMode Mode, int Players, int Ping)[]
            _servers =
        {
            ("51.161.113.128", "MP3 PROVING GROUND", GameMode.Battle, 3, 41),
            ("Project Prime - West Europe", "UNIT 3 VESPER STARPORT", GameMode.Battle, 0, 39),
            ("Project Prime - West US 2", "MP10 OVERLOAD", GameMode.Bounty, 5, 150),
            ("Project Prime - Japan", "UNIT1 ALINOS LANDFALL", GameMode.PrimeHunter, 8, 251),
            ("raspberrypi", "MP2 HARVESTER", GameMode.Battle, 1, 2)
        };

        /// <summary>Twelve maps, so every style is asked to scroll something.</summary>
        private static UiList MapList()
        {
            var list = new UiList();
            foreach ((string title, string key) in _rooms)
            {
                list.Add(new UiListRow(title, key) { Choice = key });
            }
            list.FocusFirst();
            return list;
        }

        private static UiList ServerTable(bool compact = false)
        {
            var list = new UiList();
            for (int i = 0; i < _servers.Length; i++)
            {
                list.Add(Server(i));
            }
            list.FocusFirst();
            return list;
        }

        private static ServerRow Server(int i)
        {
            (string name, string room, GameMode mode, int players, int ping) = _servers[i];
            var row = new ServerRow(name, "203.0.113.7:27888");
            row.SetStatus(new ServerStatus
            {
                Online = true,
                RoomKey = room,
                Mode = mode,
                Players = players,
                MaxPlayers = 8,
                Latency = ping
            });
            return row;
        }

        /// <summary>The settings a local match is started with.</summary>
        private static Control OfflineOptions(double width = double.NaN)
        {
            var stack = new StackPanel { Spacing = 2 };
            if (!double.IsNaN(width))
            {
                stack.Width = width;
            }
            stack.Children.Add(new ChoiceRow("Where", new[] { "Local", "Online" }, 0));
            stack.Children.Add(new ChoiceRow("Match type",
                new[] { "Battle", "Battle teams", "Survival", "Bounty" }, 0));
            stack.Children.Add(new ChoiceRow("Hunter",
                new[] { "Samus", "Kanden", "Trace", "Sylux", "Noxus", "Spire", "Weavel" }, 0));
            stack.Children.Add(new ChoiceRow("Bots", new[] { "0", "1", "2", "3" }, 3));
            stack.Children.Add(new ChoiceRow("Bot skill",
                new[] { "Easy", "Normal", "Hard", "Insane" }, 1));
            return stack;
        }

        private static Control OnlineOptions(double width = double.NaN)
        {
            var stack = new StackPanel { Spacing = 2 };
            if (!double.IsNaN(width))
            {
                stack.Width = width;
            }
            stack.Children.Add(new FieldRow("Name", "Livetek", boxWidth: 150));
            stack.Children.Add(new ChoiceRow("Hunter",
                new[] { "Samus", "Kanden", "Trace", "Sylux", "Noxus", "Spire", "Weavel" }, 3));
            stack.Children.Add(new FieldRow("Address", "89.160.162.50:27888", boxWidth: 170));
            return stack;
        }

        /// <summary>
        /// Fourteen real rows at their real heights, which is the density
        /// test. A mock row three quarters the height of the real thing would
        /// make every one of these six look better than it is.
        /// </summary>
        private static Control SettingRows(double width)
        {
            var stack = new StackPanel { Spacing = 2 };
            if (!double.IsNaN(width))
            {
                stack.Width = width;
            }
            stack.Children.Add(new Caption("window"));
            stack.Children.Add(new ChoiceRow("Mode",
                new[] { "Windowed", "Fullscreen", "Borderless" }, 0));
            stack.Children.Add(new Caption("view"));
            stack.Children.Add(new SliderRow("Field of view", 78,
                v => $"{v}°{(v == 78 ? " (DS)" : "")}", labelWidth: 130, min: 60, max: 120));
            stack.Children.Add(new SliderRow("Render scale", 100, v => $"{v}%",
                labelWidth: 130, min: 50, max: 100));
            stack.Children.Add(new ToggleRow("Lighting", on: true));
            stack.Children.Add(new ToggleRow("Fog", on: true));
            stack.Children.Add(new ToggleRow("FPS counter", on: false));
            stack.Children.Add(new Caption("hud"));
            stack.Children.Add(new ToggleRow("Pro mode HUD", on: true));
            stack.Children.Add(new ChoiceRow("Crosshair size",
                new[] { "Small", "Medium", "Big" }, 1));
            stack.Children.Add(new ChoiceRow("Crosshair type",
                new[] { "Cross", "Dot", "CrossDot", "Circle", "Brackets" }, 0));
            stack.Children.Add(new Caption("match"));
            stack.Children.Add(new ChoiceRow("Weapon",
                new[] { "Dynamic (DS)", "Static (Quake)" }, 0));
            stack.Children.Add(new ToggleRow("Chat", on: true));
            return stack;
        }

        // ------------------------------------------------------------ helpers

        /// <summary>The picture of the chosen map, at whatever size a style gives it.</summary>
        private static Control Preview(double height, double width = double.NaN)
        {
            var box = new Border
            {
                Height = height,
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 10),
                Child = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = RoomPicture(_room)
                }
            };
            if (!double.IsNaN(width))
            {
                box.Width = width;
            }
            return box;
        }

        private static Bitmap? RoomPicture(string room)
        {
            try
            {
                string path = ThumbnailGenerator.PathFor(room);
                if (!File.Exists(path))
                {
                    return null;
                }
                // Through a MemoryStream so the file is not held open.
                using var stream = new MemoryStream(File.ReadAllBytes(path));
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                // No game files, or no preview rendered yet.
                return null;
            }
        }

        /// <summary>
        /// What a pause menu is drawn over: a picture of a room, then the
        /// scrim.
        ///
        /// <c>UiLayout.Backdrop(overGame: true)</c> is the scrim alone,
        /// because in a real match the frame underneath is the match. A
        /// capture has no match, so the pause pictures came out as a menu on
        /// flat black -- which is the one thing a pause menu is never seen
        /// against, and it makes every style look equally readable.
        /// </summary>
        private static Panel MatchBehind()
        {
            var root = new Panel();
            Bitmap? picture = RoomPicture(_room);
            if (picture != null)
            {
                root.Children.Add(new Image { Source = picture, Stretch = Stretch.UniformToFill });
            }
            root.Children.Add(new Border { Background = GuiTheme.ScrimBrush });
            return root;
        }

        private static UiWord Word(string text, double size = UiLayout.WordSize,
            Color? colour = null, FontFamily? font = null)
        {
            return new UiWord(text, size, font, colour);
        }

        private static TextBlock Footer(string text, double left)
        {
            return new TextBlock
            {
                Text = text,
                FontFamily = GuiTheme.Display,
                FontSize = 12,
                Foreground = GuiTheme.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(left, 0, 0, UiLayout.FooterBottom)
            };
        }

        private static UiMark Corner(UiMark.Shape shape, string label)
        {
            return new UiMark(shape, label)
            {
                HorizontalAlignment = shape == UiMark.Shape.Accept
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = shape == UiMark.Shape.Accept
                    ? new Thickness(0, 0, UiLayout.CornerX, UiLayout.CornerY)
                    : new Thickness(UiLayout.CornerX, 0, 0, UiLayout.CornerY)
            };
        }
    }
}
#endif
