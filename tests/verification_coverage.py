"""Check verification manifests and select skills for a Git diff."""

from collections import defaultdict
from pathlib import Path, PurePosixPath
import os
import re
import subprocess
import sys


SKILLS = Path(".claude/skills")
EXCEPTIONS = Path("tests/no-verification.txt")
HUNK = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
DECLARATIONS = {
    ".swift": re.compile(
        r"^(?:(?:@\w+(?:\([^)]*\))?|\w+(?:\(set\))?)\s+)*"
        r"(?:\b(?:class|struct|enum|protocol|extension|actor|func|var|let|typealias)\s+(\w+)"
        r"|\b(init|deinit|subscript)(?=[!?]?[<(]|\s*\{))"
    ),
    ".cs": re.compile(
        r"^(?:(?:(?:public|private|protected|internal|file|static|partial|sealed|abstract)\s+)*"
        r"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(\w+)"
        r"|(?:(?:public|private|protected|internal|static|async|readonly|required|"
        r"volatile|const|virtual|override|new|unsafe)\s+)+"
        r"(?:[\w.<>?,\[\]]+\s+)*(\w+)(?=\s*(?:[<(={;]|$)))"
    ),
    ".py": re.compile(r"^(?:async\s+)?(?:def|class)\s+(\w+)"),
}


def git(*args):
    return subprocess.check_output(["git", "--literal-pathspecs", *args])


def git_paths(*args):
    return set(filter(None, os.fsdecode(git(*args)).split("\0")))


def entries(manifest, *, exceptions=False, validate_paths=False):
    for number, line in enumerate(manifest.read_text().splitlines(), 1):
        parts = line.split(maxsplit=1)
        if not parts or line.lstrip().startswith("#"):
            continue
        path, *detail = parts
        detail = detail[0] if detail else ""
        if (exceptions and not detail) or (
            not exceptions and detail and not re.fullmatch(r"\w+(?:\.\w+)*", detail)
        ):
            raise ValueError(f"{manifest}:{number}: invalid entry: {line}")
        if Path(path).is_absolute() or ".." in Path(path).parts:
            raise ValueError(f"{manifest}:{number}: expected a repository-relative path: {path}")
        if validate_paths and not Path(path).is_file():
            raise ValueError(f"{manifest}:{number}: path does not exist: {path}")
        yield path, detail


def coverage(*, validate_paths):
    owners = defaultdict(list)
    for skill in sorted(SKILLS.glob("verify-*/SKILL.md")):
        for path, symbol in entries(skill.with_name("covers.txt"), validate_paths=validate_paths):
            owners[path].append((skill.parent.name, symbol))
    return owners


def is_source(path):
    parts = PurePosixPath(path).parts
    return (
        (parts[:2] == ("Sources", "voice-switch") and len(parts) == 3 and path.endswith(".swift"))
        or (parts[0] == "dotnet" and path.endswith(".cs") and not {"bin", "obj"}.intersection(parts[1:-1]))
        or (parts[0] == "scripts" and len(parts) == 2)
    )


def gate(owners):
    tracked = git_paths("ls-files", "-z", "--", "Sources/voice-switch", "dotnet", "scripts")
    sources = {path for path in tracked if is_source(path)}
    exceptions = {path for path, _ in entries(EXCEPTIONS, exceptions=True, validate_paths=True)}
    missing = sources - owners.keys() - exceptions
    for path in sorted(missing):
        print(f"FAIL verification coverage: {path} is missing from covers.txt and {EXCEPTIONS}", file=sys.stderr)
    if missing:
        return 1
    print(f"PASS verification coverage ({len(sources)} tracked sources)")
    return 0


def declaration_names(path, source):
    # shortcut: indentation follows repository style; use a syntax parser if formatting
    # becomes inconsistent. Unscoped changes trigger all skills owning the file.
    declaration = DECLARATIONS.get(Path(path).suffix)
    scopes = []
    for line in source.decode("utf-8", errors="replace").splitlines():
        code = line.lstrip()
        indent = len(line) - len(code)
        match = declaration.match(code) if declaration else None
        if code.startswith("}"):
            while scopes and indent < scopes[-1][0]:
                scopes.pop()
        elif code and (match or not code.startswith(("{", ")", "#"))):
            while scopes and indent <= scopes[-1][0]:
                scopes.pop()
        if match:
            scopes.append((indent, next(name for name in match.groups() if name is not None)))
        names = [name for _, name in scopes]
        yield set(names) | {".".join(names[:end]) for end in range(2, len(names) + 1)}
        if code.startswith("}"):
            while scopes and indent <= scopes[-1][0]:
                scopes.pop()


def changed_symbols(base, path):
    patch = os.fsdecode(git(
        "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--color=never", "-U0", base, "--", path
    ))
    old_lines, new_lines = set(), set()
    for line in patch.splitlines():
        match = HUNK.match(line)
        if match:
            old_start, old_count, new_start, new_count = match.groups()
            for numbers, start, count in ((old_lines, old_start, old_count), (new_lines, new_start, new_count)):
                numbers.update(range(int(start), int(start) + int(count or "1")))
    if not old_lines and not new_lines:
        return None

    symbols = set()
    for changed, source in (
        (old_lines, git("show", f"{base}:{path}") if old_lines else b""),
        (new_lines, Path(path).read_bytes() if new_lines else b""),
    ):
        for number, names in enumerate(declaration_names(path, source), 1):
            if number in changed:
                if not names:
                    return None
                symbols.update(names)
    return symbols


def affected(owners, ref):
    base = git("rev-parse", "--verify", "--end-of-options", f"{ref}^{{tree}}").decode().strip()
    paths = git_paths("diff", "--name-only", "-z", "--no-renames", base, "--")
    skills = set()
    for path in paths & owners.keys():
        entries_for_path = owners[path]
        symbols = changed_symbols(base, path) if any(symbol for _, symbol in entries_for_path) else None
        skills.update(skill for skill, symbol in entries_for_path if not symbol or symbols is None or symbol in symbols)
    for skill in sorted(skills):
        print(skill)
    return 0


if __name__ == "__main__":
    try:
        checking = sys.argv[1] == "gate"
        owners = coverage(validate_paths=checking)
        result = gate(owners) if checking else affected(owners, sys.argv[2])
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"FAIL verification coverage: {error}", file=sys.stderr)
        result = 1
    sys.exit(result)
