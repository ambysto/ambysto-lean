# Ambysto Lean

Ambysto Lean shows where your disk space went and helps you reclaim it safely. Every action is approved by you and can
be undone. Lean only moves items to the Recycle Bin, never deletes them for good, and keeps a journal so you can Put
back what you moved. It runs on Windows 11 24H2 or later (x64).

## Free and Pro

Lean is open source and free to use. Free covers scanning, the volume card and map, large and old files, duplicates,
temporary files, empty folders and broken shortcuts, uninstall with leftovers, the Apps and caches lists, move to the
Recycle Bin with every safety check, and Put back (the journal shows the last 30 days).

Pro adds: open at login, the low-disk alert, report export (Markdown and JSON), Suggest, clearing known caches with the
tool's own command, the full journal history, and no purchase reminder. A licence key unlocks them; you enter it in
About. The full table is in [`docs/spec/06-free-pro.md`](docs/spec/06-free-pro.md). Pro is paid only if you are happy
to pay: "We only charge when you are happy to pay."

Where the Pro code lives:

- Open at login, the low-disk alert, report export and the purchase reminder are implemented in a separate private
  module that is not in this repository. A build from this repository has them locked.
- The other Pro features are in this repository and are switched on by the licence key check. The key is verified
  offline. Like any check in open source code, it can be edited out of your own build; what a licence gets you is the
  signed build, updates and support, and it is paid on trust.

A signed build with everything comes through the normal download and licence route on https://lean.ambysto.com/ .

## Build

This builds Lean without the Pro module. You need Windows 11 and the .NET 10 SDK, with Visual Studio or the `dotnet`
command line.

```sh
dotnet build windows/Ambysto.Lean.slnx -c Release
dotnet test windows/Ambysto.Lean.slnx -c Release --filter "TestCategory!=Console&TestCategory!=Admin&TestCategory!=ProDebug"
```

The Console and Admin test suites need an interactive session and elevation, so the command above leaves them out.

The app depends on two packages of the Ambysto Kit, `Ambysto.Kit.License` 0.1.2 and `Ambysto.Kit.Safety` 0.2.0, by exact
version. They are published on nuget.org, so a build needs no account and no token. The Kit source is at
https://github.com/ambysto/ambysto-kit .

## Safety rules

- Nothing is deleted for good: items go only to the Recycle Bin, after you approve, with a journal and Put back.
- Nothing destructive is pre-selected. Uninstall pre-selects only the app itself.

The platform-neutral specification is in [`docs/spec/`](docs/spec/).

## Contributing

Issues and pull requests are welcome. A change to safety behaviour (what may be moved, protected folders) must update
the specs in `docs/spec/` in the same pull request. The tripwire scripts `scripts/windows-choke-points.sh` and
`scripts/copy-lint.sh` must pass.

## Licence

Apache License 2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE). For the "Ambysto" name and logo, see
[TRADEMARK.md](TRADEMARK.md).
