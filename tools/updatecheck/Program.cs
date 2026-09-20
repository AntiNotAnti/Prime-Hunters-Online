using System;
using System.Text.Json;
using MphRead.Mods.Update;

int failures = 0;
void Check(bool pass, string description)
{
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {description}");
    if (!pass) failures++;
}
string Release(string tag) => JsonSerializer.Serialize(new
{
    tag_name = tag,
    assets = new[]
    {
        new
        {
            name = $"FruityPrime-{tag}-server-{UpdateCheck.Rid()}.zip",
            browser_download_url = $"https://github.com/example/releases/{tag}/server.zip",
            size = 100L,
            digest = "sha256:" + new string('a', 64)
        },
        new
        {
            name = $"FruityPrime-{tag}-{UpdateCheck.Rid()}.zip",
            browser_download_url = $"https://github.com/example/releases/{tag}/client.zip",
            size = 200L,
            digest = "sha256:" + new string('b', 64)
        }
    }
});
Check(BuildVersion.Parse("v1.0.0") == new Version(1, 0, 0), "1.0.0 is a valid release tag");
Check(BuildVersion.Parse("v1.0.0-rc1") == null, "prerelease channel stays manual");
Check(BuildVersion.Parse("local") == null, "explicit local stamp is not a release");
var update = UpdateCheck.Parse(Release("v1.0.0"), new Version(0, 9, 0));
Check(update?.Version == new Version(1, 0, 0), "0.9.0 can update to 1.0.0");
Check(update?.AssetName == $"FruityPrime-v1.0.0-{UpdateCheck.Rid()}.zip",
    "client picks the client package when the server asset is first");
Check(update?.AssetDigest == "sha256:" + new string('b', 64),
    "client keeps GitHub's SHA-256 release digest");
Check(UpdateDownload.SupportsDigest(update?.AssetDigest ?? ""),
    "GitHub SHA-256 digest is accepted for one-click install");
Check(!UpdateDownload.SupportsDigest("sha1:" + new string('b', 40)),
    "unsupported release digests are refused");
Check(UpdateCheck.Parse(Release("v1.0.0"), new Version(1, 0, 0)) == null,
    "same version is not offered again");
Check(UpdateCheck.Parse(Release("v1.0.0"), new Version(1, 0, 1)) == null,
    "a newer installation is not downgraded");
Check(UpdateCheck.Parse(Release("v1.1.0-rc1"), new Version(1, 0, 0)) == null,
    "prerelease is not offered as a stable update");
if (args.Length == 0)
    Check(!BuildVersion.IsRelease, "unstamped assembly remains a local build");
else
    Check(BuildVersion.Current == Version.Parse(args[0]), "assembly release stamp survives commit metadata");
return failures == 0 ? 0 : 1;
