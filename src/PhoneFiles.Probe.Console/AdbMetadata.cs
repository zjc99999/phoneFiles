using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PhoneFiles.Probe.Console;

// Optional, read-only helper: fill device metadata from `adb shell getprop`
// so contributors don't have to hand-type OEM/model/OS/SDK (a common error source).
// This reads only build properties — no file access, nothing is uploaded.
// ADB is opt-in via --from-adb; MTP-only contributors can ignore it entirely.
internal static class AdbMetadata
{
    public static bool TryLoad(Dictionary<string, string> opts, out string summary)
    {
        summary = "";
        var adb = ResolveAdb(opts);
        if (adb is null)
        {
            summary = "adb not found (set --adb PATH or ANDROID_HOME); keeping manual metadata";
            return false;
        }

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] keys =
        {
            "ro.product.manufacturer", "ro.product.model",
            "ro.build.version.release", "ro.build.version.sdk",
            "ro.build.fingerprint", "ro.build.display.id"
        };
        foreach (var k in keys)
            props[k] = Run(adb, $"shell getprop {k}") ?? "";

        // Fill only fields the contributor did NOT explicitly provide.
        Fill(opts, "oem", Norm(props.GetValueOrDefault("ro.product.manufacturer")));
        Fill(opts, "model", Norm(props.GetValueOrDefault("ro.product.model")));
        Fill(opts, "os", Norm(props.GetValueOrDefault("ro.build.version.release")));
        Fill(opts, "sdk", Norm(props.GetValueOrDefault("ro.build.version.sdk")));

        var fp = Norm(props.GetValueOrDefault("ro.build.fingerprint"));
        var display = Norm(props.GetValueOrDefault("ro.build.display.id"));
        var romBuild = !string.IsNullOrEmpty(display) ? display : fp;
        Fill(opts, "romBuild", romBuild);

        // Multi-source version check: fingerprint segment ":10/" vs getprop release.
        var fpVer = ExtractFingerprintVersion(fp);
        var release = Norm(props.GetValueOrDefault("ro.build.version.release"));
        var warn = "";
        if (!string.IsNullOrEmpty(fpVer) && !string.IsNullOrEmpty(release) && fpVer != release)
            warn = $" [WARN: getprop release={release} but fingerprint says Android {fpVer}; recorded getprop value, verify manually]";

        summary = $"adb={adb} model={Get(opts, "model")} os={Get(opts, "os")} sdk={Get(opts, "sdk")}{warn}";
        return true;
    }

    private static string? ResolveAdb(Dictionary<string, string> opts)
    {
        var explicitPath = Get(opts, "adb");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        foreach (var name in new[] { "adb.exe", "adb" })
        {
            var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                .Select(p => Path.Combine(p.Trim('"'), name)).FirstOrDefault(File.Exists);
            if (path is not null) return path;
        }
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ANDROID_HOME"), Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") })
            if (!string.IsNullOrEmpty(root))
            {
                var p = Path.Combine(root, "platform-tools", "adb.exe");
                if (File.Exists(p)) return p;
            }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        return File.Exists(local) ? local : null;
    }

    private static string? Run(string adb, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(adb, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return string.IsNullOrWhiteSpace(outp) ? null : outp.Trim();
        }
        catch { return null; }
    }

    private static void Fill(Dictionary<string, string> opts, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (!opts.ContainsKey(key) || string.IsNullOrEmpty(opts[key])) opts[key] = value;
    }

    private static string Get(Dictionary<string, string> opts, string key) =>
        opts.TryGetValue(key, out var v) ? v : "(unknown)";

    private static string? Norm(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim().Trim('\r', '\n');
        return v.Length == 0 || v.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : v;
    }

    // fingerprint format: brand/product/device:RELEASE/incremental/...:user/release-keys
    private static string? ExtractFingerprintVersion(string? fp)
    {
        if (string.IsNullOrEmpty(fp)) return null;
        var parts = fp.Split(':');
        return parts.Length >= 2 && parts[1].Length > 0 ? parts[1] : null;
    }
}
