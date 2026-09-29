-- Run once per dedicated LOGIN role over TCP (not SET ROLE).
DO $assert$
DECLARE expected_participant text;
        role_record record;
        table_name text;
        policy_name text;
BEGIN
  expected_participant := CASE current_user
    WHEN 'zeka_life05a_verify_auth_management' THEN 'auth-management'
    WHEN 'zeka_life05a_verify_admin_area' THEN 'admin-area'
    WHEN 'zeka_life05a_verify_admin_area_documents' THEN 'admin-area-documents'
    WHEN 'zeka_life05a_verify_client_management' THEN 'client-management'
  END;
  IF expected_participant IS NULL THEN
    RAISE EXCEPTION 'unknown verifier principal: %', current_user;
  END IF;
  policy_name := replace(current_user, 'zeka_life05a_', 'life05a_');
  SELECT rolcanlogin, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole,
         rolinherit, rolreplication
    INTO role_record FROM pg_roles WHERE rolname=current_user;
  IF NOT role_record.rolcanlogin OR role_record.rolsuper OR role_record.rolbypassrls
     OR role_record.rolcreatedb OR role_record.rolcreaterole OR role_record.rolinherit
     OR role_record.rolreplication
     OR EXISTS (SELECT FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.member
                WHERE r.rolname=current_user)
     OR EXISTS (SELECT FROM pg_auth_members m JOIN pg_roles r ON r.oid=m.roleid
                WHERE r.rolname=current_user)
     OR NOT has_database_privilege(current_user,current_database(),'CONNECT')
     OR has_schema_privilege(current_user,'life04a_fixture','CREATE')
     OR has_schema_privilege(current_user,'zeka','CREATE')
     OR NOT has_function_privilege(current_user,'zeka.current_organisation_id()','EXECUTE') THEN
    RAISE EXCEPTION 'verifier role boundary failed';
  END IF;
  FOREACH table_name IN ARRAY ARRAY['Items','Payloads'] LOOP
    IF NOT has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'SELECT')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'INSERT')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'UPDATE')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'DELETE')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'TRUNCATE')
       OR NOT EXISTS (
         SELECT FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
         WHERE n.nspname='life04a_fixture' AND c.relname=table_name
           AND c.relrowsecurity AND c.relforcerowsecurity)
       OR NOT EXISTS (
         SELECT FROM pg_policies p WHERE p.schemaname='life04a_fixture'
           AND p.tablename=table_name AND p.cmd='SELECT'
           AND p.policyname=policy_name AND p.roles=ARRAY[current_user]::name[])
    THEN
      RAISE EXCEPTION 'verifier table boundary failed for %',table_name;
    END IF;
  END LOOP;
  FOREACH table_name IN ARRAY ARRAY['Inbox','Starts','Auth','Progress'] LOOP
    IF has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'SELECT')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'INSERT')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'UPDATE')
       OR has_table_privilege(current_user,format('life04a_fixture.%I',table_name),'DELETE') THEN
      RAISE EXCEPTION 'forbidden fixture access: %',table_name;
    END IF;
  END LOOP;
END $assert$;

DO $denials$
DECLARE statement text;
BEGIN
  FOREACH statement IN ARRAY ARRAY[
    'SELECT count(*) FROM life04a_fixture."Inbox"',
    'SELECT count(*) FROM life04a_fixture."Starts"',
    'SELECT count(*) FROM life04a_fixture."Auth"',
    'SELECT count(*) FROM life04a_fixture."Progress"',
    'INSERT INTO life04a_fixture."Items" ("OrganisationId") VALUES (NULL)',
    'INSERT INTO life04a_fixture."Payloads" ("OrganisationId") VALUES (NULL)',
    'UPDATE life04a_fixture."Items" SET "Disposition"=''RETAIN''',
    'UPDATE life04a_fixture."Payloads" SET "SyntheticContent"=''altered''',
    'DELETE FROM life04a_fixture."Items"',
    'DELETE FROM life04a_fixture."Payloads"',
    'TRUNCATE life04a_fixture."Items"',
    'TRUNCATE life04a_fixture."Payloads"'
  ] LOOP
    BEGIN
      EXECUTE statement;
      RAISE EXCEPTION 'forbidden SQL unexpectedly succeeded: %', statement;
    EXCEPTION WHEN insufficient_privilege THEN
      NULL;
    END;
  END LOOP;
END $denials$;

DO $no_context$
BEGIN
  IF (SELECT count(*) FROM life04a_fixture."Items") <> 0
     OR (SELECT count(*) FROM life04a_fixture."Payloads") <> 0 THEN
    RAISE EXCEPTION 'missing context exposed rows';
  END IF;
END $no_context$;

BEGIN;
SELECT set_config('zeka.organisation_id','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',true);
DO $tenant_a$
DECLARE expected_participant text := CASE current_user
    WHEN 'zeka_life05a_verify_auth_management' THEN 'auth-management'
    WHEN 'zeka_life05a_verify_admin_area' THEN 'admin-area'
    WHEN 'zeka_life05a_verify_admin_area_documents' THEN 'admin-area-documents'
    WHEN 'zeka_life05a_verify_client_management' THEN 'client-management'
  END;
BEGIN
  IF (SELECT count(*) FROM life04a_fixture."Items") <> 1
     OR (SELECT count(*) FROM life04a_fixture."Payloads") <> 1
     OR (SELECT count(*) FROM life04a_fixture."Items"
         WHERE "ParticipantId"=expected_participant
           AND "OrganisationId"='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa') <> 1
     OR (SELECT count(*) FROM life04a_fixture."Payloads"
         WHERE "ParticipantId"=expected_participant
           AND "OrganisationId"='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa') <> 1
     OR EXISTS (SELECT FROM life04a_fixture."Items"
                WHERE "ParticipantId"<>expected_participant
                   OR "OrganisationId"<>'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')
     OR EXISTS (SELECT FROM life04a_fixture."Payloads"
                WHERE "ParticipantId"<>expected_participant
                   OR "OrganisationId"<>'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa') THEN
    RAISE EXCEPTION 'tenant A or cross-participant verifier scope failed';
  END IF;
END $tenant_a$;
COMMIT;

BEGIN;
SELECT set_config('zeka.organisation_id','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',true);
DO $tenant_b$
DECLARE expected_participant text := CASE current_user
    WHEN 'zeka_life05a_verify_auth_management' THEN 'auth-management'
    WHEN 'zeka_life05a_verify_admin_area' THEN 'admin-area'
    WHEN 'zeka_life05a_verify_admin_area_documents' THEN 'admin-area-documents'
    WHEN 'zeka_life05a_verify_client_management' THEN 'client-management'
  END;
BEGIN
  IF (SELECT count(*) FROM life04a_fixture."Items") <> 1
     OR (SELECT count(*) FROM life04a_fixture."Payloads") <> 1
     OR (SELECT count(*) FROM life04a_fixture."Items"
         WHERE "ParticipantId"=expected_participant
           AND "OrganisationId"='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb') <> 1
     OR (SELECT count(*) FROM life04a_fixture."Payloads"
         WHERE "ParticipantId"=expected_participant
           AND "OrganisationId"='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb') <> 1
     OR EXISTS (SELECT FROM life04a_fixture."Items"
                WHERE "ParticipantId"<>expected_participant
                   OR "OrganisationId"<>'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
     OR EXISTS (SELECT FROM life04a_fixture."Payloads"
                WHERE "ParticipantId"<>expected_participant
                   OR "OrganisationId"<>'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb') THEN
    RAISE EXCEPTION 'tenant B or cross-participant verifier scope failed';
  END IF;
END $tenant_b$;
ROLLBACK;
