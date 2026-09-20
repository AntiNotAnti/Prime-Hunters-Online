using System;
using System.Collections.Generic;
using Avalonia;

namespace MphRead.Mods.Launcher.Gui
{
    /// <summary>
    /// What <c>-tapcheck</c> runs: <see cref="Tap"/> on its own, against
    /// gestures written down rather than performed.
    ///
    /// The bug this answers -- scrolling the settings page on a phone
    /// answering every row the drag passed over -- is one nothing here can
    /// see. There is no touchscreen on this machine, the emulator cannot load
    /// a match, and a screenshot of a settings page says nothing about what a
    /// finger dragged across it does. What *is* checkable is the rule itself,
    /// which is a handful of points and a distance, so it lives in a class
    /// with no toolkit in it and is driven here with the coordinates a finger
    /// would have produced.
    ///
    /// The other half -- that every row calls into this rather than acting on
    /// its own press -- is not something a check can hold down. It is the
    /// reason <see cref="Tap"/> is one class and not a habit.
    /// </summary>
    internal static class TapCheck
    {
        private sealed class Case
        {
            public string Name = "";
            /// <summary>The gesture, and whether the row should have acted.</summary>
            public Func<bool> Run = null!;
            public bool Expected;
        }

        /// <summary>A row on a settings page: the width of the panel, 34 tall.</summary>
        private static readonly Size RowSize = new(420, 34);

        public static int Run()
        {
            var finger = new object();
            var other = new object();
            Case[] cases = new[]
            {
                new Case
                {
                    Name = "a tap",
                    Expected = true,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        return tap.Release(finger, new Point(120, 17), RowSize);
                    }
                },
                new Case
                {
                    // The bug, in one line: a finger that went down on a row
                    // and then dragged the page.
                    Name = "a scroll that starts on the row",
                    Expected = false,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        tap.Moved(finger, new Point(121, 30));
                        tap.Moved(finger, new Point(121, 90));
                        return tap.Release(finger, new Point(121, 140), RowSize);
                    }
                },
                new Case
                {
                    // And the same scroll ending back where it began, which is
                    // what a flick and a correction look like. Once given up,
                    // a gesture does not come back.
                    Name = "a scroll that ends where it started",
                    Expected = false,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        tap.Moved(finger, new Point(120, 70));
                        tap.Moved(finger, new Point(120, 17));
                        return tap.Release(finger, new Point(120, 17), RowSize);
                    }
                },
                new Case
                {
                    // A hand is not a mouse: a few points of travel is a tap
                    // and has to stay one, or the settings stop answering.
                    Name = "a finger that wobbles under the slop",
                    Expected = true,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        tap.Moved(finger, new Point(124, 21));
                        tap.Moved(finger, new Point(126, 14));
                        return tap.Release(finger, new Point(126, 14), RowSize);
                    }
                },
                new Case
                {
                    // What the scroll gesture above does when it decides the
                    // drag is its: it takes the pointer, and the row hears
                    // about it as OnPointerCaptureLost.
                    Name = "the scroller takes the pointer",
                    Expected = false,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        tap.Cancel();
                        return tap.Release(finger, new Point(120, 20), RowSize);
                    }
                },
                new Case
                {
                    Name = "a release outside the row",
                    Expected = false,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        return tap.Release(finger, new Point(120, 96), RowSize);
                    }
                },
                new Case
                {
                    // A second finger letting go somewhere else is not this
                    // row's release, and must not spend the first one either.
                    Name = "another finger's release",
                    Expected = false,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        return tap.Release(other, new Point(120, 17), RowSize);
                    }
                },
                new Case
                {
                    Name = "the first finger, after the second let go",
                    Expected = true,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(finger, new Point(120, 17), drags: true);
                        tap.Release(other, new Point(120, 17), RowSize);
                        return tap.Release(finger, new Point(120, 17), RowSize);
                    }
                },
                new Case
                {
                    // The desktop keeps what it had. A mouse scrolls with the
                    // wheel, so a button held across a row is not a scroll and
                    // the distance rule does not apply to it.
                    Name = "a mouse dragged across the row",
                    Expected = true,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(other, new Point(40, 17), drags: false);
                        tap.Moved(other, new Point(300, 20));
                        return tap.Release(other, new Point(300, 20), RowSize);
                    }
                },
                new Case
                {
                    Name = "a mouse released off the row",
                    Expected = false,
                    Run = () =>
                    {
                        var tap = new Tap();
                        tap.Press(other, new Point(40, 17), drags: false);
                        return tap.Release(other, new Point(40, 200), RowSize);
                    }
                },
                // SliderRow's own question: whose gesture is this drag?
                new Case
                {
                    Name = "a slider dragged along its track",
                    Expected = true,
                    Run = () => Tap.Sideways(new Vector(40, 6))
                },
                new Case
                {
                    Name = "a page scrolled past a slider",
                    Expected = false,
                    Run = () => Tap.Sideways(new Vector(6, 40))
                },
                new Case
                {
                    Name = "a diagonal that is mostly sideways",
                    Expected = true,
                    Run = () => Tap.Sideways(new Vector(-30, 25))
                },
                new Case
                {
                    Name = "a slider nudged under the slop",
                    Expected = false,
                    Run = () => Tap.Sideways(new Vector(5, 0))
                }
            };

            var failed = new List<string>();
            foreach (Case test in cases)
            {
                bool acted = test.Run();
                bool ok = acted == test.Expected;
                if (!ok)
                {
                    failed.Add(test.Name);
                }
                Console.WriteLine($"TAP {(ok ? "ok  " : "FAIL")} {test.Name}"
                    + $" | acted {acted}, wanted {test.Expected}");
            }
            Console.WriteLine(failed.Count == 0
                ? $"TAP all {cases.Length} cases pass (slop {Tap.Slop} points)"
                : $"TAP {failed.Count} case(s) FAILED: {String.Join(", ", failed)}");
            return failed.Count;
        }
    }
}
