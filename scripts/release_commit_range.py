#!/usr/bin/env python3
"""Write release commits as UTF-8 JSON without printing commit messages to logs.

Usage: python scripts/release_commit_range.py --commit FULL_HASH --tag v1.2.3
       --output PATH --metadata PATH [--previous-tag v1.2.2]

The target tag may be absent; if present it must resolve to the supplied commit.
The previous tag is the nearest other valid version tag on the first-parent
chain, including the target commit. Ties use lexicographically greatest tag
name (a deterministic policy, including for build metadata). An optional
previous tag must lie on that chain. Shallow repositories are rejected.
"""

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


SEMVER = re.compile(
    r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
    r"(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z"
)
OID = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")


class RangeError(Exception):
    pass


def git(*args, allow_failure=False):
    env = os.environ.copy()
    env["GIT_NO_REPLACE_OBJECTS"] = "1"
    env["GIT_TERMINAL_PROMPT"] = "0"
    result = subprocess.run(["git", *args], stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=env, check=False)
    if result.returncode:
        if allow_failure:
            return None
        raise RangeError("Git 操作失败，请检查仓库及对象完整性。")
    return result.stdout


def commit_oid(revision, allow_failure=False):
    data = git("rev-parse", "--verify", "--end-of-options",
               revision + "^{commit}", allow_failure=allow_failure)
    if data is None:
        return None
    value = data.decode("ascii").strip()
    if not OID.fullmatch(value):
        raise RangeError("Git 返回了无效的提交标识。")
    return value


def calculate(commit, target_tag, explicit_previous=None):
    if not OID.fullmatch(commit):
        raise RangeError("目标提交必须是完整的小写 Git 对象标识。")
    if not SEMVER.fullmatch(target_tag):
        raise RangeError("目标标签必须是 v 前缀的严格 SemVer。")
    if explicit_previous and (not SEMVER.fullmatch(explicit_previous) or explicit_previous == target_tag):
        raise RangeError("前驱标签必须是不同于目标标签的严格 SemVer。")
    if git("rev-parse", "--is-shallow-repository").strip() != b"false":
        raise RangeError("浅克隆无法验证完整历史，请先获取完整历史和标签。")
    if commit_oid(commit) != commit:
        raise RangeError("目标提交标识与解析结果不一致。")

    refs = git("for-each-ref", "--format=%(refname:strip=2)", "refs/tags/")
    try:
        tags = refs.decode("utf-8").splitlines()
    except UnicodeDecodeError as exc:
        raise RangeError("无法读取标签名称。") from exc
    tag_set = set(tags)
    if target_tag in tag_set and commit_oid("refs/tags/" + target_tag) != commit:
        raise RangeError("目标标签与目标提交不一致。")

    chain_data = git("rev-list", "--first-parent", commit).decode("ascii").splitlines()
    if not chain_data or chain_data[0] != commit or any(not OID.fullmatch(x) for x in chain_data):
        raise RangeError("无法验证目标提交的 first-parent 历史。")
    chain = set(chain_data)
    candidates = {}
    for tag in tags:
        if tag == target_tag or not SEMVER.fullmatch(tag):
            continue
        resolved = commit_oid("refs/tags/" + tag, allow_failure=True)
        if resolved in chain:
            candidates.setdefault(resolved, []).append(tag)
    previous_tag = None
    if explicit_previous:
        previous_tag = explicit_previous
        if explicit_previous not in tag_set:
            raise RangeError("指定的前驱标签不存在。")
        previous = commit_oid("refs/tags/" + explicit_previous)
        if previous not in chain:
            raise RangeError("指定的前驱标签不在目标提交的 first-parent 链上。")
    else:
        previous = next((oid for oid in chain_data if oid in candidates), None)
        if previous is not None:
            previous_tag = max(candidates[previous])

    # --format and -z form NUL-delimited triples; no author fields are requested.
    revisions = [commit] if previous is None else [previous + ".." + commit]
    raw = git("-c", "log.showSignature=false", "log", "--no-decorate",
              "--no-show-signature", "--encoding=UTF-8", "-z",
              "--format=%H%x00%s%x00%b", *revisions, "--")
    fields = raw.split(b"\0")
    if fields[-1] != b"" or (len(fields) - 1) % 3:
        raise RangeError("Git 提交记录格式不完整。")
    commits = []
    try:
        for index in range(0, len(fields) - 1, 3):
            oid, subject, body = (part.decode("utf-8", errors="strict") for part in fields[index:index + 3])
            if not OID.fullmatch(oid):
                raise RangeError("Git 提交记录包含无效标识。")
            commits.append({"hash": oid, "subject": subject, "body": body})
    except UnicodeDecodeError as exc:
        raise RangeError("提交记录不是有效 UTF-8。") from exc
    return commits, {"previous_tag": previous_tag, "commit_count": len(commits),
                     "target_commit": commit}


class SafeArgumentParser(argparse.ArgumentParser):
    def error(self, message):
        # argparse's default error includes untrusted argument values.
        self.exit(2, "参数无效；请查看 --help。\n")


def main():
    parser = SafeArgumentParser(description=__doc__)
    parser.add_argument("--commit", required=True, help="目标提交的完整对象标识")
    parser.add_argument("--tag", required=True, help="目标版本标签")
    parser.add_argument("--previous-tag", help="可选的 first-parent 前驱标签")
    parser.add_argument("--output", required=True, help="输出 JSON 文件路径")
    parser.add_argument("--metadata", required=True, help="输出元数据 JSON 文件路径")
    args = parser.parse_args()
    try:
        if Path(args.output).resolve() == Path(args.metadata).resolve():
            raise RangeError("输出文件路径必须不同。")
        commits, metadata = calculate(args.commit, args.tag, args.previous_tag)
        Path(args.output).write_text(json.dumps(commits, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
        Path(args.metadata).write_text(json.dumps(metadata, ensure_ascii=True, indent=2) + "\n", encoding="utf-8")
    except (RangeError, OSError, UnicodeError):
        # Never print Git stderr, command lines, revisions, or commit messages.
        print("无法生成发布提交区间；请检查输入、仓库完整性和输出路径。", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
