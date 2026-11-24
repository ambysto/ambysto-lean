using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Ambysto.Lean.Core.Measure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.UiTests.Support;

/// <summary>ASP-20 phase B ("Testing without touching real caches"): a copy of <c>Ambysto.Lean.FakeTool</c> as <c>uv.exe</c> in a
/// temp folder (its name holds a space, so the confirmation must quote the path), a cache folder of three 1 MiB files that the
/// fake tool names for <c>cache dir</c> and empties for <c>cache clean</c>, and the file where it logs each call. The app is started with
/// <see cref="Variables"/>: PATH with the tool's folder first. No real tool's cache is ever cleaned by a UI test.</summary>
internal sealed class FakeUv : IDisposable
{
    public FakeUv(string tag, bool withPip = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "LeanUiFakeUv " + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        ToolFolder = Path.Combine(Root, "tool");
        CacheFolder = Path.Combine(Root, "cache");
        Log = Path.Combine(Root, "faketool.log");
        Directory.CreateDirectory(ToolFolder);
        Directory.CreateDirectory(CacheFolder);
        var source = Path.Combine(AppContext.BaseDirectory, "FakeTool");
        Assert.IsTrue(File.Exists(Path.Combine(source, "uv.exe")), "the fake tool was not built next to the tests: " + source);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(ToolFolder, Path.GetFileName(file)));
        }

        for (var i = 0; i < 3; i++)
        {
            var block = new byte[1 << 20];
            for (var j = 0; j < block.Length; j++)
            {
                block[j] = (byte)((j * 31) + (i * 131) + 7);
            }

            File.WriteAllBytes(Path.Combine(CacheFolder, "package-" + i + ".bin"), block);
        }

        CacheBytes = Measure(CacheFolder);
        Assert.IsTrue(CacheBytes >= 3 * ByteUnits.MB, "precondition: the fake cache holds its three files: " + CacheBytes);

        if (withPip)
        {
            // A second Ready row without a real tool: the same fake exe as python.exe (it plays pip by its process name), with
            // its own cache folder. Opt-in, so the other tests keep their single fake row.
            PipCacheFolder = Path.Combine(Root, "pipcache");
            Directory.CreateDirectory(PipCacheFolder);
            File.Copy(ToolPath, PipToolPath);
            for (var i = 0; i < 3; i++)
            {
                File.WriteAllBytes(Path.Combine(PipCacheFolder, "wheel-" + i + ".bin"), new byte[1 << 20]);
            }

            PipCacheBytes = Measure(PipCacheFolder);
            Assert.IsTrue(PipCacheBytes >= 3 * ByteUnits.MB, "precondition: the fake pip cache holds its three files: " + PipCacheBytes);
        }
    }

    /// <summary>The fake pip's cache folder (null unless <c>withPip</c>).</summary>
    public string? PipCacheFolder { get; }

    /// <summary>The fake tool as python.exe (the pip row's program), next to uv.exe.</summary>
    public string PipToolPath => Path.Combine(ToolFolder, "python.exe");

    public long PipCacheBytes { get; }

    public int PipCleanCalls() => Calls().Count(c => c == "-m pip cache purge");

    public bool PipCacheIsEmpty() => PipCacheFolder is null || Directory.GetFileSystemEntries(PipCacheFolder).Length == 0;

    public string Root { get; }

    public string ToolFolder { get; }

    public string CacheFolder { get; }

    public string Log { get; }

    /// <summary>The fake tool as the app will find it on PATH.</summary>
    public string ToolPath => Path.Combine(ToolFolder, "uv.exe");

    /// <summary>The bytes the app shows for the fake cache: the allocated size of its folder, measured with Lean's own scanner.</summary>
    public long CacheBytes { get; }

    /// <summary>The variables for <see cref="AppSession.StartWithEnvironment"/>: PATH with the tool's folder first, the log and the
    /// cache named; <paramref name="extra"/> adds LEAN_FAKETOOL_EXIT or LEAN_FAKETOOL_SLEEP_MS.</summary>
    public Dictionary<string, string> Variables(params (string Name, string Value)[] extra)
    {
        var variables = new Dictionary<string, string>
        {
            ["PATH"] = ToolFolder + ";" + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty),
            ["LEAN_FAKETOOL_LOG"] = Log,
            ["LEAN_FAKETOOL_CACHE"] = CacheFolder,
        };
        if (PipCacheFolder is not null)
        {
            variables["LEAN_FAKETOOL_CACHE_PIP"] = PipCacheFolder;
        }

        foreach (var (name, value) in extra)
        {
            variables[name] = value;
        }

        return variables;
    }

    /// <summary>One line per call the fake tool has answered so far (its arguments).</summary>
    public IReadOnlyList<string> Calls()
    {
        if (!File.Exists(Log))
        {
            return [];
        }

        using var stream = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
    }

    public int CleanCalls() => Calls().Count(c => c == "cache clean");

    public bool CacheIsEmpty() => Directory.GetFileSystemEntries(CacheFolder).Length == 0;

    /// <summary>Starts the fake tool as a separate <c>uv.exe</c> that does nothing for <paramref name="sleepMs"/> ms: a uv that
    /// "runs". The caller ends it (by its id) when the test is done.</summary>
    public Process StartSleeper(int sleepMs)
    {
        var info = new ProcessStartInfo(ToolPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ToolFolder };
        info.Environment["LEAN_FAKETOOL_SLEEP_MS"] = sleepMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment.Remove("LEAN_FAKETOOL_LOG");
        return Process.Start(info)!;
    }

    private static long Measure(string folder) =>
        System.Threading.Tasks.Task.Run(() => new Scanner().ScanAsync(folder, null, default)).GetAwaiter().GetResult().Root.AllocatedBytes;

    public void Dispose()
    {
        // A fake tool the app started and left sleeping (a test that failed early) must not make the next test's uv row "running".
        foreach (var process in Process.GetProcessesByName("uv").Concat(Process.GetProcessesByName("python")))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, ToolPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(process.MainModule?.FileName, PipToolPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(5_000);
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // not ours, or already gone
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a sleeping fake tool may still hold its exe for a moment; the temp folder is the OS's to clean
        }
    }
}

/// <summary>Keeps this user's command log (<c>%LOCALAPPDATA%\Ambysto\Space\commands.jsonl</c>) as it was: the clean runs of a test
/// are recorded there, and the original is put back byte for byte (or the file removed) on dispose. Dispose only after the app
/// has exited.</summary>
internal sealed class CommandLogBackup : IDisposable
{
    private readonly byte[]? original;
    private readonly DateTime originalWriteUtc;

    public CommandLogBackup()
    {
        if (File.Exists(Path))
        {
            original = File.ReadAllBytes(Path);
            originalWriteUtc = File.GetLastWriteTimeUtc(Path);
        }
    }

    public static string Path { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ambysto", "Space", "commands.jsonl");

    /// <summary>The log now holds exactly <paramref name="lines"/>, one per line (no line at all: an empty file).</summary>
    public static void Write(IEnumerable<string> lines)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, string.Concat(lines.Select(l => l + "\n")), new UTF8Encoding(false));
    }

    /// <summary>The non-blank lines of the log now (empty when there is no file).</summary>
    public static List<string> Lines()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    /// <summary>One command-log line in docs/spec/05's format; the optional pieces can be damaged on purpose by passing raw JSON
    /// for <paramref name="argumentsJson"/>.</summary>
    public static string Line(string id, DateTimeOffset time, string entryId, string program, string location,
        string argumentsJson = "[\"cache\",\"clean\"]", long bytesBefore = 3145728)
    {
        string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);
        return "{\"id\":" + Quote(id)
            + ",\"time\":" + Quote(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture))
            + ",\"entryId\":" + Quote(entryId)
            + ",\"program\":" + Quote(program)
            + ",\"arguments\":" + argumentsJson
            + ",\"location\":" + Quote(location)
            + ",\"bytesBefore\":" + bytesBefore.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"bytesAfter\":0,\"exitCode\":0,\"timedOut\":false,\"result\":\"cleaned\",\"reason\":null,\"appVersion\":\"0.0.0-uitest\"}";
    }

    public void Dispose()
    {
        if (original is not null)
        {
            File.WriteAllBytes(Path, original);
            File.SetLastWriteTimeUtc(Path, originalWriteUtc);
        }
        else if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
