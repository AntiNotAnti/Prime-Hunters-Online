using System;
using System.IO;
using System.Text;

namespace MphRead.Droid
{
    /// <summary>
    /// Sends everything the shared code prints to logcat.
    ///
    /// The engine, the launcher and the map generator all report through
    /// Console.WriteLine, and on a debug build mono redirects that to logcat
    /// for free. A release build does not: the output goes nowhere, so the one
    /// class of bug that only happens in release is also the one class with no
    /// diagnostics. That is not hypothetical -- a map file that silently
    /// failed to unpack printed exactly the line that would have explained it,
    /// into a stream nobody was reading.
    /// </summary>
    internal sealed class AndroidConsole : TextWriter
    {
        private const string Tag = "FruityPrime";
        private readonly StringBuilder _line = new StringBuilder();

        public override Encoding Encoding => Encoding.UTF8;

        public static void Install()
        {
            try
            {
                var writer = new AndroidConsole();
                Console.SetOut(writer);
                Console.SetError(writer);
            }
            catch (Exception)
            {
                // Nothing to report it with, and a game that will not start
                // because its logging would not start is worse than a quiet one.
            }
        }

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
                return;
            }
            if (value != '\r')
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
            {
                return;
            }
            foreach (char c in value)
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Flush();
        }

        public override void Flush()
        {
            if (_line.Length == 0)
            {
                return;
            }
            Android.Util.Log.Info(Tag, _line.ToString());
            _line.Clear();
        }
    }
}
