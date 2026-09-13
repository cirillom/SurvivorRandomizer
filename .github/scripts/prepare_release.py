#!/usr/bin/env python3

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys


SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
CONVENTIONAL_COMMIT = re.compile(
    r"^(?P<type>[a-z][a-z0-9-]*)(?:\((?P<scope>[^()\r\n]+)\))?(?P<breaking>!)?: (?P<description>\S.*)$"
)
BREAKING_FOOTER = re.compile(r"(?m)^BREAKING(?: CHANGE|-CHANGE):[ \t]*\S")
CHANGELOG_HEADER = "# Changelog\n\nAll notable changes to this project are documented in this file.\n"
CATEGORIES = {
    "feat": "Features",
    "fix": "Bug Fixes",
    "perf": "Performance",
    "refactor": "Refactoring",
    "docs": "Documentation",
    "build": "Build System",
    "ci": "Continuous Integration",
    "test": "Tests",
    "chore": "Maintenance",
    "revert": "Reverts",
    "style": "Style",
}
CATEGORY_ORDER = [
    "Breaking Changes",
    "Features",
    "Bug Fixes",
    "Performance",
    "Refactoring",
    "Documentation",
    "Build System",
    "Continuous Integration",
    "Tests",
    "Maintenance",
    "Reverts",
    "Style",
    "Other Changes",
]


def abort(message: str) -> None:
    raise SystemExit(message)


def git(*arguments: str, check: bool = True) -> str:
    result = subprocess.run(
        ["git", *arguments],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    if check and result.returncode != 0:
        abort(result.stderr.strip() or f"git {' '.join(arguments)} failed")
    return result.stdout.strip()


def read_single_version(path: Path, pattern: re.Pattern[str], label: str) -> str:
    matches = pattern.findall(path.read_text(encoding="utf-8"))
    if len(matches) != 1:
        abort(f"{path} must contain exactly one {label} version.")
    return matches[0]


def replace_single_version(path: Path, pattern: re.Pattern[str], version: str) -> None:
    text = path.read_text(encoding="utf-8")
    updated, count = pattern.subn(lambda match: f"{match.group(1)}{version}{match.group(3)}", text)
    if count != 1:
        abort(f"Could not update exactly one version in {path}.")
    path.write_text(updated, encoding="utf-8", newline="")


def load_commits(tag: str) -> list[dict[str, object]]:
    hashes = git("rev-list", "--reverse", f"{tag}..HEAD").splitlines()
    commits: list[dict[str, object]] = []

    for commit_hash in hashes:
        message = git("show", "-s", "--format=%B", commit_hash)
        subject = message.splitlines()[0] if message else ""
        match = CONVENTIONAL_COMMIT.fullmatch(subject)
        if not match:
            abort(f"Commit {commit_hash[:7]} is not a valid Conventional Commit: {subject!r}")

        commit_type = match.group("type")
        scope = match.group("scope")
        if commit_type == "chore" and scope == "release":
            continue

        commits.append(
            {
                "hash": commit_hash[:7],
                "type": commit_type,
                "scope": scope,
                "description": match.group("description"),
                "breaking": bool(match.group("breaking")) or bool(BREAKING_FOOTER.search(message)),
            }
        )

    if not commits:
        abort(f"No releasable commits found after {tag}.")

    return commits


def calculate_version(current: str, commits: list[dict[str, object]]) -> tuple[str, str]:
    major, minor, patch = map(int, current.split("."))

    if any(commit["breaking"] for commit in commits):
        return f"{major + 1}.0.0", "major"
    if any(commit["type"] == "feat" for commit in commits):
        return f"{major}.{minor + 1}.0", "minor"
    return f"{major}.{minor}.{patch + 1}", "patch"


def render_section(version: str, commits: list[dict[str, object]]) -> str:
    grouped: dict[str, list[str]] = {}

    for commit in commits:
        category = (
            "Breaking Changes"
            if commit["breaking"]
            else CATEGORIES.get(str(commit["type"]), "Other Changes")
        )
        scope = f"**{commit['scope']}:** " if commit["scope"] else ""
        entry = f"- {scope}{commit['description']} (`{commit['hash']}`)"
        grouped.setdefault(category, []).append(entry)

    lines = [f"## [{version}] - {dt.datetime.now(dt.timezone.utc).date().isoformat()}"]
    for category in CATEGORY_ORDER:
        entries = grouped.get(category)
        if entries:
            lines.extend(["", f"### {category}", "", *entries])
    return "\n".join(lines) + "\n"


def update_changelog(path: Path, version: str, section: str) -> None:
    changelog = path.read_text(encoding="utf-8") if path.exists() else CHANGELOG_HEADER
    if not changelog.startswith("# Changelog\n"):
        abort(f"{path} must start with '# Changelog'.")
    if re.search(rf"(?m)^## \[{re.escape(version)}\] ", changelog):
        abort(f"{path} already contains version {version}.")

    first_release = changelog.find("\n## [")
    if first_release == -1:
        updated = changelog.rstrip() + "\n\n" + section
    else:
        updated = changelog[:first_release].rstrip() + "\n\n" + section + changelog[first_release:]
    path.write_text(updated, encoding="utf-8", newline="")


def append_environment(name: str, value: str) -> None:
    environment_file = os.environ.get("GITHUB_ENV")
    if environment_file:
        with Path(environment_file).open("a", encoding="utf-8", newline="") as output:
            output.write(f"{name}={value}\n")


def main() -> None:
    parser = argparse.ArgumentParser(description="Prepare a release from Conventional Commits.")
    parser.add_argument("--project", required=True, type=Path)
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--manifest", default=Path("manifest.json"), type=Path)
    parser.add_argument("--changelog", default=Path("CHANGELOG.md"), type=Path)
    parser.add_argument("--notes", required=True, type=Path)
    args = parser.parse_args()

    project_pattern = re.compile(r"<Version>((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))</Version>")
    plugin_pattern = re.compile(r'public const string PluginVersion = "((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))";')
    project_replace = re.compile(r"(<Version>)((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(</Version>)")
    plugin_replace = re.compile(r'(public const string PluginVersion = ")((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))(";)')

    project_version = read_single_version(args.project, project_pattern, "project")
    plugin_version = read_single_version(args.plugin, plugin_pattern, "plugin")
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    manifest_version = manifest.get("version_number")
    if len({project_version, plugin_version, manifest_version}) != 1:
        abort(
            "Version mismatch: "
            f"project={project_version}, plugin={plugin_version}, manifest={manifest_version}"
        )

    latest_tag = git("describe", "--tags", "--abbrev=0", "--match", "v[0-9]*")
    tag_match = re.fullmatch(r"v(.+)", latest_tag)
    if not tag_match or not SEMVER.fullmatch(tag_match.group(1)):
        abort(f"Latest version tag is not strict SemVer: {latest_tag!r}")
    if tag_match.group(1) != project_version:
        abort(f"Current version {project_version} does not match latest tag {latest_tag}.")

    commits = load_commits(latest_tag)
    version, bump = calculate_version(project_version, commits)
    tag = f"v{version}"
    if git("rev-parse", "--verify", "--quiet", f"refs/tags/{tag}", check=False):
        abort(f"Tag {tag} already exists.")

    section = render_section(version, commits)
    replace_single_version(args.project, project_replace, version)
    replace_single_version(args.plugin, plugin_replace, version)
    manifest["version_number"] = version
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="")
    update_changelog(args.changelog, version, section)
    args.notes.write_text(section, encoding="utf-8", newline="")

    for name, value in (("VERSION", version), ("TAG", tag), ("BUMP", bump)):
        append_environment(name, value)

    summary = f"Releasing {project_version} -> {version} ({bump}) from {len(commits)} commit(s)."
    print(summary)
    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with Path(summary_file).open("a", encoding="utf-8", newline="") as output:
            output.write(f"## {tag}\n\n{summary}\n")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Release preparation failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
