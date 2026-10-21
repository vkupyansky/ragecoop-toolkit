#!/usr/bin/env python3
"""Prepare local public archives under outputs/public (Python 3.11+).

The required client SHA-256 must come from the DLL checked by
EngineCompositionCheck --public-release. A digest is an attestation of those
exact bytes; this packer does not replace the managed assembly check.
"""

from __future__ import annotations

import argparse
import codecs
import hashlib
import json
import os
import re
import shutil
import stat
import tempfile
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import IO

DOC_NAMES = (
    "README.md",
    "README.ru.md",
    "ENGINE_CHANGES.md",
    "ENGINE_CHANGES.ru.md",
    "LICENSE",
    "THIRD_PARTY.txt",
)
SCREENSHOT_FILES = (
    "screenshots/01-shared-play.png",
    "screenshots/02-main-menu.png",
    "screenshots/10-vehicle-catalog.png",
    "screenshots/09-garage.png",
    "screenshots/08-partners.png",
)
PROJECT = Path("work/RageCoopAdmin")
PUBLIC_BUILD = Path("outputs/public-build")
PUBLIC_CLIENT = PUBLIC_BUILD / "Client/Release/net48/RageCoopAdmin.Client.dll"
BASELINE_DIRECTORY = Path("outputs/public-baseline")
BASELINE_PINNED_HASHES = {
    "client/scripts/RageCoop/RageCoop.Client.dll": (
        "0fde6879b5cf6dd5eeb2b91684aca67215bec39c21baa37f1ec0d653163d517a",
        "7fc650fa4142be5c1fb0c433025b4ba313ba1f191802cb5481ace0aafc78cbc6",
    ),
    "client/scripts/RageCoop/RageCoop.Core.dll": (
        "59bcc3a4d6990182a7b7c8ff4d122a256704706a7826ab1d62a75ee753aee256",
        "59bcc3a4d6990182a7b7c8ff4d122a256704706a7826ab1d62a75ee753aee256",
    ),
    "client/scripts/RageCoop/Lidgren.Network.dll": (
        "0cf348189180207818848fe669b60b03d9176596fa5b4bb02533ffcb04e599c3",
        "0cf348189180207818848fe669b60b03d9176596fa5b4bb02533ffcb04e599c3",
    ),
    "server/RageCoop.Server.dll": (
        "cfafbe010ce5003ef67e957c05e870dd1b0ab3268339b1fa140abe72d126e711",
        "cfafbe010ce5003ef67e957c05e870dd1b0ab3268339b1fa140abe72d126e711",
    ),
    "server/RageCoop.Core.dll": (
        "59bcc3a4d6990182a7b7c8ff4d122a256704706a7826ab1d62a75ee753aee256",
        "59bcc3a4d6990182a7b7c8ff4d122a256704706a7826ab1d62a75ee753aee256",
    ),
    "server/ScriptHookVDotNet.dll": (
        "53c67854b3fe7a9b6b51e1be5ddb99b47b119652c39e216165265f7271971ba1",
        "53c67854b3fe7a9b6b51e1be5ddb99b47b119652c39e216165265f7271971ba1",
    ),
    "server/ScriptHookVDotNet3.dll": (
        "3b2cd1a998d278f584c287bb05bbe14d1699ce0e4f117b62038f25fa19427951",
        "3b2cd1a998d278f584c287bb05bbe14d1699ce0e4f117b62038f25fa19427951",
    ),
    "server/RageCoop.Server.deps.json": (
        "b3cae23af707fb5269f21d3baee914459125577754306aba670861b617d6dcf4",
        "b3cae23af707fb5269f21d3baee914459125577754306aba670861b617d6dcf4",
    ),
    "server/RageCoop.Server.runtimeconfig.json": (
        "ded3aeb5856a11db0b654a785574490cab55839ebfb17efe9e39b89618fc5b91",
        "ded3aeb5856a11db0b654a785574490cab55839ebfb17efe9e39b89618fc5b91",
    ),
}
BASELINE_SOURCE_FILES = ("Program.cs", "PublicBaseline.csproj", "global.json", "packages.lock.json")
REMOVED_DIAGNOSTIC_SHA256 = "d22133818a30313e0becf010d78a556a56b34ea361dbd33588c9817631fed540"
SOURCE_DIRECTORIES = ("Client", "Server", "Shared", "Bootstrap", "EarlyClient", "Checks")
PROJECT_FILES = (
    ".editorconfig",
    "Directory.Build.props",
    "References.example.props",
    "global.json",
    "Client/RageCoopAdmin.Client.csproj",
    "Client/packages.lock.json",
    "Client/Assets/PublicVehicleCatalog.json",
    "Server/CoopAdmin.Server.csproj",
    "Server/packages.lock.json",
    "Bootstrap/CoopAdmin.Startup.csproj",
    "Bootstrap/packages.lock.json",
    "EarlyClient/RageCoop.EarlyClient.csproj",
    "EarlyClient/packages.lock.json",
)
TOOL_FILES = (
    "check.sh",
    "check-public.sh",
    "prepare_public_release.py",
    "test_prepare_public_release.py",
)
PRIVATE_HUD = frozenset(
    (
        "Speedometer.cs",
        "SpeedometerAssets.cs",
        "SpeedometerInitialization.cs",
        "SpeedometerTextureCache.cs",
    )
)
SKIP_DIRECTORIES = frozenset(("bin", "obj", "lib", "__pycache__"))
ARCHIVE_NAMES = ("RageCoop-Toolkit-Addon.zip", "RageCoop-Toolkit-Source.zip")
MANIFEST_NAME = "RageCoop-Toolkit-manifest.json"
OUTPUT_DIRECTORY = Path("outputs/public")
CHUNK_SIZE = 1024 * 1024
PRIVATE_PATH = re.compile(
    rb"/(?:Users|home)/[^\s/'\"<>]{1,255}/|[A-Za-z]:[\\/](?:Users|Documents and Settings)[\\/]"
)
# Unmodified Lib.Harmony 2.4.3 binaries from the pinned NuGet package.
PINNED_HARMONY_SHA256 = frozenset(
    (
        "ddc070b57f695ece6f92c43cf82cf4d863c69685de96a99ebd9d2fa1d8fa7a19",
        "f7d167b2500808c6c2ad6e38b6efa2741a2b809f79b0e92c36e4a7079c38d9bb",
    )
)
UPSTREAM_CI_PREFIX = b"/home" + b"/runner/work/"


class ReleaseError(ValueError):
    """An input does not meet the public archive contract."""


@dataclass(frozen=True)
class Entry:
    source: Path
    name: str
    size: int
    sha256: str
    upstream_ci_paths: bool = False


def regular_file(path: Path) -> None:
    """Reject symbolic links in the selected file's entire path."""
    for part in (*reversed(path.parents), path):
        if part.is_symlink():
            raise ReleaseError(f"Symbolic links are not allowed: {part.name}")
    if not path.is_file():
        raise ReleaseError(f"Required file is missing or not regular: {path.name}")
    if not stat.S_ISREG(path.stat().st_mode):
        raise ReleaseError(f"Input is not a regular file: {path.name}")


def stream_digest(stream: IO[bytes]) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    while block := stream.read(CHUNK_SIZE):
        digest.update(block)
        size += len(block)
    return size, digest.hexdigest()


def file_digest(path: Path) -> tuple[int, str]:
    regular_file(path)
    with path.open("rb") as stream:
        return stream_digest(stream)


def reject_private_paths(path: Path, name: str, *, text: bool, root: Path, sha256: str) -> bool:
    root_bytes = os.fsencode(root)
    overlap = max(2048, 2 * len(root_bytes) + 2)
    decoder = codecs.getincrementaldecoder("utf-8")() if text else None
    tail = b""
    pinned_harmony = not text and path.name == "0Harmony.dll" and sha256 in PINNED_HARMONY_SHA256

    def inspect(data: bytes, *, final: bool) -> bool:
        scan = data if text else data.replace(b"\x00", b"")
        if root_bytes in scan:
            raise ReleaseError(f"Private absolute path found in public file: {name}")
        allowed = False
        for match in PRIVATE_PATH.finditer(scan):
            # Keep a trailing partial CI prefix for the following chunk.
            if not final and match.start() > len(scan) - overlap:
                continue
            if pinned_harmony and scan[match.start() :].startswith(UPSTREAM_CI_PREFIX):
                allowed = True
            else:
                raise ReleaseError(f"Private absolute path found in public file: {name}")
        return allowed

    upstream_ci_paths = False
    with path.open("rb") as stream:
        while block := stream.read(CHUNK_SIZE):
            if decoder is not None:
                decoder.decode(block)
            combined = tail + block
            # CodeView uses ASCII; managed literals can contain UTF-16 paths.
            # Removing NULs also detects those without retaining the whole DLL.
            upstream_ci_paths |= inspect(combined, final=False)
            tail = combined[-overlap:]
    if decoder is not None:
        decoder.decode(b"", final=True)
    return upstream_ci_paths | inspect(tail, final=True)


def entry(path: Path, name: str, *, text: bool, root: Path) -> Entry:
    archive_path = PurePosixPath(name)
    if archive_path.is_absolute() or ".." in archive_path.parts or "\\" in name:
        raise ReleaseError("Archive names must be relative portable paths.")
    size, digest = file_digest(path)
    if size == 0:
        raise ReleaseError(f"Required file is empty: {name}")
    upstream_ci_paths = reject_private_paths(path, name, text=text, root=root, sha256=digest)
    return Entry(path, name, size, digest, upstream_ci_paths)


def source_files(root: Path) -> list[Path]:
    project = root / PROJECT
    selected = {project / name for name in PROJECT_FILES}
    for component in SOURCE_DIRECTORIES:
        directory = project / component
        if directory.is_symlink() or not directory.is_dir():
            raise ReleaseError(f"Required source directory is missing or symbolic: {component}")
        for current, directories, filenames in os.walk(directory, followlinks=False):
            for name in directories:
                if (
                    name not in SKIP_DIRECTORIES
                    and not name.startswith(".")
                    and (Path(current) / name).is_symlink()
                ):
                    raise ReleaseError(f"Symbolic source directory is not allowed: {name}")
            directories[:] = sorted(
                name
                for name in directories
                if name not in SKIP_DIRECTORIES and not name.startswith(".")
            )
            for name in filenames:
                if name in PRIVATE_HUD:
                    continue
                if Path(name).suffix in (".cs", ".csproj") or name == "packages.lock.json":
                    selected.add(Path(current) / name)
    return sorted(selected)


def object_fields(value: object, names: set[str], label: str) -> dict[str, object]:
    if not isinstance(value, dict) or set(value) != names:
        raise ReleaseError(f"Invalid baseline {label} fields.")
    return {name: value[name] for name in names}


def unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for name, value in pairs:
        if name in result:
            raise ReleaseError("Duplicate baseline JSON field.")
        result[name] = value
    return result


def collect_baseline(root: Path) -> tuple[list[Entry], dict[str, object]]:
    path = root / BASELINE_DIRECTORY / "manifest.json"
    manifest_entry = entry(path, "public-baseline/manifest.json", text=True, root=root)
    if manifest_entry.size > 128 * 1024:
        raise ReleaseError("The baseline manifest is too large.")
    try:
        with path.open(encoding="utf-8") as stream:
            raw: object = json.load(stream, object_pairs_hook=unique_object)
    except json.JSONDecodeError as error:
        raise ReleaseError("The baseline manifest is not valid JSON.") from error
    manifest = object_fields(
        raw,
        {"version", "compatibility", "provenance", "transformation", "verification", "files"},
        "manifest",
    )
    if (
        type(manifest["version"]) is not int
        or manifest["version"] != 1
        or manifest["compatibility"] != "RageCoop 1.5.12.15 / SHVDN 3.7.0-nightly.188"
    ):
        raise ReleaseError("Unsupported baseline compatibility or version.")
    transformation = object_fields(
        manifest["transformation"],
        {"resource", "metadata", "removedAssemblySha256", "operation"},
        "transformation",
    )
    if (
        transformation["resource"] != "costura.system.diagnostics.diagnosticsource.dll.compressed"
        or transformation["metadata"] != "costura.metadata"
        or transformation["removedAssemblySha256"] != REMOVED_DIAGNOSTIC_SHA256
    ):
        raise ReleaseError("Unsupported baseline resource transformation.")
    verification = object_fields(
        manifest["verification"],
        {
            "MethodBodies",
            "RawIlBytes",
            "UnchangedMetadataTables",
            "ResourcesBefore",
            "ResourcesAfter",
            "Mvid",
            "Scope",
        },
        "verification",
    )
    expected_counts = {
        "MethodBodies": 778,
        "RawIlBytes": 73173,
        "UnchangedMetadataTables": 52,
        "ResourcesBefore": 22,
        "ResourcesAfter": 21,
    }
    if (
        any(
            type(verification[name]) is not int or verification[name] != count
            for name, count in expected_counts.items()
        )
        or verification["Mvid"] != "81af895b-a74a-4395-9e93-3aaf57441be4"
    ):
        raise ReleaseError("Unsupported baseline verification identity.")
    for value in (manifest["provenance"], transformation["operation"], verification["Scope"]):
        if not isinstance(value, str) or not value.strip():
            raise ReleaseError("The baseline provenance and verification scope are required.")
    records = manifest["files"]
    if not isinstance(records, list) or len(records) != len(BASELINE_PINNED_HASHES):
        raise ReleaseError("The baseline must contain exactly nine known files.")
    seen: set[str] = set()
    entries = []
    for raw_record in records:
        record = object_fields(raw_record, {"path", "sourceSha256", "sha256", "size"}, "file")
        name = record["path"]
        if not isinstance(name, str) or name not in BASELINE_PINNED_HASHES or name in seen:
            raise ReleaseError("Unexpected or duplicate baseline file path.")
        seen.add(name)
        source_digest, digest = BASELINE_PINNED_HASHES[name]
        size = record["size"]
        if (
            record["sourceSha256"] != source_digest
            or record["sha256"] != digest
            or type(size) is not int
            or size <= 0
        ):
            raise ReleaseError("The baseline file hashes or size do not match the pinned contract.")
        item = entry(root / BASELINE_DIRECTORY / name, name, text=name.endswith(".json"), root=root)
        if (item.sha256, item.size) != (digest, size):
            raise ReleaseError(f"Baseline bytes differ from the manifest: {name}")
        entries.append(item)
    entries.append(manifest_entry)
    return entries, manifest


def collect_inputs(
    root: Path, public_client: Path, expected_sha256: str
) -> tuple[list[Entry], list[Entry], dict[str, object]]:
    if re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256) is None:
        raise ReleaseError(
            "The public client SHA-256 must contain exactly 64 hexadecimal characters."
        )
    _, actual = file_digest(public_client)
    if actual != expected_sha256.lower():
        raise ReleaseError("The public client SHA-256 differs from the checked public DLL.")
    catalog_path = root / PROJECT / "Client/Assets/PublicVehicleCatalog.json"
    regular_file(catalog_path)
    try:
        with catalog_path.open(encoding="utf-8") as stream:
            catalog = json.load(stream)
    except json.JSONDecodeError as error:
        raise ReleaseError("The public source catalog is not valid JSON.") from error
    if catalog != {"version": 1, "vehicles": []} or type(catalog["version"]) is not int:
        raise ReleaseError(
            "The public source catalog must be version 1 with an empty vehicle list."
        )

    docs = [entry(root / "publication" / name, name, text=True, root=root) for name in DOC_NAMES]
    docs.extend(
        entry(root / "publication" / name, name, text=False, root=root) for name in SCREENSHOT_FILES
    )
    binary = list(docs)
    source = list(docs)
    baseline, baseline_manifest = collect_baseline(root)
    binary.extend(baseline)
    binary_inputs = (
        (
            PUBLIC_BUILD / "EarlyClient/Release/net48/CoopControl.EarlyClient.dll",
            "client/scripts/CoopControl.EarlyClient.dll",
        ),
        (PUBLIC_BUILD / "EarlyClient/Release/net48/0Harmony.dll", "client/scripts/0Harmony.dll"),
        (public_client, "server/Resources/Client/CoopControl/RageCoopAdmin.Client.dll"),
        (
            PUBLIC_BUILD / "EarlyClient/Release/net48/0Harmony.dll",
            "server/Resources/Client/CoopControl/0Harmony.dll",
        ),
        (
            PUBLIC_BUILD / "Server/Release/net10.0/CoopAdmin.dll",
            "server/Resources/Server/CoopAdmin/CoopAdmin.dll",
        ),
        (
            PUBLIC_BUILD / "Bootstrap/Release/net10.0/CoopAdmin.Startup.dll",
            "server/Bootstrap/CoopAdmin.Startup.dll",
        ),
        (PUBLIC_BUILD / "Bootstrap/Release/net10.0/0Harmony.dll", "server/Bootstrap/0Harmony.dll"),
    )
    binary.extend(entry(root / path, name, text=False, root=root) for path, name in binary_inputs)
    if (
        next(item.sha256 for item in binary if item.name.endswith("/RageCoopAdmin.Client.dll"))
        != expected_sha256.lower()
    ):
        raise ReleaseError("The public client changed during input validation.")
    for path in source_files(root):
        source.append(entry(path, path.relative_to(root).as_posix(), text=True, root=root))
    for name in BASELINE_SOURCE_FILES:
        relative = Path("work/PublicBaseline") / name
        source.append(entry(root / relative, relative.as_posix(), text=True, root=root))
    source.extend(
        entry(root / "tools" / name, "tools/" + name, text=True, root=root) for name in TOOL_FILES
    )
    launchers = [root / "publication" / ("start-toolkit." + suffix) for suffix in ("cmd", "sh")]
    if any(path.exists() or path.is_symlink() for path in launchers):
        for path in launchers:
            binary.append(entry(path, "server/" + path.name, text=True, root=root))
            source.append(entry(path, "publication/" + path.name, text=True, root=root))
    for entries in (binary, source):
        names = [item.name.casefold() for item in entries]
        if len(names) != len(set(names)):
            raise ReleaseError(
                "Archive paths must be unique, including on case-insensitive systems."
            )
        entries.sort(key=lambda item: item.name)
    return binary, source, baseline_manifest


def write_archive(path: Path, entries: list[Entry]) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for item in entries:
            regular_file(item.source)
            info = zipfile.ZipInfo(item.name, date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            mode = 0o100755 if item.name.endswith(".sh") else 0o100644
            info.external_attr = mode << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            with item.source.open("rb") as source, archive.open(info, "w") as target:
                shutil.copyfileobj(source, target, CHUNK_SIZE)
    with zipfile.ZipFile(path) as archive:
        if archive.namelist() != [item.name for item in entries]:
            raise ReleaseError("The generated archive has an unexpected file list.")
        for item in entries:
            with archive.open(item.name) as stream:
                if stream_digest(stream) != (item.size, item.sha256):
                    raise ReleaseError(f"Input changed while preparing the archive: {item.name}")


def prepare_release(
    root: Path, expected_sha256: str, public_client: Path | None = None
) -> tuple[Path, ...]:
    if root.is_symlink():
        raise ReleaseError("The project root must not be a symbolic link.")
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ReleaseError("The project root must be a directory.")
    selected_client = PUBLIC_CLIENT if public_client is None else public_client
    if not selected_client.is_absolute():
        selected_client = root / selected_client
    binary, source, baseline_manifest = collect_inputs(root, selected_client, expected_sha256)
    output = root / OUTPUT_DIRECTORY
    for directory in (output.parent, output):
        if directory.is_symlink() or (directory.exists() and not directory.is_dir()):
            raise ReleaseError("The output directory and its parent must be real directories.")
    destinations = tuple(output / name for name in (*ARCHIVE_NAMES, MANIFEST_NAME))
    for destination in destinations:
        if destination.exists() or destination.is_symlink():
            regular_file(destination)

    # Every input is validated before creating the output directory. All files
    # are fully prepared and checked before individual atomic replacements;
    # the manifest is replaced last and records both completed ZIP digests.
    output.mkdir(parents=True, exist_ok=True)
    staged: list[Path] = []
    try:
        for destination in destinations:
            descriptor, name = tempfile.mkstemp(
                prefix="." + destination.name + "-", suffix=".tmp", dir=output
            )
            os.close(descriptor)
            staged.append(Path(name))
        archive_records = {}
        for path, name, entries in zip(staged[:2], ARCHIVE_NAMES, (binary, source), strict=True):
            write_archive(path, entries)
            size, digest = file_digest(path)
            archive_records[name] = {
                "size": size,
                "sha256": digest,
                "files": [
                    {
                        "path": item.name,
                        "size": item.size,
                        "sha256": item.sha256,
                        **({"upstreamCiBuildPaths": True} if item.upstream_ci_paths else {}),
                    }
                    for item in entries
                ],
            }
        manifest = {
            "formatVersion": 1,
            "profile": "public-no-hud-stock-vehicles",
            "publicClientSha256": expected_sha256.lower(),
            "baseline": baseline_manifest,
            "archives": archive_records,
        }
        staged[-1].write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        for path in staged:
            os.chmod(path, 0o644)
            with path.open("rb") as stream:
                os.fsync(stream.fileno())
        for path, destination in zip(staged, destinations, strict=True):
            os.replace(path, destination)
    finally:
        for path in staged:
            path.unlink(missing_ok=True)
    return destinations


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument(
        "--public-client",
        type=Path,
        help="Checked public DLL; relative paths are relative to --root.",
    )
    parser.add_argument(
        "--public-client-sha256",
        required=True,
        help="SHA-256 attested by the actual public assembly check.",
    )
    args = parser.parse_args()
    try:
        paths = prepare_release(args.root, args.public_client_sha256, args.public_client)
    except (OSError, UnicodeError, ReleaseError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Public release preparation failed: {error}\n")
    for path in paths:
        size, digest = file_digest(path)
        print(f"{path.relative_to(args.root.resolve())}: {size} bytes, SHA-256 {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
