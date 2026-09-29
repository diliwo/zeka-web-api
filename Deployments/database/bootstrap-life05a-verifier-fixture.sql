-- LIFE-05A synthetic fixture only. Run as a role administrator after the fixture
-- tables and zeka.current_organisation_id() exist. Never apply to production.
-- Stable mapping for the fixture harness:
-- auth-management       -> zeka_life05a_verify_auth_management
-- admin-area            -> zeka_life05a_verify_admin_area
-- admin-area-documents  -> zeka_life05a_verify_admin_area_documents
-- client-management     -> zeka_life05a_verify_client_management
DO $bootstrap$
DECLARE participant record;
        relation_name text;
        membership record;
        database_name text;
BEGIN
  IF to_regnamespace('life04a_fixture') IS NULL
     OR to_regprocedure('zeka.current_organisation_id()') IS NULL THEN
    RAISE EXCEPTION 'LIFE-05A fixture prerequisites are absent';
  END IF;
  FOREACH relation_name IN ARRAY ARRAY['Items', 'Payloads'] LOOP
    IF to_regclass(format('life04a_fixture.%I', relation_name)) IS NULL THEN
      RAISE EXCEPTION 'LIFE-05A fixture table % is absent', relation_name;
    END IF;
  END LOOP;
  FOR participant IN
    SELECT * FROM (VALUES
      ('auth_management', 'auth-management'),
      ('admin_area', 'admin-area'),
      ('admin_area_documents', 'admin-area-documents'),
      ('client_management', 'client-management')
    ) AS participants(role_suffix, participant_id)
  LOOP
    IF NOT EXISTS (SELECT FROM pg_roles
                   WHERE rolname='zeka_life05a_verify_' || participant.role_suffix) THEN
      EXECUTE format('CREATE ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION',
        'zeka_life05a_verify_' || participant.role_suffix);
    END IF;
    EXECUTE format('ALTER ROLE %I LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('ALTER ROLE %I RESET ALL', 'zeka_life05a_verify_' || participant.role_suffix);
    FOR database_name IN
      SELECT database.datname
      FROM pg_db_role_setting setting
      JOIN pg_roles role ON role.oid=setting.setrole
      JOIN pg_database database ON database.oid=setting.setdatabase
      WHERE role.rolname='zeka_life05a_verify_' || participant.role_suffix
    LOOP
      EXECUTE format('ALTER ROLE %I IN DATABASE %I RESET ALL',
        'zeka_life05a_verify_' || participant.role_suffix, database_name);
    END LOOP;
    FOR membership IN
      SELECT parent.rolname AS parent_name, member.rolname AS member_name
      FROM pg_auth_members m JOIN pg_roles parent ON parent.oid=m.roleid
        JOIN pg_roles member ON member.oid=m.member
      WHERE parent.rolname='zeka_life05a_verify_' || participant.role_suffix
         OR member.rolname='zeka_life05a_verify_' || participant.role_suffix
    LOOP
      EXECUTE format('REVOKE %I FROM %I', membership.parent_name, membership.member_name);
    END LOOP;
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I',
      current_database(), 'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('REVOKE ALL ON SCHEMA life04a_fixture, zeka FROM %I',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('GRANT USAGE ON SCHEMA life04a_fixture, zeka TO %I',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA life04a_fixture FROM %I',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA life04a_fixture FROM %I',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA zeka FROM %I',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO %I',
      'zeka_life05a_verify_' || participant.role_suffix);
    EXECUTE format('GRANT SELECT ON TABLE life04a_fixture."Items", life04a_fixture."Payloads" TO %I',
      'zeka_life05a_verify_' || participant.role_suffix);
  END LOOP;
END $bootstrap$;

ALTER TABLE life04a_fixture."Items" ENABLE ROW LEVEL SECURITY;
ALTER TABLE life04a_fixture."Items" FORCE ROW LEVEL SECURITY;
ALTER TABLE life04a_fixture."Payloads" ENABLE ROW LEVEL SECURITY;
ALTER TABLE life04a_fixture."Payloads" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS life05a_verifier_items ON life04a_fixture."Items";
DROP POLICY IF EXISTS life05a_verifier_payloads ON life04a_fixture."Payloads";

DO $policies$
DECLARE participant record;
        relation_name text;
BEGIN
  FOR participant IN
    SELECT * FROM (VALUES
      ('auth_management', 'auth-management'),
      ('admin_area', 'admin-area'),
      ('admin_area_documents', 'admin-area-documents'),
      ('client_management', 'client-management')
    ) AS participants(role_suffix, participant_id)
  LOOP
    FOREACH relation_name IN ARRAY ARRAY['Items', 'Payloads'] LOOP
      EXECUTE format('DROP POLICY IF EXISTS %I ON life04a_fixture.%I',
        'life05a_verify_' || participant.role_suffix, relation_name);
      EXECUTE format(
        'CREATE POLICY %I ON life04a_fixture.%I FOR SELECT TO %I USING ("ParticipantId" = %L AND "OrganisationId" = zeka.current_organisation_id())',
        'life05a_verify_' || participant.role_suffix, relation_name,
        'zeka_life05a_verify_' || participant.role_suffix, participant.participant_id);
    END LOOP;
  END LOOP;
END $policies$;
