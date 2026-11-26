#!/usr/bin/env python3
"""Compare the admitted manifest to the checked-out solution's project graph."""

from pathlib import Path, PurePosixPath
import posixpath
import re
import sys
import xml.etree.ElementTree as ET

MANIFEST = Path("scripts/ci/strict-restore-projects.tsv")
SOLUTION = Path("zeka-web-api.sln")
ROOTS = {
    "admin": "Services/AdminAreaManagement/AdminAreaManagement.API/AdminAreaManagement.API.csproj",
    "client": "Services/ClientManagement/Client.API/ClientManagement.API.csproj",
    "auth": "Services/AuthManager/AuthManager.API/AuthManager.API.csproj",
    "gateway": "ApiGateways/Ocelot.ApiGateway/Ocelot.ApiGateway.csproj",
    "linux-x64": "Tools/Zeka.AuthOwnerProvision/Zeka.AuthOwnerProvision.csproj",
}
SOLUTION_PROJECT = re.compile(r'^Project\([^)]+\)\s*=\s*"[^"]+",\s*"([^"]+\.csproj)"')
STATIC_REFERENCE = re.compile(r"(?:[a-zA-Z0-9_.-]+[\\/])+[a-zA-Z0-9_.-]+\.csproj")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def canonical_project(raw: str, parent: str = "") -> str:
    normalized = posixpath.normpath(posixpath.join(parent, raw.replace("\\", "/")))
    require(not PurePosixPath(normalized).is_absolute() and not normalized.startswith("../"),
            "a project reference leaves the repository")
    return normalized


def parse_references(project: str) -> set[str]:
    path = Path(project)
    require(path.is_file() and not path.is_symlink() and path.resolve() == Path.cwd() / path,
            f"project is absent or redirected: {project}")
    root = ET.parse(path).getroot()
    require(root.tag == "Project", f"unrecognized project format: {project}")
    frameworks = [node.text for node in root.iter("TargetFramework")]
    require(frameworks == ["net8.0"] and not list(root.iter("TargetFrameworks")),
            f"unexpected project target framework: {project}")
    require(not list(root.iter("Import")), f"unexpected project import: {project}")
    for node in root.iter():
        require(not node.tag.startswith("Restore") and node.tag not in {
            "NuGetLockFilePath", "RuntimeIdentifier", "RuntimeIdentifiers",
        }, f"unadmitted restore override: {project}")

    references = set()
    references_seen = 0
    for group in root.findall("ItemGroup"):
        for node in group.findall("ProjectReference"):
            references_seen += 1
            include = node.get("Include", "")
            require(set(node.attrib) <= {"Include", "Aliases"} and not group.get("Condition")
                    and STATIC_REFERENCE.fullmatch(include) is not None,
                    f"ambiguous or conditional project reference: {project}")
            references.add(canonical_project(include, posixpath.dirname(project)))
    require(references_seen == len(list(root.iter("ProjectReference"))) == len(references),
            f"unsupported or duplicate project reference: {project}")
    return references


def closure(root: str, graph: dict[str, set[str]]) -> set[str]:
    result = set()
    pending = [root]
    while pending:
        project = pending.pop()
        if project in result:
            continue
        require(project in graph, f"project outside the admitted solution closure: {project}")
        result.add(project)
        pending.extend(graph[project])
    return result


def verify() -> None:
    memberships = {}
    for line in MANIFEST.read_text(encoding="utf-8").splitlines():
        path, docker_keys, rid = line.split("\t")
        require(path not in memberships, f"duplicate project: {path}")
        memberships[path] = set(docker_keys.split(",") + [rid]) - {"-"}

    solution_projects = [
        canonical_project(match[1])
        for line in SOLUTION.read_text(encoding="utf-8-sig").splitlines()
        if (match := SOLUTION_PROJECT.match(line))
    ]
    require(len(solution_projects) == 36 and set(solution_projects) == memberships.keys(),
            "solution project set differs from the manifest")
    graph = {project: parse_references(project) for project in solution_projects}
    for project, dependencies in graph.items():
        require(dependencies <= memberships.keys(), f"unadmitted dependency of {project}")
    for key, root in ROOTS.items():
        require(closure(root, graph) == {path for path, keys in memberships.items() if key in keys},
                f"project reference closure differs from manifest for {key}")


if __name__ == "__main__":
    try:
        verify()
    except (OSError, ValueError, ET.ParseError) as error:
        sys.exit(f"strict-restore: {error}")
