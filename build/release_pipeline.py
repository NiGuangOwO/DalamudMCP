"""Build-once releases and verified, retryable Dalamud repository synchronization.

Only the Python standard library is needed. GITHUB_TOKEN is used for GitHub's API;
git authentication for the distribution checkout is provided by Actions checkout.
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

SOURCE = "NiGuangOwO/DalamudMCP"
DISTRIBUTION = "NiGuangOwO/DalamudPlugins"
ASSETS = (
    "latest.zip", "DalamudMCP.json", "dalamud-development-skill.zip",
    "dalamud-development.manifest.json", "dalamud-development.payload.json",
    "dalamud-mcp-bridge.zip",
)


def version(value: str) -> tuple[int, ...]:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+", value):
        raise ValueError(f"Expected a four-part plugin version: {value!r}")
    return tuple(map(int, value.split(".")))


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def write_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


class GitHub:
    def request(self, path, data=None, method=None):
        url = path if path.startswith("https://") else "https://api.github.com" + path
        if urllib.parse.urlparse(url).netloc not in ("api.github.com", "uploads.github.com"):
            raise ValueError("Refusing to send API credentials to a non-GitHub API host")
        headers = {"User-Agent": "DalamudMCP-release", "Accept": "application/vnd.github+json"}
        token = os.environ.get("GITHUB_TOKEN")
        if token:
            headers["Authorization"] = "Bearer " + token
        if isinstance(data, dict):
            data = json.dumps(data).encode()
            headers["Content-Type"] = "application/json"
        elif data is not None:
            headers["Content-Type"] = "application/octet-stream"
        request = urllib.request.Request(url, data=data, method=method, headers=headers)
        for attempt in range(5):
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    raw = response.read()
                return json.loads(raw) if raw else None
            except urllib.error.HTTPError as error:
                if error.code not in (429, 500, 502, 503, 504) or attempt == 4:
                    raise
                time.sleep(min(2 ** attempt, 15))
            except urllib.error.URLError:
                if attempt == 4:
                    raise
                time.sleep(min(2 ** attempt, 15))

    def release(self, tag):
        suffix = "latest" if tag == "latest" else "tags/" + urllib.parse.quote(tag, safe="")
        return self.request(f"/repos/{SOURCE}/releases/{suffix}")


def download(url: str, expected: str | None = None) -> bytes:
    """Public downloads never carry a token, including across redirects."""
    parsed = urllib.parse.urlparse(url)
    if parsed.scheme != "https" or parsed.hostname not in ("github.com", "raw.githubusercontent.com"):
        raise ValueError("Unexpected public download URL")
    # Distinguish a verification request from a cached draft-era 404.
    query = urllib.parse.parse_qsl(parsed.query)
    if expected:
        query.append(("verify", expected))
    url = urllib.parse.urlunparse(parsed._replace(query=urllib.parse.urlencode(query)))
    for attempt in range(6):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "DalamudMCP-release"})
            with urllib.request.urlopen(request, timeout=60) as response:
                data = response.read(64 * 1024 * 1024 + 1)
            if len(data) > 64 * 1024 * 1024:
                raise ValueError("Release download exceeds 64 MiB")
            if expected and digest(data) != expected:
                raise ValueError("Downloaded asset SHA-256 does not match")
            return data
        except (urllib.error.URLError, TimeoutError):
            if attempt == 5:
                raise
            time.sleep(min(2 ** attempt, 15))


def validate_package(package: bytes, external: bytes, expected_version: str) -> dict:
    version(expected_version)
    manifest = json.loads(external.decode("utf-8-sig"))
    if (manifest.get("InternalName"), manifest.get("Author"), manifest.get("AssemblyVersion")) != (
        "DalamudMCP", "NiGuangOwO", expected_version
    ):
        raise ValueError("Plugin identity/version mismatch")
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        if archive.testzip() is not None:
            raise ValueError("Corrupt package")
        if json.loads(archive.read("DalamudMCP.json").decode("utf-8-sig")) != manifest:
            raise ValueError("External and packaged manifests differ")
        if not archive.read("DalamudMCP.dll").startswith(b"MZ"):
            raise ValueError("Plugin DLL is not a PE assembly")
        dependencies = json.loads(archive.read("DalamudMCP.deps.json"))
        if "DalamudMCP/" + expected_version not in dependencies["libraries"]:
            raise ValueError("Dependency manifest version differs")
    return manifest


def validate_assets(directory: Path, expected_version: str, commit: str) -> dict:
    for name in ASSETS:
        if not (directory / name).is_file():
            raise ValueError("Missing release asset: " + name)
    validate_package((directory / "latest.zip").read_bytes(),
                     (directory / "DalamudMCP.json").read_bytes(), expected_version)
    guidance = json.loads((directory / "dalamud-development.manifest.json").read_text(encoding="utf-8"))
    if digest((directory / "dalamud-development-skill.zip").read_bytes()) != guidance["archiveSha256"]:
        raise ValueError("Guidance archive hash mismatch")
    with zipfile.ZipFile(directory / "dalamud-mcp-bridge.zip") as archive:
        for name in ("dalamud-mcp-bridge.exe", "dalamud-mcp-bridge.dll",
                     "dalamud-mcp-bridge.deps.json", "dalamud-mcp-bridge.runtimeconfig.json"):
            archive.getinfo(name)
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected an exact source commit")
    provenance = {"version": expected_version, "sourceCommit": commit,
                  "assets": {name: digest((directory / name).read_bytes()) for name in ASSETS}}
    write_json(directory / "release-provenance.json", provenance)
    return provenance


def plan(project: Path, output: Path, sync_only: bool, publish_allowed: bool, client=None):
    value = ET.parse(project).getroot().findtext("PropertyGroup/Version")
    version(value)
    publish = False
    if publish_allowed:
        client = client or GitHub()
        try:
            release = client.release("v" + value)
            publish = bool(release["draft"])
            if release["prerelease"]:
                raise ValueError("A prerelease cannot be published to the stable index")
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            publish = True
        if publish:
            try:
                latest = client.release("latest")
                if version(latest["tag_name"].removeprefix("v")) >= version(value):
                    raise ValueError("New release must be newer than the published stable release")
            except urllib.error.HTTPError as error:
                if error.code != 404:
                    raise
    if sync_only and publish:
        raise ValueError("Sync-only requires an existing published release")
    with output.open("a", encoding="utf-8") as stream:
        stream.write(f"version={value}\ntag=v{value}\npublish={str(publish).lower()}\n")


def publish_assets(directory: Path, expected_version: str, commit: str, notes: Path, client=None):
    client = client or GitHub()
    provenance = validate_assets(directory, expected_version, commit)
    body = notes.read_text(encoding="utf-8")
    if "## 简体中文" not in body or "## English" not in body:
        raise ValueError("Bilingual release notes are required")
    tag = "v" + expected_version
    # An existing tag is never moved, even during a retry.
    try:
        ref = client.request(f"/repos/{SOURCE}/git/ref/tags/{tag}")["object"]
        if ref["type"] == "tag":
            ref = client.request(f"/repos/{SOURCE}/git/tags/{ref['sha']}")["object"]
        if ref["type"] != "commit" or ref["sha"] != commit:
            raise ValueError("Existing release tag points at a different source commit")
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
    try:
        release = client.release(tag)
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = client.request(f"/repos/{SOURCE}/releases", {
            "tag_name": tag, "target_commitish": commit, "name": tag,
            "body": body, "draft": True, "prerelease": False,
        }, "POST")
    if release["target_commitish"] not in (commit,):
        raise ValueError("Release target is not the expected exact commit")
    known = {asset["name"]: asset for asset in release["assets"]}
    for name in (*ASSETS, "release-provenance.json"):
        data = (directory / name).read_bytes()
        asset = known.get(name)
        if asset is None:
            if not release["draft"]:
                raise ValueError("Refusing to modify an already published release")
            url = release["upload_url"].split("{")[0] + "?name=" + urllib.parse.quote(name)
            asset = client.request(url, data, "POST")
        if asset.get("digest") != "sha256:" + digest(data):
            raise ValueError("Existing/uploaded asset differs: " + name)
    if release["draft"]:
        release = client.request(f"/repos/{SOURCE}/releases/{release['id']}", {"draft": False}, "PATCH")
    for asset in release["assets"]:
        if asset["name"] in (*ASSETS, "release-provenance.json"):
            download(asset["browser_download_url"], asset["digest"].removeprefix("sha256:"))
    print(f"Published and downloaded all assets: {release['html_url']}")
    return provenance


def release_record(release: dict, fetch=download) -> tuple[dict, dict]:
    if release["draft"] or release["prerelease"]:
        raise ValueError("Only published stable releases can enter the index")
    tag = release["tag_name"]
    value = tag.removeprefix("v")
    version(value)
    if tag != "v" + value:
        raise ValueError("Unexpected release tag")
    assets = {item["name"]: item for item in release["assets"]}
    downloaded = {}
    for name in ("latest.zip", "DalamudMCP.json"):
        asset = assets[name]
        expected_url = f"https://github.com/{SOURCE}/releases/download/{tag}/{name}"
        if asset["browser_download_url"] != expected_url:
            raise ValueError("Asset is not a fixed-version source release URL")
        sha = asset.get("digest") or ""
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", sha):
            raise ValueError("GitHub asset SHA-256 is required")
        downloaded[name] = fetch(expected_url, sha.removeprefix("sha256:"))
    manifest = validate_package(downloaded["latest.zip"], downloaded["DalamudMCP.json"], value)
    published = release["published_at"]
    timestamp = int(dt.datetime.fromisoformat(published.replace("Z", "+00:00")).timestamp())
    record = {"schemaVersion": 1, "sourceRepository": SOURCE, "tag": tag,
              "version": value, "assetName": "latest.zip",
              "downloadUrl": assets["latest.zip"]["browser_download_url"],
              "sha256": digest(downloaded["latest.zip"]), "publishedAt": published,
              "publishedAtUnix": timestamp}
    return manifest, record


def update_distribution(root: Path, manifest: dict, record: dict) -> None:
    folder = root / "plugins/DalamudMCP"
    current_path = folder / "DalamudMCP.json"
    if current_path.exists():
        current = json.loads(current_path.read_text(encoding="utf-8-sig"))
        if version(current["AssemblyVersion"]) > version(record["version"]):
            raise ValueError("Refusing to downgrade the distribution")
        previous = folder / "release.json"
        if previous.exists():
            old = json.loads(previous.read_text(encoding="utf-8"))
            if old["version"] == record["version"] and old["sha256"] != record["sha256"]:
                raise ValueError("Same-version package hash changed")
    write_json(current_path, manifest)
    write_json(folder / "release.json", record)
    spec = importlib.util.spec_from_file_location("distribution_generator", root / "generate_pluginmaster.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.generate(root, "main")
    verify_index(json.loads((root / "pluginmaster.json").read_text(encoding="utf-8")), manifest, record)


def verify_index(index, manifest, record):
    matches = [entry for entry in index if entry["InternalName"] == "DalamudMCP"]
    if len(matches) != 1:
        raise ValueError("Expected exactly one DalamudMCP index entry")
    entry = matches[0]
    for key in ("AssemblyVersion", "Author", "Name", "InternalName", "Description", "Punchline", "Tags", "DalamudApiLevel"):
        if entry.get(key) != manifest.get(key):
            raise ValueError("Index field mismatch: " + key)
    for key in ("DownloadLinkInstall", "DownloadLinkUpdate"):
        if entry.get(key) != record["downloadUrl"]:
            raise ValueError("Index download URL mismatch")
    if int(entry["LastUpdate"]) != record["publishedAtUnix"]:
        raise ValueError("Index timestamp mismatch")


def git(root, *args, capture=True):
    result = subprocess.run(["git", "-C", str(root), *args], check=True,
                            text=True, encoding="utf-8", capture_output=capture)
    return result.stdout.strip() if capture else ""


def sync(root: Path, tag: str, push: bool, client=None):
    client = client or GitHub()
    manifest, record = release_record(client.release(tag))
    if push and git(root, "status", "--porcelain"):
        raise ValueError("Distribution checkout must be clean before synchronization")
    # Rebuild the index from a fresh branch after competing pushes, never force push.
    for attempt in range(5):
        if push:
            git(root, "fetch", "origin", "main")
            git(root, "merge", "--ff-only", "origin/main")
        update_distribution(root, manifest, record)
        paths = ("plugins/DalamudMCP/DalamudMCP.json", "plugins/DalamudMCP/release.json", "pluginmaster.json")
        if not push:
            return record
        git(root, "add", "--", *paths)
        changed = git(root, "diff", "--cached", "--name-only")
        if changed:
            if set(changed.splitlines()) - set(paths):
                raise ValueError("Unexpected staged distribution changes")
            git(root, "commit", "-m", f"Publish DalamudMCP {record['version']} from verified Release")
            commit = git(root, "rev-parse", "HEAD")
            result = subprocess.run(["git", "-C", str(root), "push", "origin", "HEAD:main"],
                                    text=True, capture_output=True)
            if result.returncode:
                # Discard ONLY the unpublished commit this routine just created,
                # after checking its parent and touched paths. Preserve others.
                touched = set(git(root, "diff-tree", "--no-commit-id", "--name-only", "-r", commit).splitlines())
                if touched - set(paths) or git(root, "status", "--porcelain"):
                    raise RuntimeError("Push failed with unexpected checkout changes")
                git(root, "reset", "--hard", commit + "^")
                if attempt == 4:
                    raise RuntimeError("Distribution push failed after five attempts")
                time.sleep(3)
                continue
        git(root, "fetch", "origin", "main")
        remote = git(root, "rev-parse", "origin/main")
        pinned = f"https://raw.githubusercontent.com/{DISTRIBUTION}/{remote}/pluginmaster.json"
        verify_index(json.loads(download(pinned)), manifest, record)
        download(record["downloadUrl"], record["sha256"])
        print(json.dumps({"version": record["version"], "distributionCommit": remote,
                          "indexUrl": pinned, "packageSha256": record["sha256"]}))
        return record
    raise RuntimeError("Distribution synchronization did not complete")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    command = commands.add_parser("plan")
    command.add_argument("--project", type=Path, default=Path("src/DalamudMCP/DalamudMCP.csproj"))
    command.add_argument("--output", type=Path, required=True)
    command.add_argument("--sync-only", action="store_true")
    command.add_argument("--publish-allowed", action="store_true")
    for name in ("validate", "publish"):
        command = commands.add_parser(name)
        command.add_argument("--assets", type=Path, required=True)
        command.add_argument("--version", required=True)
        command.add_argument("--commit", required=True)
        if name == "publish":
            command.add_argument("--notes", type=Path, required=True)
    command = commands.add_parser("sync")
    command.add_argument("--distribution", type=Path, required=True)
    command.add_argument("--tag", default="latest")
    command.add_argument("--push", action="store_true")
    args = parser.parse_args()
    if args.command == "plan":
        plan(args.project, args.output, args.sync_only, args.publish_allowed)
    elif args.command == "validate":
        validate_assets(args.assets, args.version, args.commit)
    elif args.command == "publish":
        publish_assets(args.assets, args.version, args.commit, args.notes)
    elif args.command == "sync":
        sync(args.distribution.resolve(), args.tag, args.push)


if __name__ == "__main__":
    main()
