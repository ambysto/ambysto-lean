using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ambysto.Lean.Core.Catalog;
using Ambysto.Lean.Core.Tests.Support;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ambysto.Lean.Core.Tests.Acceptance.Catalog;

/// <summary>One row of the plan's command table (plan "Commands"), written out here as literals so the tests do not
/// depend on the code under test for what the table says. <see cref="PlanTable.NpmScript"/> stands for
/// "&lt;folder of node.exe&gt;\node_modules\npm\bin\npm-cli.js".</summary>
internal sealed record Spec(string Id, string Program, string[] Clean, string[] Location, string[] Busy);

/// <summary>The plan's table and the output each tool prints for its location query.</summary>
internal static class PlanTable
{
    public const string NpmScript = "{npm-cli.js}";

    private static readonly string[] DotnetBusy = ["dotnet.exe", "MSBuild.exe", "devenv.exe", "nuget.exe"];

    public static readonly Spec[] All =
    [
        new("npm", "node.exe", [NpmScript, "cache", "clean", "--force"], [NpmScript, "config", "get", "cache"], ["node.exe"]),
        new("pip", "python.exe", ["-m", "pip", "cache", "purge"], ["-m", "pip", "cache", "dir"], ["python.exe", "pythonw.exe"]),
        new("uv", "uv.exe", ["cache", "clean"], ["cache", "dir"], ["uv.exe", "uvx.exe"]),
        new("nuget-http", "dotnet.exe", ["nuget", "locals", "http-cache", "--clear"], ["nuget", "locals", "http-cache", "--list"], DotnetBusy),
        new("nuget-plugins", "dotnet.exe", ["nuget", "locals", "plugins-cache", "--clear"], ["nuget", "locals", "plugins-cache", "--list"], DotnetBusy),
    ];

    public static string[] Ids => [.. All.Select(s => s.Id)];

    public static Spec Of(string id) => All.Single(s => s.Id == id);

    /// <summary>The arguments with npm's script path filled in for the program at <paramref name="programPath"/>.</summary>
    public static string[] Resolve(string[] arguments, string programPath) =>
        [.. arguments.Select(a => a == NpmScript ? Path.Combine(Path.GetDirectoryName(programPath)!, "node_modules", "npm", "bin", "npm-cli.js") : a)];

    /// <summary>What the tool prints for its location query when the cache is in <paramref name="folder"/>.</summary>
    public static string LocationOutput(string id, string folder) => id switch
    {
        "nuget-http" => "http-cache: " + folder + "\r\n",
        "nuget-plugins" => "plugins-cache: " + folder + "\r\n",
        _ => folder + "\r\n",
    };
}

/// <summary>An environment the test decides: allows (null) unless given a refusal reason.</summary>
internal sealed class FakeCommandEnvironment : ICommandEnvironment
{
    public string? Reason { get; set; }

    public int Asked { get; private set; }

    public string? Refusal()
    {
        Asked++;
        return Reason;
    }
}

internal enum CallKind
{
    Locate,
    Clean,
    Other,
}

internal sealed record RunCall(string Program, string[] Arguments, TimeSpan Timeout, string? Id, CallKind Kind);

internal sealed class FakeProgramFinder : IProgramFinder
{
    public Dictionary<string, string?> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Asked { get; } = [];

    public string? Find(string fileName)
    {
        Asked.Add(fileName);
        return Map.GetValueOrDefault(fileName);
    }
}

/// <summary>A runner that starts nothing: it recognises the plan's location queries and clean commands by their arguments
/// and answers from a per-entry handler. A call it does not recognise is recorded as <see cref="CallKind.Other"/>.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<RunCall> Calls { get; } = [];

    public Dictionary<string, Func<CommandRunResult>> Locate { get; } = [];

    public Dictionary<string, Func<CommandRunResult>> Clean { get; } = [];

    public IEnumerable<RunCall> CleanCalls => Calls.Where(c => c.Kind == CallKind.Clean);

    public IEnumerable<RunCall> LocateCalls => Calls.Where(c => c.Kind == CallKind.Locate);

    public IEnumerable<RunCall> OtherCalls => Calls.Where(c => c.Kind == CallKind.Other);

    public CommandRunResult Run(string programPath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var args = arguments.ToArray();
        var fileName = Path.GetFileName(programPath);
        foreach (var spec in PlanTable.All.Where(s => string.Equals(s.Program, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            if (args.SequenceEqual(PlanTable.Resolve(spec.Location, programPath)))
            {
                Calls.Add(new RunCall(programPath, args, timeout, spec.Id, CallKind.Locate));
                return Locate[spec.Id]();
            }

            if (args.SequenceEqual(PlanTable.Resolve(spec.Clean, programPath)))
            {
                Calls.Add(new RunCall(programPath, args, timeout, spec.Id, CallKind.Clean));
                return Clean[spec.Id]();
            }
        }

        Calls.Add(new RunCall(programPath, args, timeout, null, CallKind.Other));
        return new CommandRunResult(1, string.Empty, false);
    }
}

/// <summary>Everything a Command row test needs, in a scratch folder: a (zero-byte) program file for each program of the
/// table, a real cache folder holding one file for each entry, fakes for the finder and the runner, the process list the
/// code asks for, and a command log in the scratch folder. The default fake clean empties the folder and exits 0.</summary>
internal sealed class CatalogRig : IDisposable
{
    public CatalogRig(string name)
    {
        Temp = new TempFolder(name);
        Folders = new Dictionary<string, string>();
        foreach (var spec in PlanTable.All)
        {
            var programPath = Temp.File(@"tools\" + Path.GetFileNameWithoutExtension(spec.Program) + @"\" + spec.Program, 0);
            Finder.Map[spec.Program] = programPath;
            var folder = Temp.Folder("caches", spec.Id);
            File.WriteAllBytes(Path.Combine(folder, "cache.bin"), new byte[20000]);
            Folders[spec.Id] = folder;
            var id = spec.Id;
            Runner.Locate[id] = () => new CommandRunResult(0, PlanTable.LocationOutput(id, Folders[id]), false);
            Runner.Clean[id] = () =>
            {
                EmptyFolder(id);
                return new CommandRunResult(0, string.Empty, false);
            };
        }

        LogPath = Temp.Sub("log", "commands.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Log = CommandLog.Open(LogPath);
    }

    public TempFolder Temp { get; }

    public Dictionary<string, string> Folders { get; }

    public FakeProgramFinder Finder { get; } = new();

    /// <summary>The environment the code under test asks; allows unless a test sets <see cref="FakeCommandEnvironment.Reason"/>.</summary>
    public FakeCommandEnvironment Env { get; } = new();

    public FakeCommandRunner Runner { get; } = new();

    /// <summary>The image paths the process list reports; edit between calls.</summary>
    public List<string> Images { get; } = [];

    public string LogPath { get; }

    public CommandLog Log { get; }

    public Func<IReadOnlyList<string>> ProcessImages => () => [.. Images];

    public string ProgramPath(string id) => Finder.Map[PlanTable.Of(id).Program]!;

    public void EmptyFolder(string id)
    {
        foreach (var file in Directory.EnumerateFiles(Folders[id]))
        {
            File.Delete(file);
        }
    }

    /// <summary>The lines now in the command log file (none when the file does not exist).</summary>
    public string[] LogLines() => File.Exists(LogPath) ? [.. File.ReadAllLines(LogPath).Where(l => l.Length > 0)] : [];

    public Task<IReadOnlyList<CommandRow>> ReadRows() =>
        CommandRows.ReadAsync(Env, Finder, Runner, ProcessImages, CancellationToken.None);

    public CleanRunner NewCleanRunner(CommandLog? log = null) => new(Env, Finder, Runner, ProcessImages, log ?? Log);

    /// <summary>A row as the view would have handed it over when approved: the program and folder of this rig, in
    /// <paramref name="state"/>. A ProgramMissing row has no program or folder; a LocationUnknown row has no folder.</summary>
    public CommandRow Row(string id, CommandRowState state = CommandRowState.Ready) => new(
        AppCatalog.Entries.Single(e => e.Id == id),
        CleanCommands.All.Single(c => c.EntryId == id),
        state == CommandRowState.ProgramMissing ? null : ProgramPath(id),
        state is CommandRowState.ProgramMissing or CommandRowState.LocationUnknown ? null : Folders[id],
        state == CommandRowState.Empty ? 0 : 20000,
        state);

    public void Dispose() => Temp.Dispose();
}

/// <summary>A file that is a reparse point, for the tests of the "not an app execution alias" rule.</summary>
internal static class ReparseFiles
{
    /// <summary>A symbolic link to an .exe file made in <paramref name="temp"/> when this user may make one, otherwise an
    /// existing app execution alias under <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>; the test is Inconclusive when
    /// neither exists (a reparse point is never faked).</summary>
    public static string Require(TempFolder temp, string linkFileName)
    {
        var target = temp.File(@"real\target.exe", 10);
        var link = temp.Sub("decoy", linkFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, target);
            if (File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
            {
                return link;
            }
        }
        catch (IOException)
        {
            // No privilege for symbolic links: fall back to an existing alias below.
        }
        catch (UnauthorizedAccessException)
        {
        }

        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        if (Directory.Exists(aliases))
        {
            foreach (var file in Directory.EnumerateFiles(aliases, "*.exe"))
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                {
                    return file;
                }
            }
        }

        Assert.Inconclusive("no reparse-point file to test with: this user cannot create symbolic links and there is no app execution alias under WindowsApps");
        return string.Empty;
    }
}
