import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "release_commit_range.py"


class ReleaseRangeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.repo = Path(self.tmp.name)
        self.git("init", "-q")
        self.git("config", "user.name", "Tester")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "tag.gpgsign", "false")
        self.root = self.commit("initial")

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.repo, check=True,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout.decode().strip()

    def commit(self, subject, body=None):
        args = ["commit", "-q", "--allow-empty", "-m", subject]
        if body:
            args.extend(["-m", body])
        self.git(*args)
        return self.git("rev-parse", "HEAD")

    def run_script(self, commit, tag="v2.0.0", previous=None):
        output = self.repo / "result.json"
        args = [sys.executable, "-I", str(SCRIPT), "--commit", commit,
                "--tag", tag, "--output", str(output),
                "--metadata", str(self.repo / "metadata.json")]
        if previous is not None:
            args += ["--previous-tag", previous]
        process = subprocess.run(args, cwd=self.repo, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        data = json.loads(output.read_text(encoding="utf-8")) if output.exists() else None
        return process, data

    def test_linear_complete_messages_and_annotated_tag(self):
        self.git("tag", "-a", "v1.0.0", "-m", "release")
        first = self.commit("中文 subject", "first line\nsecond line")
        last = self.commit("another")
        self.git("tag", "v2.0.0")
        proc, data = self.run_script(last)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual(proc.stdout, b"")
        self.assertEqual([x["hash"] for x in data], [last, first])
        self.assertEqual(data[1]["body"], "first line\nsecond line\n")
        self.assertEqual(set(data[1]), {"hash", "subject", "body"})

    def test_other_tag_on_target_commit_gives_empty_range(self):
        self.git("tag", "v1.2.0")
        self.git("tag", "v1.2.1")
        proc, data = self.run_script(self.root)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual(data, [])
        metadata = json.loads((self.repo / "metadata.json").read_text(encoding="utf-8"))
        self.assertEqual(metadata, {"previous_tag": "v1.2.1", "commit_count": 0,
                                    "target_commit": self.root})

    def test_prerelease_and_strict_versions(self):
        self.git("tag", "v1.0.0")
        mid = self.commit("prerelease")
        self.git("tag", "v1.1.0-rc.1")
        end = self.commit("next")
        self.git("tag", "v1.2.0-01")
        proc, data = self.run_script(end)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual([x["hash"] for x in data], [end])
        proc, _ = self.run_script(mid, tag="v01.2.3")
        self.assertNotEqual(proc.returncode, 0)

    def test_merge_includes_branch_commits_but_ignores_branch_tags(self):
        self.git("tag", "v1.0.0")
        original = self.git("branch", "--show-current")
        self.git("checkout", "-q", "-b", "feature")
        feature = self.commit("feature")
        self.git("tag", "v9.0.0")
        self.git("checkout", "-q", original)
        main = self.commit("main")
        self.git("merge", "-q", "--no-ff", "feature", "-m", "merge")
        merge = self.git("rev-parse", "HEAD")
        proc, data = self.run_script(merge)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual({x["hash"] for x in data}, {feature, main, merge})

    def test_first_release_includes_every_reachable_commit(self):
        end = self.commit("end")
        proc, data = self.run_script(end, tag="v1.0.0")
        self.assertEqual(proc.returncode, 0)
        self.assertEqual({x["hash"] for x in data}, {self.root, end})

    def test_non_first_parent_and_target_mismatch_fail_silently(self):
        original = self.git("branch", "--show-current")
        self.git("checkout", "-q", "-b", "other")
        self.commit("other")
        self.git("tag", "v1.0.0")
        self.git("checkout", "-q", original)
        end = self.commit("main")
        proc, data = self.run_script(end, previous="v1.0.0")
        self.assertNotEqual(proc.returncode, 0)
        self.assertIsNone(data)
        self.git("tag", "v2.0.0", self.root)
        proc, data = self.run_script(end)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIsNone(data)

    def test_no_commit_content_or_git_errors_in_logs(self):
        self.git("tag", "v1.0.0")
        secret = "sensitive-value-for-test"
        end = self.commit(secret, secret)
        proc, data = self.run_script(end)
        self.assertEqual(proc.returncode, 0)
        self.assertNotIn(secret.encode(), proc.stdout + proc.stderr)
        self.assertEqual(data[0]["subject"], secret)
        proc, _ = self.run_script("bad-" + secret)
        self.assertNotEqual(proc.returncode, 0)
        self.assertNotIn(secret.encode(), proc.stdout + proc.stderr)

    def test_shallow_repository_rejected(self):
        self.git("update-ref", "refs/heads/shallow-test", self.root)
        # A shallow marker is sufficient to simulate an incomplete clone.
        (self.repo / ".git" / "shallow").write_text(self.root + "\n", encoding="ascii")
        proc, data = self.run_script(self.root)
        self.assertNotEqual(proc.returncode, 0)
        self.assertIsNone(data)


if __name__ == "__main__":
    unittest.main()
