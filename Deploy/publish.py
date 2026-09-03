#!/usr/bin/env python3
"""
Publish Tailcat.Link to NuGet.org.

One package goes out. The three libraries under it — Tailcat, Tailcat.Derp, Tailcat.Net — are not
packages of their own; their assemblies are bundled into Tailcat.Link's lib/ folder by a target in
its csproj. That bundling is silent when it breaks: the build stays green, the package still builds,
and the failure only shows up as a TypeLoadException on a consumer's machine. So this script opens
the .nupkg and refuses to push one that is missing an assembly.

Usage:
    python publish.py                     # test, bump the patch, build, pack, verify, push
    python publish.py --dry-run           # everything except the push; leaves the version alone
    python publish.py --set-version 0.2.0 # for a release that is not a patch bump
    python publish.py --no-bump           # use the version already in Directory.Build.props
    python publish.py --skip-tests        # for a re-push after a failed upload, not for a release

Requires:
    - .NET SDK on PATH (dotnet)
    - Deploy/.env with NUGET_API_KEY=<key>  (never committed — see .gitignore)
"""

import argparse
import os
import re
import subprocess
import sys
import zipfile
from pathlib import Path

# ── Paths ─────────────────────────────────────────────────────────────────────

SCRIPT_DIR = Path(__file__).parent
REPO_ROOT  = SCRIPT_DIR.parent
PROPS      = REPO_ROOT / "Directory.Build.props"
SOLUTION   = REPO_ROOT / "Tailcat.slnx"
ARTIFACTS  = REPO_ROOT / "artifacts"
ENV_FILE   = SCRIPT_DIR / ".env"

PACKAGE_ID = "Tailcat.Link"
CSPROJ     = REPO_ROOT / "src" / "Tailcat.Link" / "Tailcat.Link.csproj"

# Every assembly a consumer needs. Tailcat.Link.dll is its own build output; the rest are bundled
# from projects that are not packages, which is the part that breaks quietly.
EXPECTED_ASSEMBLIES = [
    "Tailcat.Link.dll",
    "Tailcat.Net.dll",
    "Tailcat.Derp.dll",
    "Tailcat.dll",
]

# VersionPrefix rather than Version: the repository leaves room for a -preview suffix.
VERSION_RE = re.compile(r"<VersionPrefix>(\d+)\.(\d+)\.(\d+)</VersionPrefix>")

# ── Helpers ───────────────────────────────────────────────────────────────────

def load_env(path: Path) -> dict:
    env = {}
    if not path.exists():
        return env
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        env[key.strip()] = value.strip()
    return env


def run(cmd: list, **kwargs):
    printable = [("***" if c.startswith("oy2") else str(c)) for c in map(str, cmd)]
    print(f"\n>>> {' '.join(printable)}")
    result = subprocess.run(cmd, **kwargs)
    if result.returncode != 0:
        sys.exit(result.returncode)


def read_version() -> tuple:
    m = VERSION_RE.search(PROPS.read_text(encoding="utf-8"))
    if not m:
        print(f"ERROR: <VersionPrefix> tag not found in {PROPS}")
        sys.exit(1)
    return int(m[1]), int(m[2]), int(m[3])


def current_version() -> str:
    return "{}.{}.{}".format(*read_version())


def write_version(new_ver: str) -> tuple:
    old_ver = current_version()
    content = PROPS.read_text(encoding="utf-8")
    PROPS.write_text(
        content.replace(f"<VersionPrefix>{old_ver}</VersionPrefix>",
                        f"<VersionPrefix>{new_ver}</VersionPrefix>"),
        encoding="utf-8")
    print(f"Version: {old_ver} -> {new_ver}")
    return old_ver, new_ver


def bump_version() -> tuple:
    major, minor, patch = read_version()
    return write_version(f"{major}.{minor}.{patch + 1}")


def find_nupkg(package_id: str, version: str) -> Path:
    exact = ARTIFACTS / f"{package_id}.{version}.nupkg"
    if exact.exists():
        return exact
    # Exclude the symbol package: `dotnet nuget push` uploads the matching .snupkg by itself.
    matches = [p for p in ARTIFACTS.glob(f"{package_id}.{version}*.nupkg")
               if not p.name.endswith(".symbols.nupkg")]
    if not matches:
        print(f"ERROR: {package_id}.{version}.nupkg not found in {ARTIFACTS}")
        sys.exit(1)
    return matches[0]


def verify_package(nupkg: Path):
    """Refuse to push a package whose bundled assemblies went missing.

    A pushed version can be unlisted but never replaced, so this is checked here rather than
    discovered by whoever installs it first.
    """
    with zipfile.ZipFile(nupkg) as z:
        names = z.namelist()
    libs = {Path(n).name for n in names if n.startswith("lib/")}
    missing = [a for a in EXPECTED_ASSEMBLIES if a not in libs]
    if missing:
        print(f"\nERROR: {nupkg.name} is missing bundled {', '.join(missing)}")
        print("       The IncludeReferencedProjectsInPackage target in Tailcat.Link.csproj is what")
        print("       puts them there. Anything it drops is a TypeLoadException on install.")
        sys.exit(1)
    for required in ("LICENSE", "README.md"):
        if required not in names:
            print(f"\nERROR: {nupkg.name} does not carry {required}")
            print("       BSD-3-Clause requires the notice to travel with the binaries.")
            sys.exit(1)
    print(f"\nVerified {nupkg.name}: {', '.join(sorted(libs))} + LICENSE, README.md")

# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Publish Tailcat.Link to NuGet.org")
    parser.add_argument("--dry-run",    action="store_true", help="Skip the push step (implies --no-bump)")
    parser.add_argument("--no-bump",    action="store_true", help="Skip the version bump")
    parser.add_argument("--set-version",                     help="Release this exact version (x.y.z)")
    parser.add_argument("--skip-tests", action="store_true", help="Skip the test run (re-push only)")
    args = parser.parse_args()

    if args.set_version and not re.fullmatch(r"\d+\.\d+\.\d+", args.set_version):
        print(f"ERROR: --set-version expects x.y.z, got {args.set_version!r}")
        sys.exit(1)

    # A rehearsal must not consume a version number. Bumping and then not pushing leaves the repo
    # claiming a release that does not exist, and the next real one skips a number for no reason.
    if args.dry_run and not args.no_bump and not args.set_version:
        args.no_bump = True

    env_vars = load_env(ENV_FILE)
    api_key  = env_vars.get("NUGET_API_KEY") or os.environ.get("NUGET_API_KEY")
    if not api_key and not args.dry_run:
        print(f"ERROR: NUGET_API_KEY not found in {ENV_FILE} or environment.")
        print("       Create Deploy/.env with:  NUGET_API_KEY=<your-key>")
        sys.exit(1)

    # 1. Tests. A pushed version can be unlisted but never replaced, so this gate is before the bump
    #    rather than after it — a red suite must not consume a version number.
    #    The live tests stay opt-in: they dial Tailscale's shared relays and a release must not
    #    depend on their weather.
    if args.skip_tests:
        print("Skipping tests — do this only to re-push a version that already passed.")
    else:
        run(["dotnet", "test", str(SOLUTION), "-c", "Release"], cwd=REPO_ROOT)

    # 2. Version
    if args.set_version:
        _, version = write_version(args.set_version)
    elif args.no_bump:
        version = current_version()
        print(f"Using the version already in Directory.Build.props: {version}")
    else:
        _, version = bump_version()

    # 3. Build + pack. The solution build is what -warnaserror gates; pack then runs without
    #    --no-build on purpose. Bundling the referenced assemblies needs ResolveReferences, which
    #    NoBuild=true refuses to run (NETSDK1085), and the build above has already made it a no-op.
    run(["dotnet", "build", str(SOLUTION), "-c", "Release", "-warnaserror"], cwd=REPO_ROOT)

    ARTIFACTS.mkdir(exist_ok=True)
    run(["dotnet", "pack", str(CSPROJ), "-c", "Release", "-o", str(ARTIFACTS)], cwd=REPO_ROOT)

    nupkg = find_nupkg(PACKAGE_ID, version)
    verify_package(nupkg)

    # 4. Push.
    if args.dry_run:
        print(f"\n[dry-run] Skipping push of {nupkg.name} to NuGet.org.")
        return

    run([
        "dotnet", "nuget", "push", str(nupkg),
        "--api-key", api_key,
        "--source",  "https://api.nuget.org/v3/index.json",
        "--skip-duplicate",
    ], cwd=REPO_ROOT)

    print(f"\nPublished {PACKAGE_ID} {version} to NuGet.org")
    print("Indexing takes a few minutes before `dotnet add package` finds it.")


if __name__ == "__main__":
    main()
