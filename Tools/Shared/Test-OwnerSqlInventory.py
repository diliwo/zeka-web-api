"""Credential-free parser/comparator checks; these never claim actual PG execution."""
import importlib.util
import hashlib
import json
import unittest
from unittest import mock
import tempfile
import subprocess
import contextlib
import io
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("owner_inventory", Path(__file__).with_name("OwnerSqlInventory.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class InventoryTests(unittest.TestCase):
    def test_call_line_and_literal_masking(self):
        text = '// cmd.ExecuteScalarAsync(ct);\nvar sql = "cmd.ExecuteScalarAsync(ct)";\nawait OwnerSqlExecution.ExecuteScalarAsync(command, ct);'
        result = module.csharp_producers("Tools/example.cs", text)
        self.assertEqual([x["producerId"] for x in result], ["Tools/example.cs:L3"])

    def test_raw_strings_and_comments_do_not_become_sites(self):
        text = 'var sql = $"""SELECT \'quote\' /* ; */ {name}""";\n/* x.ExecuteReaderAsync(ct) */'
        self.assertEqual(module.csharp_producers("Tools/example.cs", text), [])

    def test_raw_execution_rejected(self):
        for call in ('command.ExecuteScalarAsync(ct)', 'command?.ExecuteReaderAsync(ct)', 'GetCommand().ExecuteNonQueryAsync(ct)'):
            with self.subTest(call=call), self.assertRaises(module.InventoryError):
                module.csharp_producers("Tools/example.cs", call)

    def test_interpolation_execution_rejected(self):
        with self.assertRaises(module.InventoryError):
            module.code_mask('var x = $"{command.ExecuteScalarAsync(ct)}";')

    def test_unknown_forwarding_helper_rejected(self):
        with self.assertRaises(module.InventoryError):
            module.csharp_producers("Tools/example.cs", 'await OwnerSqlExecution.ExecuteScalarAsync(cmd,ct,producerFile,producerLine);')

    def test_helper_callers_not_shared_execution(self):
        text = '''await RowsAsync(c,"SELECT 1",ct);
await RowsAsync(c,"SELECT 2",ct);
private static async Task RowsAsync(DbConnection c,string sql,CancellationToken ct,
[CallerFilePath]string producerFile="",[CallerLineNumber]int producerLine=0)
{ await OwnerSqlExecution.ExecuteReaderAsync(command,ct,producerFile,producerLine); }'''
        result = module.csharp_producers("Shared/RlsSecurityManifestVerifier.cs",text)
        self.assertEqual([x["producerId"] for x in result], ["Shared/RlsSecurityManifestVerifier.cs:L1","Shared/RlsSecurityManifestVerifier.cs:L2"])

    def test_missing_caller_attributes_fail(self):
        with self.assertRaises(module.InventoryError):
            module.csharp_producers("Shared/RlsSecurityManifestVerifier.cs", 'private static async Task RowsAsync(c) { await OwnerSqlExecution.ExecuteReaderAsync(cmd,ct); }')

    def test_unterminated_literal_fail(self):
        for text in ('/* unfinished','var a = "unfinished','var a = """unfinished'):
            with self.subTest(text=text), self.assertRaises(module.InventoryError): module.code_mask(text)

    def test_line_collision_fail_is_inventory_responsibility(self):
        result = module.csharp_producers("a.cs",'await OwnerSqlExecution.ExecuteScalarAsync(c,ct); await OwnerSqlExecution.ExecuteNonQueryAsync(c,ct);')
        self.assertEqual(result[0]["producerId"],result[1]["producerId"])

    def fixture(self):
        inventory={"producers":[{"producerId":"a.cs:L1","producerSha256":"a"*64,"sourceBindings":{"dependency.ps1":"b"*64}}],"separateObligations":["EF migrations"]}
        receipt={"schema":module.SCHEMA,"producerId":"a.cs:L1","producerSha256":"a"*64,"querySha256":"c"*64,"serverMajor":17,"executionCompleted":True,"outcome":"success","processId":1,"observedAtUtc":"2026-11-03T00:00:00Z","sourceBindings":{"dependency.ps1":"b"*64}}
        return inventory,receipt

    def test_exact_success(self):
        inv,receipt=self.fixture()
        self.assertEqual(module.compare(inv,[receipt])["status"],"PASS")

    def test_every_source_and_provider_gate_fails_closed(self):
        for field,value in (("schema","other"),("producerId","unregistered"),("producerSha256","c"*64),("serverMajor",16),("executionCompleted",False),("outcome","fake"),("processId",None),("observedAtUtc",None),("sourceBindings",{})):
            inv,receipt=self.fixture();receipt[field]=value
            with self.subTest(field=field): self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")

    def test_postgres_error_is_observed_but_not_success(self):
        inv,receipt=self.fixture();receipt["outcome"]="postgres_error"
        result=module.compare(inv,[receipt])
        self.assertEqual(result["status"],"FAIL")
        self.assertEqual(result["postgresErrorProducers"],["a.cs:L1"])

    def auth_receipt_fixture(self):
        inventory, receipt = self.fixture()
        source = module.AUTH_INFRA + "Identity/BrowserSessions.cs"
        producer = inventory["producers"][0]
        producer.update(producerId=source + ":L42", source=source, kind="csharp-callsite")
        receipt.update(producerId=producer["producerId"], schema=module.SCHEMA_V2, sqlState=None)
        return inventory, receipt

    def test_v2_terminal_state_table(self):
        inventory, receipt = self.auth_receipt_fixture()
        success = module.compare(inventory, [receipt])
        self.assertEqual(success["status"], "PASS")
        self.assertEqual(success["successfulProducerCount"], 1)

        error = {**receipt, "outcome": "postgres_error", "sqlState": "55P03"}
        failed = module.compare(inventory, [error])
        self.assertEqual(failed["status"], "FAIL")
        self.assertEqual(failed["invalidReceiptReasons"], [])
        self.assertEqual(failed["postgresErrorProducers"], [receipt["producerId"]])

        cancelled = {**receipt, "outcome": "cancelled", "executionCompleted": False}
        incomplete = module.compare(inventory, [cancelled])
        self.assertEqual(incomplete["status"], "FAIL")
        self.assertEqual(incomplete["successfulProducerCount"], 0)
        self.assertEqual(incomplete["missingSuccessfulProducers"], [receipt["producerId"]])
        self.assertEqual(incomplete["postgresErrorProducers"], [])
        self.assertEqual(incomplete["invalidReceiptReasons"], [])

    def test_v2_rejects_invalid_terminal_state_combinations(self):
        inventory, receipt = self.auth_receipt_fixture()
        for outcome, completed, state in (
            ("success", False, None), ("success", True, "55P03"), ("success", 1, None),
            ("postgres_error", False, "55P03"), ("postgres_error", True, None),
            ("postgres_error", True, "00000"), ("postgres_error", True, "55p03"),
            ("cancelled", True, None), ("cancelled", False, "55P03"),
            ("cancelled", 0, None), ("other", False, None),
        ):
            with self.subTest(outcome=outcome, completed=completed, state=state):
                malformed = {**receipt, "outcome": outcome, "executionCompleted": completed, "sqlState": state}
                result = module.compare(inventory, [receipt, malformed])
                self.assertEqual(result["status"], "FAIL")
                self.assertEqual(len(result["invalidReceiptReasons"]), 1)
        missing_state = {key: value for key, value in receipt.items() if key != "sqlState"}
        self.assertEqual(module.compare(inventory, [missing_state])["status"], "FAIL")

    def test_v2_keeps_the_existing_source_provider_and_field_gates(self):
        inventory, receipt = self.auth_receipt_fixture()
        for field, value in (
            ("schema", "other"), ("producerId", "unregistered"), ("producerSha256", "d" * 64),
            ("querySha256", "not-a-digest"), ("serverMajor", 16), ("processId", True),
            ("observedAtUtc", "2026-11-03T00:00:00+01:00"), ("sourceBindings", {}),
            ("commandText", "forbidden field"),
        ):
            with self.subTest(field=field, value=value):
                result = module.compare(inventory, [{**receipt, field: value}])
                self.assertEqual(result["status"], "FAIL")
                self.assertEqual(len(result["invalidReceiptReasons"]), 1)
        other_inventory, other_receipt = self.fixture()
        other_receipt.update(schema=module.SCHEMA_V2, sqlState=None)
        self.assertEqual(module.compare(other_inventory, [other_receipt])["status"], "FAIL")

    def test_duplicate_and_conflicting_v2_terminals_are_uncredited(self):
        inventory, receipt = self.auth_receipt_fixture()
        cancelled = {**receipt, "outcome": "cancelled", "executionCompleted": False}
        error = {**receipt, "outcome": "postgres_error", "sqlState": "55P03"}
        legacy = {**receipt, "schema": module.SCHEMA}
        for first, second in (
            (receipt, receipt), (cancelled, cancelled), (error, error),
            (receipt, cancelled), (error, cancelled), (legacy, cancelled), (cancelled, legacy),
            (receipt, {**cancelled, "observedAtUtc": "2026-11-03T00:00:00.0000000+00:00",
                       "querySha256": "e" * 64}),
        ):
            with self.subTest(first=first["outcome"], second=second["outcome"]):
                result = module.compare(inventory, [first, second])
                self.assertEqual(result["status"], "FAIL")
                self.assertEqual(result["successfulProducerCount"], 0)
                self.assertEqual(result["postgresErrorProducers"], [])
                self.assertIn("duplicate or conflicting terminal receipt", result["invalidReceiptReasons"][0])

    def test_distinct_terminal_timestamps_and_repeated_v1_rows_keep_their_credit(self):
        inventory, receipt = self.auth_receipt_fixture()
        success = {**receipt, "observedAtUtc": "2026-11-03T00:00:00.0000001Z"}
        cancelled = {**receipt, "observedAtUtc": "2026-11-03T00:00:00.0000002+00:00",
                     "outcome": "cancelled", "executionCompleted": False}
        result = module.compare(inventory, [success, cancelled])
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(result["successfulProducerCount"], 1)

        inventory, legacy = self.fixture()
        repeated = module.compare(inventory, [legacy, legacy, {**legacy, "outcome": "postgres_error"}])
        self.assertEqual(repeated["status"], "PASS")
        self.assertEqual(repeated["successfulProducerCount"], 1)
        self.assertEqual(repeated["postgresErrorProducers"], [legacy["producerId"]])
        legacy_cancelled = {**legacy, "outcome": "cancelled", "executionCompleted": False, "sqlState": None}
        self.assertEqual(module.compare(inventory, [legacy_cancelled])["status"], "FAIL")

    def test_internal_scan_failure_never_reads_content(self):
        # Explicit synthetic scanner fixture, not a real source scan receipt.
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/"fixture.cs"; path.write_text("synthetic fixture",encoding="utf-8")
            with mock.patch.object(module,"SCANNER","synthetic-scanner"), mock.patch.object(module.subprocess,"run",return_value=subprocess.CompletedProcess([],1)) as scan, mock.patch.object(Path,"read_bytes") as read:
                with self.assertRaises(module.InventoryError): module.public_bytes(path)
                self.assertEqual(scan.call_count,1);read.assert_not_called()

    def test_missing_scanner_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/"fixture.cs";path.write_text("synthetic fixture",encoding="utf-8")
            with mock.patch.object(module,"SCANNER",None),mock.patch.object(Path,"read_bytes") as read:
                with self.assertRaises(module.InventoryError):module.public_bytes(path)
                read.assert_not_called()

    def test_unqualified_execution_rejected(self):
        with self.assertRaises(module.InventoryError):module.csharp_producers("a.cs","await ExecuteReaderAsync(c,ct);")

    def test_forbidden_receipt_content_fields_fail(self):
        inv,receipt=self.fixture();receipt["sql"]="synthetic forbidden field"
        self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")

    def test_extra_dependency_paths_fail(self):
        inv,receipt=self.fixture();receipt["sourceBindings"]["unapproved/path"]="c"*64
        self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")

    def test_metadata_types_and_values_fail_closed(self):
        for field,value in (("serverMajor",17.0),("processId",True),("processId",0),("observedAtUtc","unapproved locator"),("observedAtUtc","2026-99-03T00:00:00Z"),("observedAtUtc","2026-11-03T00:00:00+01:00"),("sqlState","unapproved locator"),("sqlState","42883")):
            inv,receipt=self.fixture();receipt[field]=value
            with self.subTest(field=field,value=value):self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")

    def test_new_production_tools_are_discovered_and_named_tests_excluded(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            for relative in ("Tools/NewProduction/NewProduction.csproj","Tools/NewProduction/Program.cs","Tools/Another/New.cs","Tools/Owner.Tests/Owner.Tests.csproj","Tools/Owner.Tests/Program.cs","Tools/Zeka.OwnerSqlContractTests/Program.cs","Tools/Zeka.OwnerSqlContractTests/Fixture.csproj","Tools/NewProduction/obj/Generated.cs"):
                path=root/relative;path.parent.mkdir(parents=True,exist_ok=True);path.touch()
            projects,files=module.discover_tools(root)
            self.assertEqual([p.relative_to(root).as_posix() for p in projects],["Tools/NewProduction/NewProduction.csproj"])
            self.assertEqual([p.relative_to(root).as_posix() for p in files],["Tools/Another/New.cs","Tools/NewProduction/Program.cs"])

    def closed_fixture(self, root):
        for directory in ("Deployments/local-stack", "Deployments/database", "Tools"):
            (root/directory).mkdir(parents=True, exist_ok=True)
        kinds = {module.QUERY_FILE: "query-factory"}
        for dependencies in module.DEPENDENCIES.values():
            for dependency in dependencies: kinds[dependency] = "delegated-query-factory"
        for bootstrap in module.BOOTSTRAPS: kinds[bootstrap] = "bootstrap-batch"
        kinds["Deployments/local-stack/NoSql.ps1"] = "reviewed-orchestration-support"
        files = {}
        for relative, kind in kinds.items():
            path=root/relative; path.parent.mkdir(parents=True,exist_ok=True)
            content=b"# reviewed synthetic fixture\n"
            path.write_bytes(content)
            files[relative]={"sha256":hashlib.sha256(content).hexdigest(),"classification":kind}
        manifest={"schema":"zeka-owner-sql-closed-surface/v1",
            "reviewPolicy":"Every file-set or byte change requires explicit source review and manifest acceptance; no automatic enrollment.","files":files}
        path=root/module.CLOSED_SURFACE;path.parent.mkdir(parents=True,exist_ok=True)
        path.write_text(json.dumps(manifest),encoding="utf-8")
        return manifest

    def check_closed_fixture(self, root):
        # Synthetic source fixture only: bypass its deterministic-scan side effect,
        # not the real production inventory scanner or any PostgreSQL requirement.
        with mock.patch.object(module,"public_bytes",side_effect=lambda p:p.read_bytes()):
            return module.validate_closed_scripts(root)

    def test_reviewed_closed_script_bytes_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);manifest=self.closed_fixture(root)
            self.assertEqual(set(self.check_closed_fixture(root)),set(manifest["files"])|{module.CLOSED_SURFACE})

    def test_new_powershell_and_sql_files_fail_closed(self):
        for relative in ("Deployments/local-stack/New.ps1","Deployments/local-stack/nested/New.sql",
                         "Deployments/database/New.sql","Tools/NewProduction/New.psm1"):
            with self.subTest(relative=relative),tempfile.TemporaryDirectory() as directory:
                root=Path(directory);self.closed_fixture(root)
                path=root/relative;path.parent.mkdir(parents=True,exist_ok=True);path.write_text("SELECT 1",encoding="utf-8")
                with self.assertRaises(module.InventoryError):self.check_closed_fixture(root)

    def test_hidden_second_query_under_same_existing_id_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);self.closed_fixture(root)
            path=root/module.QUERY_FILE
            path.write_text("'owner.identity.v1' { 'SELECT 1'; 'SELECT 2' }",encoding="utf-8")
            with self.assertRaises(module.InventoryError):self.check_closed_fixture(root)

    def test_reviewed_support_script_cannot_gain_inline_sql(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);self.closed_fixture(root)
            (root/"Deployments/local-stack/NoSql.ps1").write_text("$query='SELECT 1'",encoding="utf-8")
            with self.assertRaises(module.InventoryError):self.check_closed_fixture(root)

    def test_removed_or_reclassified_script_fails_closed(self):
        for remove in (False,True):
            with self.subTest(remove=remove),tempfile.TemporaryDirectory() as directory:
                root=Path(directory);manifest=self.closed_fixture(root)
                if remove:(root/module.QUERY_FILE).unlink()
                else:
                    manifest["files"][module.QUERY_FILE]["classification"]="reviewed-orchestration-support"
                    (root/module.CLOSED_SURFACE).write_text(json.dumps(manifest),encoding="utf-8")
                with self.assertRaises(module.InventoryError):self.check_closed_fixture(root)
    def test_query_digest_is_mandatory_and_strict_hex(self):
        for value in (None, "", "x"*64, "A"*64, "a"*63, 17):
            inv,receipt=self.fixture();receipt["querySha256"]=value
            with self.subTest(value=value):self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")
        inv,receipt=self.fixture();del receipt["querySha256"]
        self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")
    def test_bootstrap_query_digest_must_equal_source_bytes(self):
        inv,receipt=self.fixture();inv["producers"][0]["kind"]="bootstrap-batch"
        self.assertEqual(module.compare(inv,[receipt])["status"],"FAIL")
        receipt["querySha256"]=inv["producers"][0]["producerSha256"]
        self.assertEqual(module.compare(inv,[receipt])["status"],"PASS")
    def test_missing_producer_fail(self):
        inv,_=self.fixture();self.assertEqual(module.compare(inv,[])["status"],"FAIL")

    def test_unknown_and_malformed_receipts_cannot_green(self):
        inv,receipt=self.fixture()
        for extra in (None,{}, {"producerId":"unknown"}):
            with self.subTest(extra=extra): self.assertEqual(module.compare(inv,[receipt,extra])["status"],"FAIL")


    def test_closed_manifest_contract_and_entry_shape_are_not_auto_admitted(self):
        variants = (
            ("schema", "unknown", None),
            ("reviewPolicy", "unreviewed replacement", None),
            ("files", [], None),
            (None, None, None),
            (None, None, {"sha256": "A" * 64, "classification": "query-factory"}),
            (None, None, {"sha256": "a" * 64, "classification": "unknown"}),
        )
        for field, value, entry in variants:
            with self.subTest(field=field, entry=entry), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = self.closed_fixture(root)
                if field is not None:
                    manifest[field] = value
                    expected = "manifest contract rejected"
                else:
                    manifest["files"][module.QUERY_FILE] = entry
                    expected = "classification/hash rejected"
                (root / module.CLOSED_SURFACE).write_text(json.dumps(manifest), encoding="utf-8")
                with self.assertRaisesRegex(module.InventoryError, expected):
                    self.check_closed_fixture(root)

    def test_delegated_and_bootstrap_scripts_cannot_be_reclassified_as_support(self):
        delegated = next(iter(module.DEPENDENCIES.values()))[0]
        for relative, expected in ((delegated, "delegated-factory"), (module.BOOTSTRAPS[0], "bootstrap")):
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = self.closed_fixture(root)
                manifest["files"][relative]["classification"] = "reviewed-orchestration-support"
                (root / module.CLOSED_SURFACE).write_text(json.dumps(manifest), encoding="utf-8")
                with self.assertRaisesRegex(module.InventoryError, "Closed " + expected + " classification rejected"):
                    self.check_closed_fixture(root)

    def test_closed_script_discovery_excludes_only_explicit_fixture_and_build_scopes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = self.closed_fixture(root)
            ignored = ("Tools/Owner/bin/Generated.sql", "Tools/Owner/obj/Generated.ps1",
                       "Tools/Example.Tests/Fixture.sql", "Tools/Zeka.OwnerSqlContractTests/Fixture.sql")
            for relative in ignored:
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("-- generated synthetic fixture", encoding="utf-8")
            active = "Tools/NewOwner/Unregistered.sql"
            (root / active).parent.mkdir(parents=True)
            (root / active).write_text("-- new synthetic owner input", encoding="utf-8")
            discovered = module.discover_closed_scripts(root)
            self.assertEqual(set(discovered), set(manifest["files"]) | {active})
            with self.assertRaisesRegex(module.InventoryError, "file set changed"):
                self.check_closed_fixture(root)

class DiscoveryClosureTests(unittest.TestCase):
    def source(self, relative="Tools/Owner/Owner.csproj", content='<Project Sdk="Microsoft.NET.Sdk" />'):
        fixture = tempfile.TemporaryDirectory()
        self.addCleanup(fixture.cleanup)
        root = Path(fixture.name)
        self.write(root, relative, content)
        return root, root/relative

    def write(self, root, relative, content):
        path = root/relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def closure(self, root, projects):
        # Fixture-only scanner stub: all source bodies are synthetic test inputs.
        # The production entry point still performs its real scan before every read.
        with mock.patch.object(module, "public_bytes", side_effect=lambda p:p.read_bytes()):
            return module.discover_project_sources(root, projects)

    def test_chief_unqualified_static_and_generic_query_bypasses_fail_closed(self):
        for call in ('await ExecuteSqlRawAsync(context.Database, "SELECT 1");',
                     'await context.Database.SqlQueryRaw<int>("SELECT 1").ToListAsync(ct);'):
            with self.subTest(call=call), self.assertRaisesRegex(module.InventoryError, "Unsupported runtime EF SQL"):
                module.csharp_producers("Tools/Owner/Program.cs", call)

    def test_ef_query_and_execute_forms_are_never_empty_or_registered(self):
        for call in ('context.Database.ExecuteSqlRaw("SELECT 1")',
                     'RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(context.Database, "SELECT 1")',
                     'context.Database.SqlQuery<int>($"SELECT {value}")',
                     'context.Entities.FromSqlRaw("SELECT 1")',
                     'context.Entities.FromSqlInterpolated($"SELECT {value}")',
                     'context.Entities.ExecuteUpdateAsync()',
                     'context.Entities.ExecuteDelete()',
                     'var query = context.Database.SqlQueryRaw<int>;',
                     'using static Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions; ExecuteSql($"SELECT {value}")'):
            with self.subTest(call=call), self.assertRaises(module.InventoryError):
                module.csharp_producers("Tools/Owner/Program.cs", call)

    def test_auth_ef_receipts_bind_only_exact_executing_operations(self):
        browser = module.AUTH_INFRA + "Identity/BrowserSessions.cs"
        worker = module.AUTH_INFRA + "Workers/WorkerIdentitySource.cs"
        lock = 'AuthOwnerSqlReceipt.ObserveAuthQueryAsync(database, () => database.Users.FromSqlInterpolated(sql).SingleOrDefaultAsync(ct))'
        update = 'AuthOwnerSqlReceipt.ObserveAuthUpdateAsync(database, () => affected.ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, now), ct))'
        load = 'AuthOwnerSqlReceipt.ObserveAuthLoadAsync(database, () => database.Users.FromSqlInterpolated(sql).LoadAsync(ct))'
        for source, expression in ((browser, lock), (browser, update), (worker, load)):
            with self.subTest(source=source, expression=expression):
                self.assertEqual(module.csharp_producers(source, "\nawait " + expression + ";"),
                                 [{"producerId": source + ":L2", "source": source, "kind": "csharp-callsite"}])
        for source, expression in (
            (browser, 'affected.ExecuteUpdateAsync(set, ct)'),
            (worker, lock),
            (browser, update.replace("ExecuteUpdateAsync", "ExecuteDeleteAsync")),
            (browser, update[:-1] + ", file, 12)"),
            (browser, lock.replace("SingleOrDefaultAsync(ct)", "AsAsyncEnumerable()")),
            (browser, lock.replace("FromSqlInterpolated(sql)", "FromSqlInterpolated(SqlQueryRaw(sql))")),
            (browser, lock.replace("AuthOwnerSqlReceipt.", "")),
            (browser, lock + "; affected.ExecuteUpdateAsync(set, ct)"),
            (browser, "OwnerSqlExecution.ObserveAuthCommand(command, file, line)"),
        ):
            with self.subTest(source=source, expression=expression), self.assertRaises(module.InventoryError):
                module.csharp_producers(source, "await " + expression + ";")

    def test_auth_adapter_enum_is_metadata_only(self):
        enum_value = "var update = CommandSource.ExecuteUpdate; var read = CommandSource.FromSqlQuery;"
        self.assertEqual(module.csharp_producers(module.AUTH_RECEIPT, enum_value), [])
        for operation in ("affected.ExecuteUpdateAsync(set, ct);", "CommandSource.ExecuteUpdate();", "CommandSource.FromSqlQuery();"):
            with self.subTest(operation=operation), self.assertRaises(module.InventoryError):
                module.csharp_producers(module.AUTH_RECEIPT, enum_value + operation)

    def test_auth_web_project_is_exact_and_default_compiles_endpoints(self):
        root, project = self.source(module.AUTH_WEB_PROJECT, '<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><InvariantGlobalization>true</InvariantGlobalization></PropertyGroup><ItemGroup><ProjectReference Include="../AuthCore/AuthCore.csproj" /></ItemGroup></Project>')
        caller = self.write(root, project.parent.relative_to(root) / "Program.cs", "class Program {}")
        endpoint = self.write(root, project.parent.relative_to(root) / "Endpoints/WorkerRotationEndpoints.cs", "class Endpoints {}")
        dependency = self.write(root, project.parent.parent.relative_to(root) / "AuthCore/AuthCore.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
        self.assertEqual(self.closure(root, [project]), (sorted([project, dependency]), sorted([caller, endpoint])))
        other = self.write(root, "Other/Other.csproj", '<Project Sdk="Microsoft.NET.Sdk.Web" />')
        with self.assertRaises(module.InventoryError):
            self.closure(root, [other])
        self.write(root, module.AUTH_WEB_PROJECT, '<Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><Compile Include="../Helpers/*.cs" /></ItemGroup></Project>')
        with self.assertRaises(module.InventoryError):
            self.closure(root, [project])

    def test_native_method_group_and_unqualified_references_fail_closed(self):
        for call in ('var execute = command.ExecuteNonQueryAsync;',
                     'Func<CancellationToken,Task<DbDataReader>> run = command.ExecuteReaderAsync;',
                     'var execute = ExecuteScalarAsync;', 'nameof(command.ExecuteNonQueryAsync)',
                     'var execute = command.ExecuteDbDataReaderAsync;'):
            with self.subTest(call=call), self.assertRaises(module.InventoryError):
                module.csharp_producers("Tools/Owner/Program.cs", call)

    def test_escaped_identifier_cannot_hide_executable_api(self):
        for call in (r'ExecuteSql\u0052awAsync(context.Database, "SELECT 1")',
                     r'command.ExecuteNon\U00000051ueryAsync(ct)'):
            with self.subTest(call=call), self.assertRaises(module.InventoryError):
                module.csharp_producers("Tools/Owner/Program.cs", call)

    def test_unicode_format_character_cannot_hide_ef_identifier(self):
        call = 'context.Database.Exec' + chr(0x200c) + 'uteSqlRawAsync("SELECT 1")'
        with self.assertRaises(module.InventoryError):
            module.csharp_producers("Tools/Owner/Program.cs", call)

    def test_transitive_default_compiled_helper_cannot_escape_discovery(self):
        root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../Libraries/First/First.csproj" /></ItemGroup></Project>')
        self.write(root, "Libraries/First/First.csproj", '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Second/Second.csproj" /></ItemGroup></Project>')
        self.write(root, "Libraries/Second/Second.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
        helper = self.write(root, "Libraries/Second/Hidden.cs", 'await ExecuteSqlRawAsync(context.Database,"SELECT 1");')
        projects, files = self.closure(root, [project])
        self.assertEqual(len(projects), 3)
        self.assertIn(helper, files)
        with self.assertRaises(module.InventoryError):
            module.csharp_producers(helper.relative_to(root).as_posix(), helper.read_text(encoding="utf-8"))

    def test_new_transitive_instrumented_helper_registers_its_own_producer(self):
        root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../Libraries/Helper/Helper.csproj" /></ItemGroup></Project>')
        self.write(root, "Libraries/Helper/Helper.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
        helper = self.write(root, "Libraries/Helper/Default.cs", 'await OwnerSqlExecution.ExecuteScalarAsync(command,ct);')
        _, files = self.closure(root, [project])
        producers = [item for path in files for item in module.csharp_producers(path.relative_to(root).as_posix(), path.read_text(encoding="utf-8"))]
        self.assertEqual([x["producerId"] for x in producers], ["Libraries/Helper/Default.cs:L1"])
        # A later default-compiled helper is included without a hand-maintained EXTRA entry.
        added = self.write(root, "Libraries/Helper/Added.cs", 'command.ExecuteNonQueryAsync(ct);')
        _, files = self.closure(root, [project])
        self.assertIn(added, files)
        with self.assertRaises(module.InventoryError):
            module.csharp_producers(added.relative_to(root).as_posix(), added.read_text(encoding="utf-8"))

    def test_literal_linked_file_and_project_cycle_are_closed(self):
        root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="../../Libraries/Shared.cs" /><ProjectReference Include="../../Libraries/Helper.csproj" /></ItemGroup></Project>')
        shared = self.write(root, "Libraries/Shared.cs", "class Shared {}")
        self.write(root, "Libraries/Helper.csproj", '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Tools/Owner/Owner.csproj" /></ItemGroup></Project>')
        projects, files = self.closure(root, [project])
        self.assertEqual(len(projects), 2)
        self.assertIn(shared, files)

    def test_source_scope_changes_fail_closed_instead_of_guessing(self):
        bodies = ('<Import Project="extra.targets" />',
                  '<Target Name="Inject"><ItemGroup><Compile Include="Hidden.cs" /></ItemGroup></Target>',
                  '<ItemGroup Condition="true"><ProjectReference Include="Helper.csproj" /></ItemGroup>',
                  '<ItemGroup><Compile Include="$(Hidden)" /></ItemGroup>',
                  '<ItemGroup><Compile Include="../**/*.cs" /></ItemGroup>',
                  '<ItemGroup><Compile Include="Hidden.cs" Condition="true" /></ItemGroup>',
                  '<PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>',
                  '<ItemGroup><Compile Remove="$(Hidden)" /></ItemGroup>',
                  '<ItemGroup><Compile Update="Hidden.cs" /></ItemGroup>',
                  '<ItemGroup><Reference Include="Hidden"><HintPath>Hidden.dll</HintPath></Reference></ItemGroup>')
        for body in bodies:
            with self.subTest(body=body):
                root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk">'+body+'</Project>')
                with self.assertRaises(module.InventoryError): self.closure(root, [project])

    def test_build_injection_properties_fail_closed(self):
        for prop in ("CustomBeforeMicrosoftCommonTargets", "CustomAfterMicrosoftCommonTargets",
                     "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "MSBuildProjectExtensionsPath", "CoreCompileDependsOn"):
            with self.subTest(prop=prop):
                root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><'+prop+'>Hidden.targets</'+prop+'></PropertyGroup></Project>')
                with self.assertRaises(module.InventoryError): self.closure(root, [project])

    def reviewed_rid_lock_props(self):
        return (
            b"<Project>\n"
            b"  <PropertyGroup Condition=\"'$(RestoreContractRid)' == 'linux-x64'\">\n"
            b"    <NuGetLockFilePath>$(MSBuildProjectDirectory)/packages.linux-x64.lock.json</NuGetLockFilePath>\n"
            b"  </PropertyGroup>\n"
            b"</Project>\n"
        )

    def test_exact_reviewed_rid_lock_props_is_bound_and_admitted(self):
        root, project = self.source()
        props = root/"Directory.Build.props"
        props.write_bytes(self.reviewed_rid_lock_props())

        projects, sources = self.closure(root, [project])

        self.assertEqual([project.resolve()], projects)
        self.assertIn(props.resolve(), sources)

    def test_reviewed_props_admission_rejects_other_bytes_and_locations(self):
        exact = self.reviewed_rid_lock_props()
        for name, content in (
            ("mutated", exact + b"\n"),
            ("import", b'<Project><Import Project="Hidden.props" /></Project>'),
            ("compile", b'<Project><ItemGroup><Compile Include="Hidden.cs" /></ItemGroup></Project>'),
            ("reference", b'<Project><ItemGroup><ProjectReference Include="Hidden.csproj" /></ItemGroup></Project>'),
        ):
            with self.subTest(case=name):
                root, project = self.source()
                (root/"Directory.Build.props").write_bytes(content)
                with self.assertRaises(module.InventoryError):
                    self.closure(root, [project])

        for location in ("project", "outside"):
            with self.subTest(location=location):
                outer, project = self.source("Repository/Tools/Owner/Owner.csproj")
                root = outer/"Repository"
                (root/"Directory.Build.props").write_bytes(exact)
                ancestor = project.parent if location == "project" else outer
                (ancestor/"Directory.Build.props").write_bytes(exact)
                with self.assertRaises(module.InventoryError):
                    self.closure(root, [project])

    def test_directory_build_composition_fails_closed(self):
        for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
            with self.subTest(name=name):
                root, project = self.source()
                self.write(root, name, '<Project />')
                with self.assertRaises(module.InventoryError): self.closure(root, [project])

    def test_missing_or_outside_project_source_fails_closed(self):
        for include in ("Missing.csproj", "../../../Outside.csproj"):
            with self.subTest(include=include):
                root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="'+include+'" /></ItemGroup></Project>')
                with self.assertRaises(module.InventoryError): self.closure(root, [project])

    def test_current_literal_removed_subtree_is_not_default_compiled(self):
        root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Remove="Legacy/**" /></ItemGroup></Project>')
        active = self.write(root, "Tools/Owner/Active.cs", "class Active {}")
        self.write(root, "Tools/Owner/Legacy/Removed.cs", "class Legacy {}")
        self.write(root, "Tools/Owner/obj/Generated.cs", "class Generated {}")
        self.write(root, "Tools/Owner/bin/Output.cs", "class Output {}")
        _, files = self.closure(root, [project])
        self.assertEqual(files, [active])

    def test_s5855_quote_masks_preserve_prefixes_delimiters_and_following_call(self):
        literals = ('""', r'"ordinary \" quote"', '@"verbatim "" quote"',
                    '$"ordinary {value}"', '$@"verbatim {value} "" quote"',
                    '@$"verbatim {value} "" quote"', "'x'", "'\\''",
                    '"""raw " inside"""', '$"""raw {value}"""',
                    '$$"""raw {{value}}"""', '""""raw triple """ inside""""')
        for literal in literals:
            with self.subTest(literal=literal):
                source = 'var value = ' + literal + ';\nawait OwnerSqlExecution.ExecuteScalarAsync(command,ct);'
                masked = module.code_mask(source)
                self.assertEqual(len(masked), len(source))
                self.assertEqual(masked.count('\n'), source.count('\n'))
                self.assertEqual([x["producerId"] for x in module.csharp_producers("a.cs", source)], ["a.cs:L2"])

    def test_ef_execution_in_interpolation_fails_closed(self):
        for source in ('$"{context.Database.SqlQueryRaw<int>(\"SELECT 1\")}"',
                       '$"{ExecuteSqlRawAsync(context.Database,\"SELECT 1\")}"'):
            with self.subTest(source=source), self.assertRaises(module.InventoryError): module.code_mask(source)


    def test_unbalanced_execution_never_becomes_an_empty_inventory(self):
        with self.assertRaisesRegex(module.InventoryError, "Unbalanced C# delimiters"):
            module.csharp_producers("Tools/Synthetic/Program.cs",
                                    "await OwnerSqlExecution.ExecuteScalarAsync(command, ct;")

    def test_expression_helper_requires_termination_and_compiler_identity_forwarding(self):
        declaration = ("private static object RowsAsync([CallerFilePath] string producerFile, "
                       "[CallerLineNumber] int producerLine) => ")
        wrapper = "OwnerSqlExecution.ExecuteReaderAsync(command, ct, producerFile, producerLine)"
        source = "await RowsAsync(command, ct);\n" + declaration + wrapper + ";"
        self.assertEqual([item["producerId"] for item in module.csharp_producers(
            "Shared/RlsSecurityManifestVerifier.cs", source)], ["Shared/RlsSecurityManifestVerifier.cs:L1"])
        with self.assertRaisesRegex(module.InventoryError, "Unterminated helper expression body"):
            module.csharp_producers("Shared/RlsSecurityManifestVerifier.cs", declaration + wrapper)
        with self.assertRaisesRegex(module.InventoryError, "does not forward compiler caller identity"):
            module.csharp_producers("Shared/RlsSecurityManifestVerifier.cs",
                                    declaration + "OwnerSqlExecution.ExecuteReaderAsync(command, ct);")

    def test_auth_observer_method_group_is_not_an_executed_provider_operation(self):
        source = ("AuthOwnerSqlReceipt.ObserveAuthQueryAsync(database, "
                  "() => database.Users.FromSqlInterpolated)")
        with self.assertRaisesRegex(module.InventoryError, "Unsupported Auth EF receipt operation"):
            module.csharp_producers(module.AUTH_INFRA + "Identity/BrowserSessions.cs", source)

    def test_missing_and_non_project_roots_are_refused_before_source_discovery(self):
        root, _ = self.source()
        for relative in ("Tools/Missing.csproj", "Tools/Owner/NotAProject.txt"):
            with self.subTest(relative=relative), self.assertRaisesRegex(
                    module.InventoryError, "Missing/unsupported source project"):
                self.closure(root, [root / relative])

    def test_project_reference_and_link_metadata_cannot_change_the_compiled_boundary(self):
        bodies = (
            ('<ProjectReference Include="Helper.csproj" ReferenceOutputAssembly="false" />',
             "Unsupported project-reference metadata"),
            ('<ProjectReference Include="Helper.csproj"><Aliases>external</Aliases></ProjectReference>',
             "Unsupported project-reference metadata"),
            ('<Compile Include="Linked.cs" Visible="false" />', "Unsupported linked Compile metadata"),
            ('<Compile Include="Linked.cs"><Link>Other.cs</Link></Compile>', "Unsupported linked Compile metadata"),
            ('<EnableDefaultItems>false</EnableDefaultItems>', "Custom default Compile scope"),
        )
        for body, expected in bodies:
            with self.subTest(body=body):
                root, project = self.source(content='<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>'
                                                   + body + '</ItemGroup></Project>')
                self.write(root, "Tools/Owner/Linked.cs", "class SyntheticLinked {}")
                self.write(root, "Tools/Owner/Helper.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
                with self.assertRaisesRegex(module.InventoryError, expected):
                    self.closure(root, [project])

    @staticmethod
    def directory_link(link, target):
        # A real reparse point in an owned temporary fixture, never an owner path.
        if module.os.name == "nt":
            import _winapi
            _winapi.CreateJunction(str(target), str(link))
        else:
            link.symlink_to(target, target_is_directory=True)

    def test_project_reference_directory_reparse_is_refused_before_resolving_it(self):
        root, project = self.source()
        target = root / "SourceProject"
        target.mkdir()
        self.write(root, "SourceProject/Linked.csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
        link = root / "LinkedProject"
        self.directory_link(link, target)
        with self.assertRaisesRegex(module.InventoryError, "Reparse point in project source closure"):
            self.closure(root, [link / "Linked.csproj"])

    def test_default_compile_directory_reparse_cannot_extend_the_source_scope(self):
        root, project = self.source()
        target = root / "SourceHelpers"
        target.mkdir()
        self.write(root, "SourceHelpers/Hidden.cs", "class SyntheticHidden {}")
        self.directory_link(project.parent / "LinkedHelpers", target)
        with self.assertRaisesRegex(module.InventoryError, "Reparse point in default Compile source scope"):
            self.closure(root, [project])

    def test_closed_owner_script_reparse_is_refused_without_following_the_target(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            InventoryTests.closed_fixture(self, root)
            target = root / "SyntheticTarget"
            target.mkdir()
            self.directory_link(root / "Deployments/local-stack/LinkedInput", target)
            with self.assertRaisesRegex(module.InventoryError, "Reparse point in closed owner-script source scope"):
                module.discover_closed_scripts(root)

class FinalSourceSetTests(unittest.TestCase):
    def fixture(self):
        fixture = tempfile.TemporaryDirectory()
        self.addCleanup(fixture.cleanup)
        root = Path(fixture.name)
        files = {"Tools/Owner/Owner.csproj": '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../Libraries/Helper/Helper.csproj" /></ItemGroup></Project>',
                 "Tools/Owner/Program.cs": "class Program {}",
                 "Libraries/Helper/Helper.csproj": '<Project Sdk="Microsoft.NET.Sdk" />',
                 "Libraries/Helper/Default.cs": "class Helper {}",
                 "Tools/Shared/OwnerSqlExecution.cs": "class OwnerSqlExecution {}",
                 "Deployments/local-stack/Original.ps1": "# synthetic reviewed source"}
        for relative, contents in files.items():
            path = root/relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(contents, encoding="utf-8")
        (root/"Deployments/database").mkdir(parents=True)
        return root

    def sets(self, root):
        projects, files = module.discover_tools(root)
        projects, closure = module.discover_project_sources(root, projects)
        paths = {p.relative_to(root).as_posix() for p in set(projects)|set(files)|set(closure)}
        return paths, set(module.discover_closed_scripts(root))

    def check(self, root, expected_graph, expected_scripts):
        # Fixture-only source read stub. Production cache checks remain unchanged.
        with mock.patch.object(module, "public_bytes", side_effect=lambda p:p.read_bytes()), mock.patch.object(module, "SERVICE_PROJECTS", ()), mock.patch.object(module, "EXTRA", ()):
            module.recheck_source_sets(root, expected_graph, expected_scripts)

    def test_unchanged_final_source_sets_pass(self):
        root = self.fixture()
        with mock.patch.object(module, "public_bytes", side_effect=lambda p:p.read_bytes()):
            graph, scripts = self.sets(root)
        self.check(root, graph, scripts)

    def test_late_default_helper_project_or_script_change_fails_closed(self):
        for change in ("default-helper", "transitive-helper", "new-project", "script", "removed-helper"):
            with self.subTest(change=change):
                root = self.fixture()
                with mock.patch.object(module, "public_bytes", side_effect=lambda p:p.read_bytes()):
                    graph, scripts = self.sets(root)
                if change == "removed-helper":
                    (root/"Libraries/Helper/Default.cs").unlink()
                else:
                    relative, contents = {
                        "default-helper": ("Tools/Owner/Late.cs", "class Late {}"),
                        "transitive-helper": ("Libraries/Helper/Late.cs", "class Late {}"),
                        "new-project": ("Tools/Late/Late.csproj", '<Project Sdk="Microsoft.NET.Sdk" />'),
                        "script": ("Deployments/local-stack/Late.ps1", "# synthetic new source")}[change]
                    path = root/relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text(contents, encoding="utf-8")
                with self.assertRaises(module.InventoryError): self.check(root, graph, scripts)


class ReferencedSourceBoundaryTests(unittest.TestCase):
    def fixture(self):
        fixture = tempfile.TemporaryDirectory()
        self.addCleanup(fixture.cleanup)
        root = Path(fixture.name)
        contents = {
            "Tools/Owner/Program.cs": "await ProvisionAsync();",
            "Tools/Owner/Owner.csproj": '<Project Sdk="Microsoft.NET.Sdk" />',
            "Libraries/Helper/Default.cs": "class Helper {}",
            "Tools/Shared/OwnerSqlExecution.cs": "class OwnerSqlExecution {}",
            "Services/Example/RuntimeClosure.cs": "await command.ExecuteNonQueryAsync(ct);",
        }
        for relative, content in contents.items():
            path = root/relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
        bindings = self.bindings(root, contents)
        manifest = {"schema": "zeka-owner-sql-referenced-source-boundary/v1",
                    "reviewPolicy": module.SOURCE_BOUNDARY_POLICY, "baseRevision": "b"*40,
                    "sourceGraphSha256": module.source_graph_sha256(bindings),
                    "runtimeOnlySources": {"Services/Example/RuntimeClosure.cs": {
                        "sha256": bindings["Services/Example/RuntimeClosure.cs"],
                        "classification": "service-runtime-only",
                        "reason": "Reviewed runtime closure implementation; not admitted owner invocation."}}}
        self.write_manifest(root, manifest)
        return root, list(contents), manifest

    def bindings(self, root, files):
        # Synthetic fixture-only source bytes, never a production scanner shortcut.
        return {relative: hashlib.sha256((root/relative).read_bytes()).hexdigest() for relative in files}

    def write_manifest(self, root, manifest):
        path = root/module.SOURCE_BOUNDARY
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(manifest), encoding="utf-8")

    def validate(self, root, bindings):
        with mock.patch.object(module, "public_bytes", side_effect=lambda p:p.read_bytes()):
            return module.validate_referenced_source_boundary(root, bindings)

    def test_exact_reviewed_source_graph_allows_only_declared_runtime_classification(self):
        root, files, _ = self.fixture()
        self.assertEqual(self.validate(root, self.bindings(root, files)), {"Services/Example/RuntimeClosure.cs"})

    def test_changed_owner_caller_cannot_invoke_unchanged_excluded_runtime_sql(self):
        root, files, _ = self.fixture()
        unchanged = (root/"Services/Example/RuntimeClosure.cs").read_bytes()
        (root/"Tools/Owner/Program.cs").write_text("await RuntimeClosure.RunAsync();", encoding="utf-8")
        self.assertEqual((root/"Services/Example/RuntimeClosure.cs").read_bytes(), unchanged)
        with self.assertRaisesRegex(module.InventoryError, "explicit review required"):
            self.validate(root, self.bindings(root, files))

    def test_new_removed_or_changed_transitive_helper_fails_closed(self):
        for change in ("new", "removed", "changed"):
            with self.subTest(change=change):
                root, files, _ = self.fixture()
                relative = "Libraries/Helper/Default.cs"
                if change == "new":
                    relative = "Libraries/Helper/Added.cs"
                    files.append(relative)
                    (root/relative).write_text("await command.ExecuteScalarAsync(ct);", encoding="utf-8")
                elif change == "removed":
                    (root/relative).unlink()
                    files.remove(relative)
                else:
                    (root/relative).write_text("await RuntimeClosure.RunAsync();", encoding="utf-8")
                with self.assertRaises(module.InventoryError): self.validate(root, self.bindings(root, files))

    def test_project_core_or_runtime_bytes_cannot_change_under_existing_manifest(self):
        for relative in ("Tools/Owner/Owner.csproj", "Tools/Shared/OwnerSqlExecution.cs",
                         "Services/Example/RuntimeClosure.cs"):
            with self.subTest(relative=relative):
                root, files, _ = self.fixture()
                (root/relative).write_text("changed source fixture", encoding="utf-8")
                with self.assertRaises(module.InventoryError): self.validate(root, self.bindings(root, files))

    def test_runtime_classification_schema_and_hash_fail_closed(self):
        for change in ("hash", "kind", "reason", "outside", "unknown-member"):
            with self.subTest(change=change):
                root, files, manifest = self.fixture()
                item = manifest["runtimeOnlySources"]["Services/Example/RuntimeClosure.cs"]
                if change == "hash": item["sha256"] = "0"*64
                elif change == "kind": item["classification"] = "automatic-exemption"
                elif change == "reason": item["reason"] = ""
                elif change == "outside": manifest["runtimeOnlySources"] = {"Tools/Owner/Program.cs": item}
                else: item["automaticEnrollment"] = True
                self.write_manifest(root, manifest)
                with self.assertRaises(module.InventoryError): self.validate(root, self.bindings(root, files))

    def test_graph_hash_is_canonical_and_path_set_sensitive(self):
        first = {"b.cs": "b"*64, "a.cs": "a"*64}
        self.assertEqual(module.source_graph_sha256(first), module.source_graph_sha256(dict(reversed(list(first.items())))))
        self.assertNotEqual(module.source_graph_sha256(first), module.source_graph_sha256({"renamed.cs": "b"*64, "a.cs": "a"*64}))

    def test_missing_or_ambiguous_manifest_never_enables_exclusion(self):
        for body in ('{}', '{"schema":"a","schema":"b"}', '["unsupported"]'):
            with self.subTest(body=body):
                root, files, _ = self.fixture()
                (root/module.SOURCE_BOUNDARY).write_text(body, encoding="utf-8")
                with self.assertRaises(module.InventoryError): self.validate(root, self.bindings(root, files))

class ScanIdentityTests(unittest.TestCase):
    def setUp(self):
        self.cache = mock.patch.object(module, "_READ_CACHE", {})
        self.cache.start()
        self.addCleanup(self.cache.stop)

    def fixture(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "synthetic.cs"
        path.write_bytes(b"synthetic public fixture")
        return path

    def test_successful_scan_precedes_read_and_unchanged_identity_reuses_only_scanned_bytes(self):
        path = self.fixture()
        original_read = Path.read_bytes
        reads = []
        with mock.patch.object(module, "SCANNER", "synthetic-scanner"), mock.patch.object(
                module.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as scan:
            def observe_read(candidate):
                self.assertEqual(scan.call_count, 1)
                reads.append(candidate)
                return original_read(candidate)
            with mock.patch.object(Path, "read_bytes", observe_read):
                self.assertEqual(module.public_bytes(path), b"synthetic public fixture")
                self.assertEqual(module.public_bytes(path), b"synthetic public fixture")
        self.assertEqual(scan.call_count, 1)
        self.assertEqual(reads, [path.resolve()])

    def test_cached_file_mutation_is_refused_before_another_scan_or_content_read(self):
        path = self.fixture()
        with mock.patch.object(module, "SCANNER", "synthetic-scanner"), mock.patch.object(
                module.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as scan:
            module.public_bytes(path)
            path.write_bytes(b"changed after admission and now longer")
            with mock.patch.object(Path, "read_bytes") as read:
                with self.assertRaisesRegex(module.InventoryError, "changed after its successful scan"):
                    module.public_bytes(path)
                read.assert_not_called()
        self.assertEqual(scan.call_count, 1)

    def test_input_changed_by_scanner_is_never_read_or_cached(self):
        path = self.fixture()
        def scan_changes_input(*args, **kwargs):
            path.write_bytes(b"changed during scan and now longer")
            return subprocess.CompletedProcess([], 0)
        with mock.patch.object(module, "SCANNER", "synthetic-scanner"), mock.patch.object(
                module.subprocess, "run", side_effect=scan_changes_input), mock.patch.object(Path, "read_bytes") as read:
            with self.assertRaisesRegex(module.InventoryError, "changed during its secret scan"):
                module.public_bytes(path)
            read.assert_not_called()
        self.assertEqual(module._READ_CACHE, {})

    def test_input_changed_during_the_read_is_not_cached_as_admitted(self):
        path = self.fixture()
        original_read = Path.read_bytes
        def changing_read(candidate):
            value = original_read(candidate)
            candidate.write_bytes(value + b" changed")
            return value
        with mock.patch.object(module, "SCANNER", "synthetic-scanner"), mock.patch.object(
                module.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)), mock.patch.object(
                Path, "read_bytes", changing_read):
            with self.assertRaisesRegex(module.InventoryError, "changed during its post-scan read"):
                module.public_bytes(path)
        self.assertEqual(module._READ_CACHE, {})

    def test_scanner_start_failure_and_timeout_never_read_the_input(self):
        for failure in (OSError("synthetic scanner start failure"), subprocess.TimeoutExpired(["synthetic-scanner"], 120)):
            with self.subTest(category=type(failure).__name__):
                path = self.fixture()
                with mock.patch.object(module, "SCANNER", "synthetic-scanner"), mock.patch.object(
                        module.subprocess, "run", side_effect=failure), mock.patch.object(Path, "read_bytes") as read:
                    with self.assertRaisesRegex(module.InventoryError, "scan could not complete"):
                        module.public_bytes(path)
                    read.assert_not_called()
        self.assertEqual(module._READ_CACHE, {})

    def test_absent_file_directory_and_symlink_metadata_are_refused_before_scanning(self):
        path = self.fixture()
        for candidate in (path.parent / "missing.cs", path.parent):
            with self.subTest(kind=candidate.name), mock.patch.object(module.subprocess, "run") as scan:
                with self.assertRaisesRegex(module.InventoryError, "existing regular non-symlink"):
                    module.public_bytes(candidate)
                scan.assert_not_called()
        # Synthetic metadata refusal; this does not claim creation of a Windows symlink.
        with mock.patch.object(Path, "is_symlink", return_value=True), mock.patch.object(module.subprocess, "run") as scan:
            with self.assertRaisesRegex(module.InventoryError, "existing regular non-symlink"):
                module.public_bytes(path)
            scan.assert_not_called()


class ReceiptInputTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def write(self, name, value):
        path = self.root / name
        path.write_text(value, encoding="utf-8-sig")
        return path

    def read(self, paths):
        # Only synthetic receipt fixtures bypass their scanner subprocess here.
        # Real public_bytes scan/identity behavior has its own tests above.
        with mock.patch.object(module, "public_bytes", side_effect=lambda path: path.read_bytes()):
            return module.read_records(paths)

    def test_directory_expansion_is_sorted_and_duplicate_inputs_are_not_replayed(self):
        self.write("owner-sql-b.jsonl", '\n{"producerId":"second"}\n')
        first = self.write("owner-sql-a.jsonl", '{"producerId":"first"}\n\n')
        self.write("unrelated.jsonl", '{"mustNotAppear":true}')
        self.assertEqual(self.read([self.root, first, first]), [{"producerId": "first"}, {"producerId": "second"}])

    def test_explicit_file_glob_and_json_array_use_the_same_validated_input_reader(self):
        first = self.write("one.json", '[{"producerId":"first"},{"producerId":"second"}]')
        self.assertEqual(self.read([first]), [{"producerId": "first"}, {"producerId": "second"}])
        self.write("owner-sql-one.jsonl", '{"producerId":"third"}')
        self.assertEqual(self.read([self.root / "owner-sql-*.jsonl"]), [{"producerId": "third"}])

    def test_empty_directory_and_unmatched_glob_do_not_become_empty_success(self):
        for path in (self.root, self.root / "missing-*.jsonl"):
            with self.subTest(path=path.name), self.assertRaisesRegex(module.InventoryError, "No receipt files matched"):
                self.read([path])

    def test_malformed_json_lines_and_arrays_are_not_silently_dropped(self):
        for text in ('[{"missing":', '{"missing":\n', '{"ok":1}\nnot-json\n'):
            with self.subTest(text=text):
                path = self.write("broken.jsonl", text)
                with self.assertRaises(json.JSONDecodeError):
                    self.read([path])


class CompleteInventoryTests(unittest.TestCase):
    """Exercise the real builder with synthetic, explicitly sealed source data.

    No production implementation is copied or replaced. The fixture manifests are
    unit-test inputs, never review authority for the real Zeka source graph.
    """

    def write(self, root, relative, text):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def fixture(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        closed = InventoryTests.closed_fixture(self, root)
        for tool in module.TOOLS:
            self.write(root, tool + "/" + Path(tool).name + ".csproj", '<Project Sdk="Microsoft.NET.Sdk" />')
            self.write(root, tool + "/Program.cs", "class SyntheticFixture {}")
        for project in module.SERVICE_PROJECTS:
            sdk = "Microsoft.NET.Sdk.Web" if project == module.AUTH_WEB_PROJECT else "Microsoft.NET.Sdk"
            self.write(root, project, '<Project Sdk="' + sdk + '" />')
        for extra in module.EXTRA:
            self.write(root, extra, "class SyntheticFixture {}")
        self.write(root, "Tools/Shared/OwnerSqlExecution.cs", "class SyntheticAdapterFixture {}")
        self.write(root, "Tools/Zeka.AuthOwnerProvision/Program.cs", "await OwnerSqlExecution.ExecuteScalarAsync(command, ct);")
        self.write(root, module.QUERY_FILE, "\n".join("'" + value + "'" for value in module.QUERY_IDS + module.CONTROL_IDS))
        runtime = "Services/AuthManager/AuthManager.Infrastructure/SyntheticRuntime.cs"
        self.write(root, runtime, "await runtimeCommand.ExecuteScalarAsync(ct);")
        self.seal_closed(root, closed)
        self.seal_graph(root, runtime)
        return root, closed, runtime

    def seal_closed(self, root, manifest):
        for relative, item in manifest["files"].items():
            item["sha256"] = hashlib.sha256((root / relative).read_bytes()).hexdigest()
        self.write(root, module.CLOSED_SURFACE, json.dumps(manifest))

    def seal_graph(self, root, runtime):
        # The deterministic scanner is substituted only for these generated public
        # fixture bytes; every source-discovery and manifest check stays real.
        with mock.patch.object(module, "public_bytes", side_effect=lambda path: path.read_bytes()):
            projects, tools = module.discover_tools(root)
            projects, sources = module.discover_project_sources(root, projects + [root / path for path in module.SERVICE_PROJECTS])
        files = set(projects) | set(tools) | set(sources) | {root / path for path in module.EXTRA}
        files.add(root / "Tools/Shared/OwnerSqlExecution.cs")
        bindings = {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}
        manifest = {"schema": "zeka-owner-sql-referenced-source-boundary/v1", "reviewPolicy": module.SOURCE_BOUNDARY_POLICY,
                    "baseRevision": "a" * 40, "sourceGraphSha256": module.source_graph_sha256(bindings),
                    "runtimeOnlySources": {runtime: {"sha256": bindings[runtime], "classification": "service-runtime-only",
                                                     "reason": "Explicit synthetic fixture runtime classification."}}}
        self.write(root, module.SOURCE_BOUNDARY, json.dumps(manifest))

    def inventory(self, root):
        with mock.patch.object(module, "public_bytes", side_effect=lambda path: path.read_bytes()):
            return module.build_inventory(root)

    def test_complete_builder_binds_each_producer_dependency_and_separate_obligation(self):
        root, _, runtime = self.fixture()
        inventory = self.inventory(root)
        expected = {"Tools/Zeka.AuthOwnerProvision/Program.cs:L1", *module.QUERY_IDS, *module.CONTROL_IDS,
                    *("bootstrap:" + path + ":batch" for path in module.BOOTSTRAPS)}
        self.assertEqual({item["producerId"] for item in inventory["producers"]}, expected)
        self.assertEqual(len(inventory["producers"]), len(expected))
        self.assertEqual(inventory["scope"]["runtimeOnlySources"], [runtime])
        self.assertEqual(len(inventory["separateObligations"]), 2)
        for item in inventory["producers"]:
            self.assertEqual(item["producerSha256"], hashlib.sha256((root / item["source"]).read_bytes()).hexdigest())
            if item["producerId"] in module.DEPENDENCIES:
                self.assertEqual(set(item["sourceBindings"]), set(module.DEPENDENCIES[item["producerId"]]))

    def test_new_unsealed_default_helper_is_rejected_before_runtime_exclusion(self):
        root, _, _ = self.fixture()
        self.write(root, "Tools/Zeka.AuthOwnerProvision/NewHelper.cs", "class UnexpectedHelper {}")
        with self.assertRaisesRegex(module.InventoryError, "compiled source/project bytes or file set changed"):
            self.inventory(root)

    def test_reviewed_but_duplicate_physical_calls_never_share_one_producer_identity(self):
        root, _, runtime = self.fixture()
        self.write(root, "Tools/Zeka.AuthOwnerProvision/Program.cs",
                   "await OwnerSqlExecution.ExecuteScalarAsync(first, ct); await OwnerSqlExecution.ExecuteScalarAsync(second, ct);")
        self.seal_graph(root, runtime)
        with self.assertRaisesRegex(module.InventoryError, "Multiple executions share one physical producer line"):
            self.inventory(root)

    def test_reviewed_graph_with_no_handwritten_csharp_producer_is_not_a_complete_inventory(self):
        root, _, runtime = self.fixture()
        self.write(root, "Tools/Zeka.AuthOwnerProvision/Program.cs", "class NoOwnerExecution {}")
        self.seal_graph(root, runtime)
        with self.assertRaisesRegex(module.InventoryError, "Empty C# SQL inventory"):
            self.inventory(root)

    def test_query_identifier_change_is_rejected_even_when_script_bytes_are_sealed(self):
        root, closed, _ = self.fixture()
        path = root / module.QUERY_FILE
        path.write_text(path.read_text(encoding="utf-8") + "\n'owner.unregistered.v1'", encoding="utf-8")
        self.seal_closed(root, closed)
        with self.assertRaisesRegex(module.InventoryError, "query/control identifier set changed"):
            self.inventory(root)

    def test_late_source_mutation_is_rejected_by_the_final_byte_recheck(self):
        root, _, _ = self.fixture()
        original_recheck = module.recheck_source_sets
        def mutate_after_recheck(*arguments):
            original_recheck(*arguments)
            self.write(root, "Tools/Zeka.AuthOwnerProvision/Program.cs", "class ChangedAfterInventory {}")
        with mock.patch.object(module, "recheck_source_sets", side_effect=mutate_after_recheck):
            with self.assertRaisesRegex(module.InventoryError, "Source changed during inventory"):
                self.inventory(root)

    def test_missing_required_tool_scope_cannot_enter_discovery(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(module.InventoryError, "Missing source scope"):
                module.build_inventory(Path(directory))

    def command(self, root, *arguments):
        output = io.StringIO()
        # Only owned, public fixture files use this scanner seam. The real builder,
        # manifests, receipt reader and comparator still execute unchanged.
        with mock.patch.object(module, "public_bytes", side_effect=lambda path: path.read_bytes()), \
                mock.patch.object(module, "SCANNER", module.SCANNER), \
                mock.patch.object(sys, "argv", ["OwnerSqlInventory.py", "--source-root", str(root), *map(str, arguments)]), \
                contextlib.redirect_stdout(output):
            status = module.main()
            selected_scanner = module.SCANNER
        return status, json.loads(output.getvalue()), selected_scanner

    def receipts(self, inventory):
        return [{"schema": module.SCHEMA, "producerId": producer["producerId"],
                 "producerSha256": producer["producerSha256"],
                 "querySha256": producer["producerSha256"] if producer["kind"] == "bootstrap-batch" else "c" * 64,
                 "serverMajor": 17, "executionCompleted": True, "outcome": "success", "processId": 1,
                 "observedAtUtc": "2026-11-03T00:00:00Z", "sourceBindings": producer.get("sourceBindings", {})}
                for producer in inventory["producers"]]

    def test_cli_inventory_persists_the_actual_source_bound_inventory(self):
        root, _, _ = self.fixture()
        target = root / "inventory.json"
        expected = self.inventory(root)
        status, summary, scanner = self.command(root, "--inventory-out", target, "--sonar", "synthetic-fixture-scanner")
        self.assertEqual(status, 0)
        self.assertEqual(summary, {"status": "INVENTORIED", "producerCount": len(expected["producers"])})
        self.assertEqual(json.loads(target.read_text(encoding="utf-8")), expected)
        self.assertEqual(scanner, "synthetic-fixture-scanner")

    def test_cli_receipt_success_is_distinct_from_inventory_only_success(self):
        root, _, _ = self.fixture()
        inventory = self.inventory(root)
        receipts = self.write(root, "receipts.json", json.dumps(self.receipts(inventory)))
        report = root / "report.json"
        status, summary, _ = self.command(root, "--receipts", receipts, "--report", report)
        self.assertEqual(status, 0)
        self.assertEqual(summary, {"status": "PASS", "expectedProducerCount": len(inventory["producers"]),
                                   "successfulProducerCount": len(inventory["producers"])})
        result = json.loads(report.read_text(encoding="utf-8"))
        self.assertEqual(result["missingSuccessfulProducers"], [])
        self.assertEqual(result["invalidReceiptReasons"], [])
        self.assertEqual(result["separateObligations"], inventory["separateObligations"])

    def test_cli_empty_receipt_selection_cannot_claim_executed_producers(self):
        root, _, _ = self.fixture()
        report = root / "report.json"
        status, summary, _ = self.command(root, "--receipts", "--report", report)
        self.assertEqual(status, 2)
        self.assertEqual(summary["status"], "FAIL")
        self.assertEqual(summary["successfulProducerCount"], 0)
        result = json.loads(report.read_text(encoding="utf-8"))
        self.assertEqual(len(result["missingSuccessfulProducers"]), summary["expectedProducerCount"])

    def test_cli_malformed_receipt_never_exports_its_contents_or_a_success_report(self):
        root, _, _ = self.fixture()
        marker = "private-row-canary-not-a-credential"
        receipt = self.write(root, "receipts.json", '[{"syntheticRow":"' + marker + '",')
        report = root / "report.json"
        status, summary, _ = self.command(root, "--receipts", receipt, "--report", report)
        self.assertEqual((status, summary), (2, {"status": "FAIL", "reason": "JSONDecodeError"}))
        self.assertNotIn(marker, json.dumps(summary))
        self.assertFalse(report.exists())

    def test_cli_output_io_error_is_sanitized_without_success(self):
        root, _, _ = self.fixture()
        target = root / "not-created" / "inventory.json"
        status, summary, _ = self.command(root, "--inventory-out", target)
        self.assertEqual((status, summary), (2, {"status": "FAIL", "reason": "FileNotFoundError"}))
        self.assertNotIn(str(root), json.dumps(summary))
        self.assertFalse(target.exists())

    def test_cli_report_io_failure_does_not_print_a_passing_receipt_summary(self):
        root, _, _ = self.fixture()
        receipts = self.write(root, "receipts.json", json.dumps(self.receipts(self.inventory(root))))
        target = root / "not-created" / "report.json"
        status, summary, _ = self.command(root, "--receipts", receipts, "--report", target)
        self.assertEqual((status, summary), (2, {"status": "FAIL", "reason": "FileNotFoundError"}))
        self.assertFalse(target.exists())

    def test_cli_unmatched_receipt_input_preserves_the_controlled_refusal(self):
        root, _, _ = self.fixture()
        status, summary, _ = self.command(root, "--receipts", root / "absent-*.jsonl")
        self.assertEqual((status, summary), (2, {"status": "FAIL", "reason": "No receipt files matched an explicit input"}))

    def test_cli_source_drift_is_not_reported_as_an_empty_successful_inventory(self):
        root, _, _ = self.fixture()
        self.write(root, "Tools/Zeka.AuthOwnerProvision/Unexpected.cs", "class Unexpected {}")
        status, summary, _ = self.command(root)
        self.assertEqual(status, 2)
        self.assertEqual(summary, {"status": "FAIL", "reason": "Referenced compiled source/project bytes or file set changed; explicit review required"})


if __name__ == "__main__": unittest.main()
