"""Exercise the unchanged graph admission code with isolated project fixtures."""

from contextlib import contextmanager
import importlib.util
from pathlib import Path
import posixpath
import runpy
import tempfile
import unittest
import os


SCRIPT = Path(__file__).resolve().parents[1] / "strict-restore-graph.py"
SPEC = importlib.util.spec_from_file_location("strict_restore_graph_fixture", SCRIPT)
graph = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(graph)


@contextmanager
def working_directory(directory):
    previous = Path.cwd()
    os.chdir(directory)
    try:
        yield
    finally:
        os.chdir(previous)


class StrictRestoreGraphTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="zeka-graph-unit-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.projects = [*graph.ROOTS.values(), *(
            f"Synthetic/Project{index:02}/Project{index:02}.csproj" for index in range(31)
        )]
        self.dependencies = {project: set() for project in self.projects}
        self.dependencies[graph.ROOTS["admin"]] = {self.projects[5]}
        self.dependencies[graph.ROOTS["client"]] = {self.projects[5], self.projects[6]}
        self.dependencies[graph.ROOTS["auth"]] = {self.projects[7]}
        self.dependencies[graph.ROOTS["gateway"]] = {self.projects[8]}
        self.dependencies[graph.ROOTS["linux-x64"]] = {self.projects[7]}
        self.memberships = {project: set() for project in self.projects}
        for name, project in graph.ROOTS.items():
            self.memberships[project].add(name)
        self.memberships[self.projects[5]] = {"admin", "client"}
        self.memberships[self.projects[6]] = {"client"}
        self.memberships[self.projects[7]] = {"auth", "linux-x64"}
        self.memberships[self.projects[8]] = {"gateway"}
        for project, dependencies in self.dependencies.items():
            references = "".join(
                f'<ProjectReference Include="{posixpath.relpath(dependency, posixpath.dirname(project))}" />'
                for dependency in sorted(dependencies)
            )
            self.write(project, self.project_xml(references))
        self.write_manifest()
        self.write_solution()

    @staticmethod
    def project_xml(references="", properties="<TargetFramework>net8.0</TargetFramework>"):
        return f"<Project><PropertyGroup>{properties}</PropertyGroup><ItemGroup>{references}</ItemGroup></Project>"

    def write(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def manifest_lines(self):
        return [
            "\t".join((project, ",".join(sorted(keys - {"linux-x64"})) or "-",
                       "linux-x64" if "linux-x64" in keys else "-"))
            for project, keys in self.memberships.items()
        ]

    def write_manifest(self, lines=None):
        self.write(graph.MANIFEST, "\n".join(self.manifest_lines() if lines is None else lines) + "\n")

    def write_solution(self, projects=None):
        projects = self.projects if projects is None else projects
        self.write(graph.SOLUTION, "\n".join(
            f'Project("{{00000000-0000-0000-0000-000000000001}}") = "Synthetic", "{project}", "{{00000000-0000-0000-0000-000000000002}}"'
            for project in projects
        ))

    def verify(self):
        with working_directory(self.root):
            return graph.verify()

    def parse(self, body):
        self.write(self.projects[9], body)
        with working_directory(self.root):
            return graph.parse_references(self.projects[9])

    def test_complete_manifest_and_shared_project_closures_pass(self):
        self.assertIsNone(self.verify())

    def test_canonical_paths_normalize_separators_and_internal_parent_segments(self):
        self.assertEqual(graph.canonical_project(r"Code\Leaf.csproj"), "Code/Leaf.csproj")
        self.assertEqual(graph.canonical_project("../Leaf/Leaf.csproj", "Code/Api"), "Code/Leaf/Leaf.csproj")

    def test_absolute_and_escaping_project_paths_fail_closed(self):
        for path in ("/outside.csproj", "../outside.csproj", "Code/../../outside.csproj"):
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "leaves the repository"):
                graph.canonical_project(path)

    def test_closure_handles_shared_dependencies_and_cycles_without_losing_members(self):
        self.assertEqual(graph.closure("root", {"root": {"shared"}, "shared": {"root"}}), {"root", "shared"})

    def test_closure_rejects_a_dependency_missing_from_the_admitted_graph(self):
        with self.assertRaisesRegex(ValueError, "outside the admitted solution closure"):
            graph.closure("root", {"root": {"missing"}})

    def test_missing_project_file_is_refused(self):
        (self.root / self.projects[9]).unlink()
        with working_directory(self.root), self.assertRaisesRegex(ValueError, "absent or redirected"):
            graph.parse_references(self.projects[9])

    def test_unrecognized_project_root_is_refused(self):
        with self.assertRaisesRegex(ValueError, "unrecognized project format"):
            self.parse("<NotProject />")

    def test_missing_wrong_duplicate_or_plural_target_frameworks_are_refused(self):
        for properties in ("", "<TargetFramework>net9.0</TargetFramework>",
                           "<TargetFramework>net8.0</TargetFramework>" * 2,
                           "<TargetFramework>net8.0</TargetFramework><TargetFrameworks>net8.0</TargetFrameworks>"):
            with self.subTest(properties=properties), self.assertRaisesRegex(ValueError, "target framework"):
                self.parse(self.project_xml(properties=properties))

    def test_explicit_import_is_refused(self):
        with self.assertRaisesRegex(ValueError, "unexpected project import"):
            self.parse(self.project_xml().replace("</Project>", '<Import Project="other.props" /></Project>'))

    def test_restore_lock_and_runtime_overrides_are_refused(self):
        for tag in ("RestoreSources", "RestoreAdditionalProjectSources", "NuGetLockFilePath", "RuntimeIdentifier", "RuntimeIdentifiers"):
            with self.subTest(tag=tag), self.assertRaisesRegex(ValueError, "unadmitted restore override"):
                self.parse(self.project_xml(properties=f"<TargetFramework>net8.0</TargetFramework><{tag}>value</{tag}>"))

    def test_static_reference_and_alias_are_accepted(self):
        self.assertEqual(self.parse(self.project_xml('<ProjectReference Include="../Leaf/Leaf.csproj" Aliases="global" />')),
                         {"Synthetic/Leaf/Leaf.csproj"})

    def test_conditional_dynamic_missing_and_extra_reference_attributes_are_refused(self):
        references = ('<ProjectReference Include="$(OtherProject)" />', '<ProjectReference />',
                      '<ProjectReference Include="../Leaf/Leaf.csproj" Condition="false" />',
                      '<ProjectReference Include="../Leaf/Leaf.csproj" ReferenceOutputAssembly="false" />')
        for reference in references:
            with self.subTest(reference=reference), self.assertRaisesRegex(ValueError, "ambiguous or conditional"):
                self.parse(self.project_xml(reference))

    def test_conditional_item_group_is_refused(self):
        body = self.project_xml('<ProjectReference Include="../Leaf/Leaf.csproj" />').replace("<ItemGroup>", '<ItemGroup Condition="false">')
        with self.assertRaisesRegex(ValueError, "ambiguous or conditional"):
            self.parse(body)

    def test_reference_that_escapes_the_repository_is_refused(self):
        with self.assertRaisesRegex(ValueError, "leaves the repository"):
            self.parse(self.project_xml('<ProjectReference Include="../../../../Leaf/Leaf.csproj" />'))

    def test_duplicate_project_references_are_refused(self):
        with self.assertRaisesRegex(ValueError, "unsupported or duplicate"):
            self.parse(self.project_xml('<ProjectReference Include="../Leaf/Leaf.csproj" />' * 2))

    def test_reference_outside_a_supported_item_group_is_refused(self):
        body = self.project_xml().replace("</Project>", '<ProjectReference Include="../Leaf/Leaf.csproj" /></Project>')
        with self.assertRaisesRegex(ValueError, "unsupported or duplicate"):
            self.parse(body)

    def test_duplicate_manifest_project_is_refused(self):
        lines = self.manifest_lines()
        self.write_manifest([*lines, lines[0]])
        with self.assertRaisesRegex(ValueError, "duplicate project"):
            self.verify()

    def test_solution_project_count_or_membership_drift_is_refused(self):
        for projects in (self.projects[:-1], [*self.projects[:-1], "Synthetic/Other/Other.csproj"]):
            with self.subTest(projects=projects):
                self.write_solution(projects)
                with self.assertRaisesRegex(ValueError, "solution project set differs"):
                    self.verify()

    def test_reference_outside_the_solution_is_refused(self):
        self.write(self.projects[9], self.project_xml('<ProjectReference Include="../Outside/Outside.csproj" />'))
        with self.assertRaisesRegex(ValueError, "unadmitted dependency"):
            self.verify()

    def test_manifest_root_closure_drift_is_refused(self):
        self.memberships[self.projects[5]].remove("admin")
        self.write_manifest()
        with self.assertRaisesRegex(ValueError, "reference closure differs"):
            self.verify()

    def test_entrypoint_accepts_the_complete_synthetic_graph(self):
        with working_directory(self.root):
            runpy.run_path(str(SCRIPT), run_name="__main__")

    def test_entrypoint_reports_missing_manifest_as_a_controlled_refusal(self):
        (self.root / graph.MANIFEST).unlink()
        with working_directory(self.root), self.assertRaisesRegex(SystemExit, "^strict-restore:"):
            runpy.run_path(str(SCRIPT), run_name="__main__")

    def test_entrypoint_reports_malformed_manifest_as_a_controlled_refusal(self):
        self.write_manifest(["invalid row"])
        with working_directory(self.root), self.assertRaisesRegex(SystemExit, "^strict-restore:"):
            runpy.run_path(str(SCRIPT), run_name="__main__")

    def test_entrypoint_reports_malformed_project_xml_as_a_controlled_refusal(self):
        self.write(self.projects[9], "<Project>")
        with working_directory(self.root), self.assertRaisesRegex(SystemExit, "^strict-restore:"):
            runpy.run_path(str(SCRIPT), run_name="__main__")


if __name__ == "__main__":
    unittest.main()
