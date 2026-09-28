-- Run once per ClientManagement database using a separately authorized PostgreSQL role administrator.
DO $bootstrap$
DECLARE managed_role text;
        database_name text;
        login_mode text;
BEGIN
  FOREACH managed_role IN ARRAY ARRAY[
    'zeka_client_owner', 'zeka_client_migrator', 'zeka_client_runtime'
  ] LOOP
    login_mode := CASE WHEN managed_role='zeka_client_owner' THEN 'NOLOGIN' ELSE 'LOGIN' END;
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname=managed_role) THEN
      EXECUTE pg_catalog.format(
        'CREATE ROLE %I %s NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
        managed_role, login_mode);
    END IF;
    EXECUTE pg_catalog.format(
      'ALTER ROLE %I %s NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION',
      managed_role, login_mode);
    EXECUTE pg_catalog.format('ALTER ROLE %I RESET ALL', managed_role);
    FOR database_name IN
      SELECT database.datname
      FROM pg_catalog.pg_db_role_setting role_setting
      JOIN pg_catalog.pg_roles role ON role.oid=role_setting.setrole
      JOIN pg_catalog.pg_database database ON database.oid=role_setting.setdatabase
      WHERE role.rolname=managed_role
    LOOP
      EXECUTE pg_catalog.format(
        'ALTER ROLE %I IN DATABASE %I RESET ALL', managed_role, database_name);
    END LOOP;
  END LOOP;
END $bootstrap$;
DO $database$ BEGIN
  EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO zeka_client_migrator, zeka_client_runtime', current_database());
END $database$;
GRANT zeka_client_owner TO zeka_client_migrator WITH INHERIT FALSE, SET TRUE, ADMIN FALSE;
DO $memberships$
DECLARE membership record;
BEGIN
  FOR membership IN
    SELECT parent.rolname AS parent_name, member.rolname AS member_name
    FROM pg_auth_members m JOIN pg_roles parent ON parent.oid=m.roleid JOIN pg_roles member ON member.oid=m.member
    WHERE (parent.rolname IN ('zeka_client_owner','zeka_client_migrator','zeka_client_runtime')
       OR member.rolname IN ('zeka_client_owner','zeka_client_migrator','zeka_client_runtime'))
      AND NOT (parent.rolname='zeka_client_owner' AND member.rolname='zeka_client_migrator')
  LOOP EXECUTE format('REVOKE %I FROM %I', membership.parent_name, membership.member_name); END LOOP;
END $memberships$;
ALTER SCHEMA public OWNER TO zeka_client_owner;
CREATE SCHEMA IF NOT EXISTS zeka AUTHORIZATION zeka_client_owner;
ALTER SCHEMA zeka OWNER TO zeka_client_owner;
REVOKE ALL ON SCHEMA zeka FROM PUBLIC, zeka_client_runtime;
GRANT USAGE ON SCHEMA zeka TO zeka_client_runtime;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO zeka_client_migrator, zeka_client_runtime;
DO $ownership$
DECLARE object record;
BEGIN
  FOR object IN SELECT format('%I.%I', n.nspname, c.relname) name
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND c.relkind IN ('r','p')
  LOOP
    EXECUTE 'ALTER TABLE ' || object.name || ' OWNER TO zeka_client_owner';
  END LOOP;
END $ownership$;
DO $functions$
DECLARE object record;
BEGIN
  FOR object IN SELECT n.nspname, p.proname, pg_get_function_identity_arguments(p.oid) arguments
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public'
  LOOP EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO zeka_client_owner', object.nspname, object.proname, object.arguments); END LOOP;
END $functions$;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC, zeka_client_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC, zeka_client_runtime;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC, zeka_client_runtime;
DO $default_privileges$
DECLARE privilege_rule record;
BEGIN
  FOR privilege_rule IN
    SELECT * FROM (VALUES
      ('', 'ALL', 'TABLES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('', 'ALL', 'SEQUENCES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('', 'EXECUTE', 'FUNCTIONS', 'PUBLIC'),
      ('', 'ALL', 'FUNCTIONS', 'zeka_client_migrator, zeka_client_runtime'),
      ('', 'ALL', 'TYPES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('', 'ALL', 'SCHEMAS', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA public ', 'ALL', 'TABLES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA public ', 'ALL', 'SEQUENCES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA public ', 'ALL', 'FUNCTIONS', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA public ', 'ALL', 'TYPES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA zeka ', 'ALL', 'TABLES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA zeka ', 'ALL', 'SEQUENCES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA zeka ', 'ALL', 'FUNCTIONS', 'PUBLIC, zeka_client_migrator, zeka_client_runtime'),
      ('IN SCHEMA zeka ', 'ALL', 'TYPES', 'PUBLIC, zeka_client_migrator, zeka_client_runtime')
    ) AS manifest(scope_clause, privileges, object_kind, grantees)
  LOOP
    EXECUTE pg_catalog.format(
      'ALTER DEFAULT PRIVILEGES FOR ROLE zeka_client_owner %sREVOKE %s ON %s FROM %s',
      privilege_rule.scope_clause, privilege_rule.privileges,
      privilege_rule.object_kind, privilege_rule.grantees);
  END LOOP;
END $default_privileges$;
DO $existing_objects$
DECLARE object_name text;
        grant_rule record;
        lifecycle_object record;
        column_grant record;
        target_table regclass;
        grantee_sql text;
        update_list text;
        table_privileges text;
BEGIN
  FOR grant_rule IN
    SELECT * FROM (VALUES
      ('TABLE', 'SELECT, INSERT, UPDATE, DELETE', ARRAY[
        'Clients','SocialWorkers','SocialCases','Assessments','ProfessionalAssessments',
        'ProfessionnalExperience','SchoolRegistrations','MonitoringReports','MonitoringActions'
      ]),
      ('TABLE', 'SELECT', ARRAY[
        'Languages','NatureOfContract','Profession','School','Training','TrainingField','TrainingType'
      ]),
      ('SEQUENCE', 'SELECT, USAGE', ARRAY[
        'Assessments_AssessmentId_seq','Clients_Id_seq','MonitoringActions_ActionId_seq',
        'MonitoringReports_Id_seq','ProfessionalAssessments_Id_seq',
        'ProfessionnalExperience_ProfessionnalExperienceId_seq','SchoolRegistrations_SchoolRegistrationId_seq',
        'SocialCases_SchoolRegistrationId_seq','SocialWorkers_SocialWorkerId_seq',
        'OrganisationExportFences_Id_seq','OrganisationExportFragments_Id_seq',
        'OrganisationExportInbox_Id_seq','OrganisationExportOutbox_Id_seq',
        'OrganisationClosureFences_Id_seq','OrganisationClosureInbox_Id_seq',
        'OrganisationClosureOutbox_Id_seq'
      ])
    ) AS grants(object_kind, privileges, object_names)
  LOOP
    FOREACH object_name IN ARRAY grant_rule.object_names LOOP
      IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
        EXECUTE pg_catalog.format(
          'GRANT %s ON %s public.%I TO zeka_client_runtime',
          grant_rule.privileges, grant_rule.object_kind, object_name);
      END IF;
    END LOOP;
  END LOOP;
  FOR lifecycle_object IN
    SELECT * FROM (VALUES
      ('OrganisationExportFences', ARRAY['ReleasedAt']::text[]),
      ('OrganisationExportFragments', ARRAY[]::text[]),
      ('OrganisationExportInbox', ARRAY[]::text[]),
      ('OrganisationExportOutbox', ARRAY[]::text[]),
      ('OrganisationClosureFences', ARRAY[]::text[]),
      ('OrganisationClosureInbox', ARRAY[]::text[]),
      ('OrganisationClosureOutbox', ARRAY[]::text[])
    ) AS inventory(name, update_columns)
  LOOP
    target_table := pg_catalog.to_regclass(pg_catalog.format('public.%I', lifecycle_object.name));
    IF target_table IS NOT NULL THEN
      EXECUTE pg_catalog.format(
        'REVOKE ALL ON TABLE public.%I FROM PUBLIC, zeka_client_runtime', lifecycle_object.name);
      FOR column_grant IN
        SELECT DISTINCT attribute.attname, acl.grantee, relation.relowner
        FROM pg_catalog.pg_class relation
        JOIN pg_catalog.pg_attribute attribute ON attribute.attrelid=relation.oid
        CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) AS acl
        WHERE relation.oid=target_table AND attribute.attnum>0
          AND NOT attribute.attisdropped AND acl.grantee<>relation.relowner
      LOOP
        grantee_sql := CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
          ELSE pg_catalog.quote_ident(pg_catalog.pg_get_userbyid(column_grant.grantee)) END;
        EXECUTE pg_catalog.format(
          'REVOKE ALL (%I) ON TABLE public.%I FROM %s CASCADE',
          column_grant.attname, lifecycle_object.name, grantee_sql);
      END LOOP;
      IF pg_catalog.cardinality(lifecycle_object.update_columns) > 0 THEN
        SELECT pg_catalog.string_agg(pg_catalog.quote_ident(column_name), ', ')
          INTO update_list FROM pg_catalog.unnest(lifecycle_object.update_columns) column_name;
        table_privileges := pg_catalog.format('SELECT, INSERT, UPDATE (%s)', update_list);
      ELSE
        table_privileges := 'SELECT, INSERT';
      END IF;
      EXECUTE pg_catalog.format(
        'GRANT %s ON TABLE public.%I TO zeka_client_runtime',
        table_privileges, lifecycle_object.name);
    END IF;
  END LOOP;
  IF pg_catalog.to_regprocedure('zeka.current_organisation_id()') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_client_runtime;
  END IF;
  IF pg_catalog.to_regprocedure('zeka.reject_writes_during_export_fence()') IS NOT NULL THEN
    REVOKE ALL ON FUNCTION zeka.reject_writes_during_export_fence() FROM PUBLIC, zeka_client_runtime;
    GRANT EXECUTE ON FUNCTION zeka.reject_writes_during_export_fence() TO zeka_client_runtime;
  END IF;
  FOREACH object_name IN ARRAY ARRAY[
    'zeka.reject_writes_during_closure_fence()',
    'zeka.release_organisation_closure_fence(uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)'
  ] LOOP
    IF pg_catalog.to_regprocedure(object_name) IS NOT NULL THEN
      EXECUTE pg_catalog.format('ALTER FUNCTION %s OWNER TO zeka_client_owner', object_name);
      EXECUTE pg_catalog.format(
        'REVOKE ALL ON FUNCTION %s FROM PUBLIC, zeka_client_runtime', object_name);
      EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION %s TO zeka_client_runtime', object_name);
    END IF;
  END LOOP;
END $existing_objects$;
