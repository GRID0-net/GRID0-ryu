using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.Utilities;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems
{
    /// <summary>
    /// [GRID0] Keeps <see cref="PrivateServerSplatoon3Patches.PatchesDir"/> in step with the newest
    /// <c>patches-&lt;serial&gt;</c> release of GRID0plus-cfw, the same release the console Toolbox
    /// installs from (cfw repo, <c>patches/README.md</c>). No patch ships inside the emulator.
    /// </summary>
    /// <remarks>
    /// Runs at every startup. The SHA-256 of every file on disk is compared with the release
    /// manifest; if anything differs the player is asked, and on yes the files are installed from
    /// one download of <c>patches.zip</c>, each checked against the manifest before it replaces
    /// anything, and the emulator restarts: patches are applied when a game boots, so a game
    /// already running would otherwise keep the old ones. The folder is exclusively GRID0+'s, so
    /// any file the manifest does not list is deleted.
    /// </remarks>
    internal static partial class Grid0Patches
    {
        private const string Repo = "GRID0-net/GRID0plus-cfw";
        private const string ReleasesApi = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";
        private const string DownloadBase = "https://github.com/" + Repo + "/releases/download/";

        [GeneratedRegex(@"^patches-([0-9]+)$")]
        private static partial Regex PatchesTag();

        [GeneratedRegex(@"^exefs_patches/[A-Za-z0-9_.-]+/[A-Za-z0-9]+\.ips$")]
        private static partial Regex SafeDest();

        private sealed record Entry(string Dest, string Sha256, string Asset);

        public static async Task CheckAtStartupAsync()
        {
            try
            {
                await CheckAsync();
            }
            catch (Exception e)
            {
                Logger.Warning?.Print(LogClass.Application, $"[GRID0] Patch check failed: {e.Message}");

                if (!Directory.Exists(Path.Combine(PrivateServerSplatoon3Patches.PatchesDir, "exefs_patches")))
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ContentDialogHelper.CreateWarningDialog(
                        "GRID0+ patches could not be downloaded.",
                        "Splatoon 3 cannot connect to GRID0+ without them. Ryujinx tries again every time it starts."));
                }
            }
        }

        private static async Task CheckAsync()
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GRID0-Ryujinx");

            string tag = NewestTag(await http.GetStringAsync(ReleasesApi));
            List<Entry> entries = ParseManifest(await http.GetStringAsync(DownloadBase + tag + "/manifest.json"));

            string root = PrivateServerSplatoon3Patches.PatchesDir;
            List<Entry> needed = entries.Where(e => !Matches(Path.Combine(root, e.Dest), e.Sha256)).ToList();
            HashSet<string> listed = entries.Select(e => Path.GetFullPath(Path.Combine(root, e.Dest))).ToHashSet();
            List<string> stale = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(f => !listed.Contains(Path.GetFullPath(f))).ToList()
                : [];

            if (needed.Count == 0 && stale.Count == 0)
            {
                Logger.Info?.Print(LogClass.Application, $"[GRID0] Patches current ({tag}).");

                return;
            }

            int changes = needed.Count + stale.Count;
            bool accepted = false;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                accepted = await ContentDialogHelper.CreateChoiceDialog(
                    "GRID0+ patch update",
                    $"{changes} GRID0+ patch file{(changes == 1 ? "" : "s")} changed ({tag}).",
                    "Download now? Ryujinx restarts afterwards so the game uses them.");
            });

            if (!accepted)
            {
                return;
            }

            if (needed.Count > 0)
            {
                byte[] zipBytes = await http.GetByteArrayAsync(DownloadBase + tag + "/patches.zip");
                using ZipArchive zip = new(new MemoryStream(zipBytes), ZipArchiveMode.Read);

                // Everything is read and checked before the first file is written.
                List<(Entry Entry, byte[] Data)> files = [];
                foreach (Entry e in needed)
                {
                    ZipArchiveEntry z = zip.GetEntry(e.Asset) ?? throw new InvalidDataException($"patches.zip has no {e.Asset}");
                    using MemoryStream ms = new();
                    using (Stream s = z.Open())
                    {
                        s.CopyTo(ms);
                    }

                    byte[] data = ms.ToArray();
                    if (Convert.ToHexStringLower(SHA256.HashData(data)) != e.Sha256)
                    {
                        throw new InvalidDataException($"{e.Asset} does not match its manifest hash");
                    }

                    files.Add((e, data));
                }

                foreach ((Entry e, byte[] data) in files)
                {
                    string path = Path.Combine(root, e.Dest);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string tmp = path + ".tmp";
                    await File.WriteAllBytesAsync(tmp, data);
                    File.Move(tmp, path, overwrite: true);
                }
            }

            foreach (string f in stale)
            {
                File.Delete(f);
            }

            if (Directory.Exists(root))
            {
                foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                    {
                        Directory.Delete(d);
                    }
                }
            }

            Logger.Info?.Print(LogClass.Application, $"[GRID0] Patches updated to {tag}; restarting.");
            Restart();
        }

        private static string NewestTag(string releasesJson)
        {
            using JsonDocument doc = JsonDocument.Parse(releasesJson);
            string best = null;
            int bestSerial = -1;

            foreach (JsonElement r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean())
                {
                    continue;
                }

                Match m = PatchesTag().Match(r.GetProperty("tag_name").GetString() ?? string.Empty);
                if (m.Success && int.Parse(m.Groups[1].Value) > bestSerial)
                {
                    best = m.Value;
                    bestSerial = int.Parse(m.Groups[1].Value);
                }
            }

            return best ?? throw new InvalidDataException("no patches release found");
        }

        /// <summary>The release's emulator entries. Any malformed entry rejects the whole manifest.</summary>
        private static List<Entry> ParseManifest(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            List<Entry> entries = [];

            foreach (JsonElement p in doc.RootElement.GetProperty("patches").EnumerateArray())
            {
                if (!p.GetProperty("targets").EnumerateArray().Any(t => t.GetString() == "emulator"))
                {
                    continue;
                }

                string dest = p.GetProperty("dest").GetString() ?? string.Empty;
                string sha = p.GetProperty("sha256").GetString() ?? string.Empty;
                string asset = p.GetProperty("asset").GetString() ?? string.Empty;

                if (!SafeDest().IsMatch(dest) || sha.Length != 64 || asset != sha + ".ips")
                {
                    throw new InvalidDataException($"malformed manifest entry {dest}");
                }

                entries.Add(new Entry(dest, sha, asset));
            }

            return entries.Count > 0 ? entries : throw new InvalidDataException("manifest lists no emulator patches");
        }

        private static bool Matches(string path, string sha)
        {
            return File.Exists(path) && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) == sha;
        }

        private static void Restart()
        {
            // An AppImage must be relaunched as the AppImage, not the binary it mounted.
            string self = (OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("APPIMAGE") : null)
                          ?? Environment.ProcessPath!;

            Dispatcher.UIThread.Post(() =>
            {
                _ = Process.Start(self, CommandLineState.Arguments);
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                Environment.Exit(0);
            });
        }
    }
}
