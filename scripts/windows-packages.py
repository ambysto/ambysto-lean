"""Lean's package check for Windows (intent Constraints): the only packages are Microsoft's Windows App SDK, MSTest, the
Ambysto Kit packages, and WixToolset.Sdk (owner decision Q1) and WixToolset.UI.wixext (owner decision 2026-10-18) in windows/installer/. Every package has one exact version
(no range, no wildcard, no property). A Kit package from a prerelease (`0.2.0-local.<commit>`, packed from the Kit's main
before its tag) fails, unless the marker file KIT-LOCAL sits in the folder checked: phase B's development branch only,
never merged (the merge PR deletes the marker and switches to the tagged version; a marker with no prerelease Kit fails).
Reads every MSBuild file under the folder given (default windows/) and global.json; bin/ and obj/ are skipped.
Usage: python3 -I windows-packages.py [dir]"""
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ALLOWED = {"Microsoft.WindowsAppSDK", "MSTest", "Ambysto.Kit.License", "Ambysto.Kit.Safety"}
ALLOWED_SDKS = {"Microsoft.NET.Sdk"}
INSTALLER_ONLY = {"WixToolset.Sdk", "WixToolset.UI.wixext"}  # the UI extension: owner decision 2026-10-18

root = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else pathlib.Path(__file__).resolve().parent.parent / "windows")
problems = []
local_kit = (root / "KIT-LOCAL").is_file()
prerelease_kit = []
EXACT = re.compile(r"^[0-9]+(\.[0-9]+){1,3}(-[0-9A-Za-z.-]+)?$")


def check_version(name, version, path):
    rel = path.relative_to(root)
    if version is None or not EXACT.match(version.strip()):
        problems.append(f"{rel}: {name} needs one exact version, not {version!r}")
        return
    if name.startswith("Ambysto.Kit.") and "-" in version:
        prerelease_kit.append(f"{rel}: {name} {version}")


def allowed(name, path, sdk):
    if name in (ALLOWED_SDKS if sdk else ALLOWED):
        return True
    return name in INSTALLER_ONLY and "installer" in path.relative_to(root).parts[:1]


def check(name, path, what, sdk=False):
    name = name.split("/")[0].strip()
    if not allowed(name, path, sdk):
        problems.append(f"{path.relative_to(root)}: {what} {name}")


files = [p for p in root.rglob("*") if p.is_file() and not ({"bin", "obj"} & set(p.relative_to(root).parts))
         and (p.suffix in {".csproj", ".props", ".targets", ".wixproj", ".vbproj", ".fsproj"} or p.name == "packages.config")]
for path in files:
    try:
        tree = ET.parse(path)
    except ET.ParseError as e:
        problems.append(f"{path.relative_to(root)}: not readable XML ({e})")
        continue
    for el in tree.iter():
        tag = el.tag.split("}")[-1]
        if tag == "Project" and el.get("Sdk"):
            for sdk in el.get("Sdk").split(";"):
                check(sdk, path, "SDK", sdk=True)
        elif tag in {"Sdk", "Import"} and el.get("Sdk"):
            check(el.get("Sdk"), path, "SDK", sdk=True)
        elif tag == "Sdk" and el.get("Name"):
            check(el.get("Name"), path, "SDK", sdk=True)
        elif tag in {"PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload"}:
            name = el.get("Include") or el.get("Update")
            if name:
                check(name, path, tag)
                version = el.get("Version")
                if version is None:
                    version = next((c.text for c in el if c.tag.split("}")[-1] == "Version"), None)
                check_version(name, version, path)
        elif tag == "package" and el.get("id"):
            check(el.get("id"), path, "package")
        elif tag == "Reference" and any(c.tag.split("}")[-1] == "HintPath" for c in el):
            problems.append(f"{path.relative_to(root)}: a DLL reference by HintPath ({el.get('Include')})")
for gj in root.rglob("global.json"):
    for name in json.loads(gj.read_text()).get("msbuild-sdks", {}):
        check(name, gj, "SDK", sdk=True)

if prerelease_kit and not local_kit:
    problems += [p + " is a prerelease Kit (only with the KIT-LOCAL marker, never merged)" for p in prerelease_kit]
if local_kit and not prerelease_kit:
    problems.append("KIT-LOCAL marker present but no prerelease Kit package: delete the marker")
if not files:
    problems.append("no MSBuild file found")
if problems:
    print("\n".join(problems))
    print(f"FAIL: {len(problems)} package(s) outside the allowed set in {root}")
    sys.exit(1)
if local_kit:
    print("NOT MERGEABLE: KIT-LOCAL marker present; " + "; ".join(prerelease_kit))
print(f"PASS: {len(files)} MSBuild files, only allowed packages, exact versions")
