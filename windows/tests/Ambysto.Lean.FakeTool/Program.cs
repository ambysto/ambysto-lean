using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Ambysto.Lean.FakeTool;

/// <summary>Run as <c>uv.exe</c> by the Apps and caches UI tests. Test code: it deletes with File and Directory directly.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var line = string.Join(' ', args);
        var rawLine = line;
        // Run as python.exe (a copy of this exe, see FakeUv.WithPip) it plays pip: "-m pip cache dir" / "-m pip cache purge" are
        // answered like uv's "cache dir" / "cache clean", for the folder of LEAN_FAKETOOL_CACHE_PIP.
        var asPip = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "python", StringComparison.OrdinalIgnoreCase);
        if (asPip)
        {
            line = line switch { "-m pip cache dir" => "cache dir", "-m pip cache purge" => "cache clean", _ => line };
        }

        if (Environment.GetEnvironmentVariable("LEAN_FAKETOOL_LOG") is { Length: > 0 } log)
        {
            File.AppendAllText(log, rawLine + "\n", new UTF8Encoding(false));
        }

        var cache = Environment.GetEnvironmentVariable(asPip ? "LEAN_FAKETOOL_CACHE_PIP" : "LEAN_FAKETOOL_CACHE");
        if (line == "cache dir")
        {
            Console.Out.WriteLine(cache);
            return 0;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("LEAN_FAKETOOL_SLEEP_MS"), NumberStyles.None, CultureInfo.InvariantCulture, out var sleep) && sleep > 0)
        {
            Thread.Sleep(sleep);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("LEAN_FAKETOOL_EXIT"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exit))
        {
            Console.Error.WriteLine("fake uv: failing on purpose");
            return exit;
        }

        if (line == "cache clean" && !string.IsNullOrEmpty(cache) && Directory.Exists(cache))
        {
            foreach (var file in Directory.GetFiles(cache))
            {
                File.Delete(file);
            }

            foreach (var folder in Directory.GetDirectories(cache))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        return 0;
    }
}
