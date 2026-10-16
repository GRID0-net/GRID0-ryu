using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Mods;
using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// The binary patches Splatoon 3 needs before it will talk to a private server, applied
    /// from GRID0+'s own patch folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splatoon 3 carries its own statically linked gRPC/TLS stack and pins Nintendo's real
    /// certificate independently of the guest OS's <c>ssl:</c> service. Every other title
    /// that talks to a private server only needs <see cref="Services.Ssl.SslService.PrivateServerTrust"/>
    /// -- the emulated <c>ssl:</c> service trusting <c>PrivateServerCaBundle</c> is enough,
    /// because those titles actually ask that service to validate the peer. Splatoon 3 never
    /// does: its own compiled-in TLS stack checks the certificate itself, so the guest-OS-level
    /// trust callback is never consulted for its NPLN connections, and the TLS handshake never
    /// completes -- the game just sits on "connecting" forever. This is documented at length,
    /// against real hardware, in GRID0+'s own <c>switch/README.md</c> ("Part 3: Splatoon 3
    /// pins its own certificates"), which is where these patches come from.
    /// </para>
    /// <para>
    /// The patch bytes are the real <c>.ips</c> files of the
    /// <see href="https://github.com/GRID0-net/GRID0plus-cfw">GRID0plus-cfw</see> repository's
    /// <c>patches/</c> directory -- the exact files a real console applies through Atmosphère.
    /// None ship inside the emulator: <c>Grid0Patches</c> downloads the newest
    /// <c>patches-&lt;serial&gt;</c> release into <see cref="PatchesDir"/> at startup, laid out
    /// as <c>exefs_patches/&lt;category&gt;/&lt;buildId&gt;.ips</c>, the same paths the release
    /// manifest names. Only the release's Splatoon 3 game patches are downloaded: its
    /// <c>bcat</c>, system <c>ssl:</c> and browser patches target modules this emulator
    /// high-level-emulates rather than running as a guest NSO, so they could never match a
    /// loaded build id. The <c>.ips</c> bytes carry no Nintendo code and neither create nor
    /// impersonate a Nintendo signature.
    /// </para>
    /// <para>
    /// <b>This is gated on Splatoon 3's exact build, not its version number or the emulator's
    /// firmware</b>, by the build id in each <c>.ips</c> file name (trailing zeros trimmed, the
    /// same shape <see cref="ModLoader"/> computes for every loaded NSO). A title update changes
    /// the game's own binary and silently stops these patches matching -- the same failure mode
    /// <c>switch/README.md</c> warns about for the disk-based version of this patch. A build
    /// with no matching file gets nothing, exactly as an unmatched <c>exefs_patches</c> file
    /// would: if Splatoon 3 stops connecting after an update, publishing the new build's patches
    /// is the first thing to try.
    /// </para>
    /// </remarks>
    public static class PrivateServerSplatoon3Patches
    {
        public const ulong Splatoon3ApplicationId = 0x0100C2500FC20000;

        /// <summary>
        /// Where the downloaded patches live. Exclusively GRID0+'s: the updater deletes anything
        /// in it the newest manifest does not list.
        /// </summary>
        public static string PatchesDir => Path.Combine(AppDataManager.BaseDirPath, "grid0plus", "patches");

        /// <summary>
        /// Older Splatoon 3 builds the patch release has no <c>.ips</c> files for, mapped to the build
        /// whose files carry the same bytes and the categories of those files that apply.
        /// </summary>
        /// <remarks>
        /// These two builds were covered before the toolbox files were embedded, by the same bytes
        /// as 11.3.0's <c>s3grpcverify_bypass</c> (the pinned-certificate check, 0x00157B20) and
        /// <c>s3grpcpeer_bypass</c> (the peer-hostname comparison, 0x0014E1B0 and 0x0014DD80):
        /// 11.3.0's binary only grew after both sites, so 11.2.0 shares them unchanged. The
        /// toolbox's newer categories (<c>s3certpin_bypass</c>, <c>s3verifyoption_bypass</c> and
        /// <c>s3smallmatch_bypass</c>) sit elsewhere in the binary and have never been confirmed
        /// for these builds, so they are not applied to them. The oldest build was only ever recorded with the certificate
        /// bypass -- its peer-hostname offsets were never established -- and stays that way rather
        /// than being guessed at.
        /// </remarks>
        private static readonly Dictionary<string, (string PatchBuildId, string[] Categories)> _sharedSites = new()
        {
            // 11.2.0
            ["6830B3A12406CB4716FEC5ADDC35D3E2DC92D212"] =
                ("28C4287AEE36F7499DA60F3E68B54C70DA382D75", ["s3grpcverify_bypass", "s3grpcpeer_bypass"]),

            // An older build, certificate-pinning check only.
            ["726D2B882DD9EF10F4A9D73EED088740630FB6C8"] =
                ("28C4287AEE36F7499DA60F3E68B54C70DA382D75", ["s3grpcverify_bypass"]),
        };

        /// <summary>
        /// Adds every embedded patch whose file name matches <paramref name="buildId"/> to
        /// <paramref name="target"/>. Returns how many were added (0 for an unrecognised build).
        /// </summary>
        internal static int Apply(string buildId, MemPatch target)
        {
            if (string.IsNullOrEmpty(buildId))
            {
                return 0;
            }

            string[] onlyCategories = null;
            string patchBuildId = buildId;

            if (_sharedSites.TryGetValue(buildId, out var shared))
            {
                patchBuildId = shared.PatchBuildId;
                onlyCategories = shared.Categories;
            }

            string categoriesDir = Path.Combine(PatchesDir, "exefs_patches");
            if (!Directory.Exists(categoriesDir))
            {
                Logger.Warning?.Print(LogClass.ModLoader,
                    $"Splatoon 3: no GRID0+ patches in {PatchesDir}; restart the emulator online to download them");

                return 0;
            }

            int applied = 0;

            foreach (string category in Directory.GetDirectories(categoriesDir))
            {
                if (onlyCategories != null && Array.IndexOf(onlyCategories, Path.GetFileName(category)) < 0)
                {
                    continue;
                }

                // The 40-digit name only. The release also carries a 64-digit copy of the same
                // bytes for Atmosphère versions that match that form; reading both would apply
                // every patch twice.
                string file = Path.Combine(category, patchBuildId + ".ips");
                if (!File.Exists(file))
                {
                    continue;
                }

                using FileStream stream = File.OpenRead(file);
                using BinaryReader reader = new(stream);
                new IpsPatcher(reader).AddPatches(target);
                applied++;
            }

            if (applied > 0)
            {
                Logger.Info?.Print(LogClass.ModLoader,
                    $"Splatoon 3: applied {applied} GRID0+ private-server patch(es) for build {buildId}");
            }

            return applied;
        }
    }
}
