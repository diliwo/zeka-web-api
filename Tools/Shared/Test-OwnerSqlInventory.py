"""Credential-free parser/comparator checks; these never claim actual PG execution."""
import importlib.util
import hashlib
import json
import unittest
from unittest import mock
import tempfile
import subprocess
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

if __name__ == "__main__": unittest.main()
