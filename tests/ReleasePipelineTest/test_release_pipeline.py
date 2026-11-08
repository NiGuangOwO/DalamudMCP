"""Publication guards: corrupt/mismatched assets must never reach the index."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import zipfile
import subprocess

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release_pipeline", ROOT / "build/release_pipeline.py")
pipeline = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pipeline)


def manifest(value="1.0.0.46"):
    return {"InternalName": "DalamudMCP", "Name": "DalamudMCP", "Author": "NiGuangOwO",
            "AssemblyVersion": value, "Description": "中文 / English", "Punchline": "Test",
            "Tags": ["mcp"], "DalamudApiLevel": 15}


def package(data=None):
    data = data or manifest()
    memory = io.BytesIO()
    with zipfile.ZipFile(memory, "w") as archive:
        archive.writestr("DalamudMCP.json", json.dumps(data))
        archive.writestr("DalamudMCP.dll", b"MZtest-only-fixture")
        archive.writestr("DalamudMCP.deps.json", json.dumps({"libraries": {"DalamudMCP/" + data["AssemblyVersion"]: {}}}))
    return memory.getvalue()


def release_fixture():
    files = {"latest.zip": package(), "DalamudMCP.json": json.dumps(manifest()).encode()}
    release = {"tag_name": "v1.0.0.46", "draft": False, "prerelease": False,
               "published_at": "2026-11-08T01:02:03Z", "assets": []}
    for name, data in files.items():
        release["assets"].append({"name": name, "digest": "sha256:" + pipeline.digest(data),
                                  "browser_download_url": f"https://github.com/{pipeline.SOURCE}/releases/download/v1.0.0.46/{name}"})
    def fetch(url, sha=None):
        data = files[url.rsplit("/", 1)[1]]
        if sha and pipeline.digest(data) != sha:
            raise ValueError("hash mismatch")
        return data
    return release, files, fetch


class PublicationGuards(unittest.TestCase):
    def test_manifest_and_archive_identity(self):
        data = manifest()
        self.assertEqual(pipeline.validate_package(package(data), json.dumps(data).encode(), "1.0.0.46"), data)
        for key, val in (("Author", "SomeoneElse"), ("InternalName", "OtherPlugin"), ("AssemblyVersion", "1.0.0.45")):
            bad = dict(data, **{key: val})
            with self.subTest(key=key), self.assertRaises(ValueError):
                pipeline.validate_package(package(bad), json.dumps(bad).encode(), "1.0.0.46")

    def test_package_manifest_disagreement(self):
        bad = dict(manifest(), Description="different")
        with self.assertRaises(ValueError):
            pipeline.validate_package(package(), json.dumps(bad).encode(), "1.0.0.46")

    def test_invalid_archive_and_dependency_version(self):
        with self.assertRaises(zipfile.BadZipFile):
            pipeline.validate_package(b"invalid", json.dumps(manifest()).encode(), "1.0.0.46")
        memory = io.BytesIO()
        with zipfile.ZipFile(memory, "w") as archive:
            archive.writestr("DalamudMCP.json", json.dumps(manifest()))
            archive.writestr("DalamudMCP.dll", b"MZfixture")
            archive.writestr("DalamudMCP.deps.json", '{"libraries":{"DalamudMCP/1.0.0.45":{}}}')
        with self.assertRaises(ValueError):
            pipeline.validate_package(memory.getvalue(), json.dumps(manifest()).encode(), "1.0.0.46")

    def test_stable_record_uses_release_time_and_pinned_url(self):
        release, files, fetch = release_fixture()
        data, record = pipeline.release_record(release, fetch)
        self.assertEqual(data, manifest())
        self.assertEqual(record["sha256"], pipeline.digest(files["latest.zip"]))
        self.assertEqual(record["publishedAtUnix"], 1794099723)
        self.assertIn("/v1.0.0.46/", record["downloadUrl"])

    def test_drafts_prereleases_and_floating_links_are_rejected(self):
        original, _, fetch = release_fixture()
        for field in ("draft", "prerelease"):
            release = dict(original, **{field: True})
            with self.subTest(field=field), self.assertRaises(ValueError):
                pipeline.release_record(release, fetch)
        release = copy.deepcopy(original)
        release["assets"][0]["browser_download_url"] = "https://github.com/NiGuangOwO/DalamudMCP/releases/latest/download/latest.zip"
        with self.assertRaises(ValueError):
            pipeline.release_record(release, fetch)

    def test_missing_or_mismatched_digest_is_rejected(self):
        release, _, fetch = release_fixture()
        for sha in (None, "sha256:" + "0" * 64):
            release["assets"][0]["digest"] = sha
            with self.subTest(sha=sha), self.assertRaises((ValueError, TypeError)):
                pipeline.release_record(release, fetch)

    def test_downgrade_and_same_version_replacement_are_rejected_before_write(self):
        release, _, fetch = release_fixture()
        data, record = pipeline.release_record(release, fetch)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp);folder = root / "plugins/DalamudMCP";folder.mkdir(parents=True)
            pipeline.write_json(folder / "DalamudMCP.json", manifest("1.0.0.47"))
            before = (folder / "DalamudMCP.json").read_bytes()
            with self.assertRaises(ValueError):pipeline.update_distribution(root, data, record)
            self.assertEqual((folder / "DalamudMCP.json").read_bytes(), before)
            pipeline.write_json(folder / "DalamudMCP.json", data)
            pipeline.write_json(folder / "release.json", dict(record, sha256="0" * 64))
            with self.assertRaises(ValueError):pipeline.update_distribution(root, data, record)

    def test_index_must_match_version_urls_timestamp_and_metadata(self):
        release, _, fetch = release_fixture()
        data, record = pipeline.release_record(release, fetch)
        entry = dict(data, DownloadLinkInstall=record["downloadUrl"], DownloadLinkUpdate=record["downloadUrl"],
                     LastUpdate=str(record["publishedAtUnix"]))
        pipeline.verify_index([entry], data, record)
        for key in ("AssemblyVersion", "Description", "DownloadLinkInstall", "DownloadLinkUpdate", "LastUpdate"):
            bad = dict(entry, **{key: "0"})
            with self.subTest(key=key), self.assertRaises(ValueError):pipeline.verify_index([bad], data, record)
        with self.assertRaises(ValueError):pipeline.verify_index([entry, entry], data, record)

    def test_existing_release_and_sync_only_planning(self):
        release, _, _ = release_fixture()
        class Client:
            def release(self, tag):return release
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp);project = root / "plugin.csproj";out = root / "output"
            project.write_text('<Project><PropertyGroup><Version>1.0.0.46</Version></PropertyGroup></Project>')
            pipeline.plan(project, out, True, True, Client())
            self.assertIn("publish=false", out.read_text())

    def test_network_failure_is_not_treated_as_missing_release(self):
        class Client:
            def release(self, tag):raise urllib.error.HTTPError("url", 403, "Forbidden", {}, None)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp);project = root / "plugin.csproj"
            project.write_text('<Project><PropertyGroup><Version>1.0.0.46</Version></PropertyGroup></Project>')
            with self.assertRaises(urllib.error.HTTPError):pipeline.plan(project, root / "out", False, True, Client())

    def test_published_tag_is_never_repointed(self):
        class Client:
            def request(self, path, *args):return {"object": {"type": "commit", "sha": "1" * 40}}
        with patch.object(pipeline, "validate_assets", return_value={}), tempfile.TemporaryDirectory() as temp:
            notes = Path(temp) / "notes.md";notes.write_text("## 简体中文\n\n说明\n\n## English\n\nNotes", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "different source commit"):
                pipeline.publish_assets(Path(temp), "1.0.0.46", "2" * 40, notes, Client())

    def test_new_version_plan_and_sync_only_missing_release(self):
        class Client:
            def release(self, tag):
                if tag == "latest":return {"tag_name": "v1.0.0.45"}
                raise urllib.error.HTTPError("url", 404, "Not found", {}, None)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp);project = root / "plugin.csproj"
            project.write_text('<Project><PropertyGroup><Version>1.0.0.46</Version></PropertyGroup></Project>')
            pipeline.plan(project, root / "out", False, True, Client())
            self.assertIn("publish=true", (root / "out").read_text())
            with self.assertRaises(ValueError):pipeline.plan(project, root / "out", True, True, Client())

    def test_sync_retries_competing_push_and_is_idempotent(self):
        # Real local bare remote: insert another publisher's commit immediately
        # before our first push, then require both publishers' data to survive.
        release, files, fetch = release_fixture()
        class Client:
            def release(self, tag):return release
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp);remote = base / "remote.git";work = base / "work";other = base / "other"
            subprocess.run(["git", "init", "--bare", str(remote)], check=True, capture_output=True)
            subprocess.run(["git", "clone", str(remote), str(work)], check=True, capture_output=True)
            for root in (work,):
                pipeline.git(root, "config", "user.name", "Fixture")
                pipeline.git(root, "config", "user.email", "fixture@example.invalid")
            pipeline.git(work, "checkout", "-b", "main")
            (work / ".gitignore").write_text("__pycache__/\n")
            # Minimal generator fixture writes a real index from the metadata;
            # production generator behavior has its own distribution test suite.
            (work / "generate_pluginmaster.py").write_text('''import json
def generate(root, branch):
 folder=root/'plugins/DalamudMCP'
 entry=json.loads((folder/'DalamudMCP.json').read_text(encoding='utf-8'))
 record=json.loads((folder/'release.json').read_text(encoding='utf-8'))
 entry.update(DownloadLinkInstall=record['downloadUrl'],DownloadLinkUpdate=record['downloadUrl'],LastUpdate=record['publishedAtUnix'])
 (root/'pluginmaster.json').write_text(json.dumps([entry]),encoding='utf-8')
''', encoding="utf-8")
            pipeline.git(work, "add", ".");pipeline.git(work, "commit", "-m", "Fixture")
            pipeline.git(work, "push", "origin", "main")
            subprocess.run(["git", "clone", "--branch", "main", str(remote), str(other)], check=True, capture_output=True)
            pipeline.git(other, "config", "user.name", "Other publisher")
            pipeline.git(other, "config", "user.email", "other@example.invalid")
            actual_run = subprocess.run;injected = [False]
            def race(args, **kwargs):
                if args[:4] == ["git", "-C", str(work), "push"] and not injected[0]:
                    injected[0] = True
                    (other / "other-plugin.txt").write_text("preserved")
                    pipeline.git(other, "add", ".");pipeline.git(other, "commit", "-m", "Concurrent plugin update")
                    actual_run(["git", "-C", str(other), "push", "origin", "main"], check=True, capture_output=True)
                return actual_run(args, **kwargs)
            def public_download(url, expected=None):
                if url.endswith("pluginmaster.json"):
                    return actual_run(["git", "--git-dir", str(remote), "show", "main:pluginmaster.json"], check=True, capture_output=True).stdout
                return fetch(url, expected)
            with patch.object(pipeline.subprocess, "run", side_effect=race), patch.object(pipeline, "download", side_effect=public_download), patch.object(pipeline, "release_record", return_value=pipeline.release_record(release, fetch)), patch.object(pipeline.time, "sleep"):
                pipeline.sync(work, "v1.0.0.46", True, Client())
                first = pipeline.git(work, "rev-parse", "HEAD")
                pipeline.sync(work, "v1.0.0.46", True, Client())
                self.assertEqual(first, pipeline.git(work, "rev-parse", "HEAD"))
            self.assertTrue(injected[0])
            self.assertEqual((work / "other-plugin.txt").read_text(), "preserved")
            self.assertEqual(pipeline.git(work, "status", "--porcelain"), "")


if __name__ == "__main__":
    unittest.main()
