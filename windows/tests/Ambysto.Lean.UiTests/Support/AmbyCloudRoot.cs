using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>Copied from ambysto-space windows/tests/Ambysto.Lean.Core.Tests/Support/CloudRoot.cs (ASP-15 UI tests; the stale-root
/// sweep is left out, the UI tests unregister their root on dispose). Drives the test machine's fake cloud provider, AmbyCloud (<c>the test-machine notes</c>, section "AmbyCloud";
/// binary at <see cref="ExePath"/>): registers a sync root, creates real online-only placeholders (Cloud Files API,
/// in sync and dehydrated, as OneDrive's are), serves them with a log that records every fetch with the reader's pid,
/// and reads <c>status</c> (bytes on disk; it never hydrates) as a second oracle. Test-project only.</summary>
internal sealed class AmbyCloudRoot : IDisposable
{
    public const string ExePath = @"C:\Ambysto\tools\AmbyCloud\AmbyCloud.exe";

    private Process? serve;
    private int baseline;

    private AmbyCloudRoot(string root, string logPath)
    {
        Root = root;
        LogPath = logPath;
    }

    /// <summary>The registered sync root.</summary>
    public string Root { get; }

    /// <summary>The provider's JSON-lines log.</summary>
    public string LogPath { get; }

    /// <summary>Inconclusive when the provider is not installed on this machine (the only reason to skip a cloud test).</summary>
    public static void RequireInstalled()
    {
        if (!File.Exists(ExePath))
        {
            Assert.Inconclusive("the AmbyCloud fake cloud provider is not at " + ExePath + " (the test-machine notes, AmbyCloud)");
        }
    }

    public string Combine(string relative) => Path.Combine(Root, relative);

    /// <summary>Registers <paramref name="root"/> (created if needed) as a sync root of the fake provider.</summary>
    public static AmbyCloudRoot Register(string root, string logPath)
    {
        RequireInstalled();
        Directory.CreateDirectory(root);
        try
        {
            Run("register", "--root", root);
        }
        catch (AssertFailedException)
        {
            // A registration that failed partway may still have left a sync root behind: take it back before failing.
            TryUnregister(root);
            throw;
        }

        return new AmbyCloudRoot(root, logPath);
    }

    private static void TryUnregister(string root)
    {
        try
        {
            RunRaw("unregister", "--root", root);
        }
        catch (AssertFailedException)
        {
            // Not registered (any more).
        }
    }

    /// <summary>Creates online-only file placeholders of the given sizes (a trailing backslash makes a folder).</summary>
    public void Populate(params (string Path, long Size)[] entries)
    {
        var manifest = Path.Combine(Path.GetDirectoryName(Root)!, "manifest-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var items = entries.Select(e => e.Path.EndsWith('\\') ? (object)new { path = e.Path, dir = true } : new { path = e.Path, size = e.Size });
        File.WriteAllText(manifest, JsonSerializer.Serialize(items));
        try
        {
            var lines = Run("populate", "--root", Root, "--manifest", manifest);
            var summary = lines.Last(l => l.TryGetProperty("event", out var e) && e.GetString() == "populated");
            Assert.AreEqual(0, summary.GetProperty("failed").GetInt32(), "populate reported failures: " + string.Join("\n", lines.Select(l => l.ToString())));
        }
        finally
        {
            File.Delete(manifest);
        }
    }

    /// <summary>Starts the provider; it stops when its stdin is closed (<see cref="StopServe"/>).</summary>
    public void Serve()
    {
        var info = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "serve", "--root", Root, "--log", LogPath })
        {
            info.ArgumentList.Add(a);
        }

        serve = Process.Start(info)!;
        var line = serve.StandardOutput.ReadLine();
        if (line is null || !line.Contains("\"serving\"", StringComparison.Ordinal))
        {
            var error = serve.StandardError.ReadToEnd();
            StopServe();
            Assert.Fail($"AmbyCloud did not start serving: {line}\n{error}");
        }

        baseline = Fetches().Count;
    }

    /// <summary>Makes <paramref name="relative"/> online-only again (a hydrated file the provider takes back, as OneDrive's
    /// "Free up space" does). AmbyCloud dehydrates only as the connected provider, so the serve process is stopped first
    /// and started again after (with a new fetch baseline).</summary>
    public void Dehydrate(string relative)
    {
        StopServe();
        var lines = Run("dehydrate", "--root", Root, "--path", relative);
        var done = lines.Last(l => l.TryGetProperty("event", out var e) && e.GetString() == "dehydrate-done");
        Assert.AreEqual(1, done.GetProperty("dehydrated").GetInt32(), "dehydrate " + relative + ": " + string.Join("\n", lines.Select(l => l.ToString())));
        Serve();
    }

    /// <summary>Fetches up to now are not the code under test's (after a positive control, for example).</summary>
    public void MarkFetchBaseline() => baseline = Fetches().Count;

    /// <summary>The fetch lines that name <paramref name="relative"/>.</summary>
    public IReadOnlyList<string> FetchesOf(string relative) =>
        Fetches().Where(line => line.Contains("\"path\":\"" + relative.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"", StringComparison.OrdinalIgnoreCase)).ToList();

    public void StopServe()
    {
        if (serve is null)
        {
            return;
        }

        try
        {
            serve.StandardInput.Close();
            if (!serve.WaitForExit(10_000))
            {
                serve.Kill(entireProcessTree: true);
                serve.WaitForExit(5_000);
            }
        }
        finally
        {
            serve.Dispose();
            serve = null;
        }
    }

    /// <summary>The log lines that record a fetch (a download the code under test must never cause).</summary>
    public IReadOnlyList<string> Fetches()
    {
        if (!File.Exists(LogPath))
        {
            return [];
        }

        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var fetches = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("\"event\":\"fetch\"", StringComparison.Ordinal))
            {
                fetches.Add(line);
            }
        }

        return fetches;
    }

    public void AssertNoFetch(string after)
    {
        var fetches = Fetches().Skip(baseline).ToList();
        Assert.AreEqual(0, fetches.Count, "a download was started (" + after + "):\n" + string.Join("\n", fetches));
    }

    /// <summary>The provider's own view of one entry: bytes on disk (−1 when unknown), the recall attribute and whether it is a placeholder.</summary>
    public (long OnDisk, long Size, bool RecallOnDataAccess, bool IsPlaceholder) Status(string relative)
    {
        var text = string.Join("", RunRaw("status", "--root", Root));
        using var doc = JsonDocument.Parse(text);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!string.Equals(entry.GetProperty("path").GetString(), relative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var onDisk = entry.TryGetProperty("onDisk", out var od) && od.ValueKind == JsonValueKind.Number ? od.GetInt64() : -1;
            return (onDisk, entry.GetProperty("size").GetInt64(), entry.GetProperty("recallOnDataAccess").GetBoolean(), entry.GetProperty("isPlaceholder").GetBoolean());
        }

        Assert.Fail($"AmbyCloud status does not list {relative}");
        return default;
    }

    /// <summary>Fails unless <paramref name="relative"/> is an online-only placeholder with nothing on disk.</summary>
    public void RequireOnlineOnly(string relative)
    {
        var status = Status(relative);
        Assert.IsTrue(status.IsPlaceholder, relative + " is not a placeholder");
        Assert.IsTrue(status.RecallOnDataAccess, relative + " has no recall-on-data-access attribute");
        Assert.AreEqual(0L, status.OnDisk, relative + " has bytes on disk");
    }

    private static List<JsonElement> Run(params string[] args)
    {
        var parsed = new List<JsonElement>();
        foreach (var line in RunRaw(args))
        {
            if (line.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(line);
                parsed.Add(document.RootElement.Clone());
            }
        }

        return parsed;
    }

    private static List<string> RunRaw(params string[] args)
    {
        var info = new ProcessStartInfo(ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        var output = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            output.Add(line);
        }

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Assert.Fail($"AmbyCloud '{string.Join(' ', args)}' exited {process.ExitCode}:\n{string.Join("\n", output)}\n{error}");
        }

        return output;
    }

    public void Dispose()
    {
        try
        {
            StopServe();
        }
        finally
        {
            // Whatever happened to the serve process, the root is unregistered: a registered root left behind would
            // make every later test on this machine wrong.
            TryUnregister(Root);
        }

        for (var i = 0; i < 5 && File.Exists(LogPath); i++)
        {
            try
            {
                File.Delete(LogPath);
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
