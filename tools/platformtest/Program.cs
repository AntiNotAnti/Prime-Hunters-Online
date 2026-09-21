using System;
using System.IO;
using MphRead.Mods.Platform;

string root = Path.Combine(Path.GetTempPath(), "path fixture with spaces");
string bundle = Path.Combine(root, "Project Prime.app", "Contents");
string executable = Path.Combine(bundle, "MacOS");
string resources = Path.Combine(bundle, "Resources", "maps");
var cases = new (string Directory, string Expected)[]
{
    (executable, resources),
    (executable + Path.DirectorySeparatorChar, resources),
    // Ordinary publishes and developer builds retain their portable layout.
    (root, Path.Combine(root, "maps")),
    (Path.Combine(root, "Contents", "MacOS"),
        Path.Combine(root, "Contents", "MacOS", "maps"))
};
foreach (var item in cases)
{
    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", item.Directory);
    if (AppContext.BaseDirectory != item.Directory)
    {
        throw new Exception("Could not set the test executable directory.");
    }
    string expected = OperatingSystem.IsMacOS() ? item.Expected : Path.Combine(item.Directory, "maps");
    string actual = AppPaths.Maps;
    if (actual != expected)
    {
        throw new Exception($"Map path mismatch: expected {expected}, got {actual}");
    }
}
Console.WriteLine($"Platform map path regressions passed ({cases.Length} cases).");
