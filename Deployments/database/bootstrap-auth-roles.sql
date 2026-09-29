-- AuthManagement registry tables are explicit Issue #45 RLS exclusions; role separation still applies.
-- Lifecycle grants are reconciled only when the separately migrated objects exist.
DO $bootstrap$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_auth_owner') THEN
    CREATE ROLE zeka_auth_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_auth_migrator') THEN
    CREATE ROLE zeka_auth_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_auth_runtime') THEN
    CREATE ROLE zeka_auth_runtime LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_auth_closure_recovery') THEN
    CREATE ROLE zeka_auth_closure_recovery NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
END $bootstrap$;
ALTER ROLE zeka_auth_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_owner RESET ALL;
ALTER ROLE zeka_auth_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_migrator RESET ALL;
ALTER ROLE zeka_auth_runtime LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_runtime RESET ALL;
ALTER ROLE zeka_auth_closure_recovery NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_closure_recovery PASSWORD NULL;
ALTER ROLE zeka_auth_closure_recovery RESET ALL;
DO $settings$
DECLARE setting record;
BEGIN
  FOR setting IN
    SELECT role.rolname, database.datname
    FROM pg_catalog.pg_db_role_setting role_setting
    JOIN pg_catalog.pg_roles role ON role.oid=role_setting.setrole
    JOIN pg_catalog.pg_database database ON database.oid=role_setting.setdatabase
    WHERE role.rolname IN (
      'zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery')
  LOOP
    EXECUTE pg_catalog.format('ALTER ROLE %I IN DATABASE %I RESET ALL', setting.rolname, setting.datname);
  END LOOP;
END $settings$;
DO $managed_schema$
DECLARE schema_owner name;
BEGIN
  SELECT pg_catalog.pg_get_userbyid(namespace.nspowner)
    INTO schema_owner
    FROM pg_catalog.pg_namespace namespace
    WHERE namespace.nspname = 'zeka';

  IF NOT FOUND THEN
    CREATE SCHEMA zeka AUTHORIZATION zeka_auth_owner;
  ELSIF schema_owner <> 'zeka_auth_owner' THEN
    RAISE EXCEPTION USING
      ERRCODE = '42501',
      MESSAGE = 'managed schema zeka has unexpected owner';
  END IF;
END $managed_schema$;
DO $database$ BEGIN
  EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
  EXECUTE format(
    'REVOKE ALL ON DATABASE %I FROM zeka_auth_owner, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery',
    current_database());
  EXECUTE format(
    'GRANT CONNECT ON DATABASE %I TO zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery',
    current_database());
END $database$;
GRANT zeka_auth_owner TO zeka_auth_migrator WITH INHERIT FALSE, SET TRUE, ADMIN FALSE;
DO $memberships$
DECLARE membership record;
BEGIN
  FOR membership IN
    SELECT parent.rolname AS parent_name, member.rolname AS member_name
    FROM pg_auth_members m JOIN pg_roles parent ON parent.oid=m.roleid JOIN pg_roles member ON member.oid=m.member
    WHERE (parent.rolname IN (
             'zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery')
       OR member.rolname IN (
             'zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime','zeka_auth_closure_recovery'))
      AND NOT (parent.rolname='zeka_auth_owner' AND member.rolname='zeka_auth_migrator')
  LOOP EXECUTE format('REVOKE %I FROM %I', membership.parent_name, membership.member_name); END LOOP;
END $memberships$;
ALTER SCHEMA public OWNER TO zeka_auth_owner;
REVOKE ALL ON SCHEMA public
  FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
GRANT USAGE ON SCHEMA public TO zeka_auth_migrator, zeka_auth_runtime;
REVOKE ALL ON SCHEMA zeka
  FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
GRANT USAGE ON SCHEMA zeka TO zeka_auth_runtime, zeka_auth_closure_recovery;
DO $ownership$
DECLARE object record;
        table_grant record;
        public_grantee CONSTANT text := 'PUBLIC';
BEGIN
  FOR object IN SELECT c.oid, n.nspname, c.relname
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname IN ('public','zeka') AND c.relkind IN ('r','p')
  LOOP
    EXECUTE pg_catalog.format(
      'ALTER TABLE %I.%I OWNER TO zeka_auth_owner', object.nspname, object.relname);
    FOR table_grant IN
      SELECT DISTINCT acl.grantee, pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
      FROM pg_catalog.pg_class relation
      CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
      WHERE relation.oid=object.oid AND acl.grantee<>relation.relowner
    LOOP
      EXECUTE pg_catalog.format(
        'REVOKE ALL ON TABLE %I.%I FROM %s CASCADE',
        object.nspname, object.relname,
        CASE WHEN table_grant.grantee=0 THEN public_grantee
          ELSE pg_catalog.quote_ident(table_grant.grantee_name) END);
    END LOOP;
  END LOOP;
END $ownership$;
DO $migration_history$
BEGIN
  IF pg_catalog.to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
    GRANT SELECT, INSERT, UPDATE, DELETE
      ON TABLE public."__EFMigrationsHistory" TO zeka_auth_migrator;
  END IF;
END $migration_history$;
DO $functions$
DECLARE object record;
BEGIN
  FOR object IN SELECT n.nspname, p.proname, pg_get_function_identity_arguments(p.oid) arguments
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('public','zeka')
  LOOP EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO zeka_auth_owner', object.nspname, object.proname, object.arguments); END LOOP;
END $functions$;
REVOKE ALL ON ALL TABLES IN SCHEMA public
  FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public
  FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public
  FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
DO $managed_schema_objects$
BEGIN
  IF pg_catalog.to_regnamespace('zeka') IS NOT NULL THEN
    REVOKE ALL ON ALL TABLES IN SCHEMA zeka
      FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
    REVOKE ALL ON ALL SEQUENCES IN SCHEMA zeka
      FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
    REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA zeka
      FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
  END IF;
END $managed_schema_objects$;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON TABLES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON FUNCTIONS FROM zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON TYPES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON SCHEMAS FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON TYPES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
DO $managed_schema_defaults$
BEGIN
  IF pg_catalog.to_regnamespace('zeka') IS NOT NULL THEN
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON TABLES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON TYPES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime, zeka_auth_closure_recovery;
  END IF;
END $managed_schema_defaults$;
DO $existing_objects$
DECLARE object_name text;
BEGIN
  FOREACH object_name IN ARRAY ARRAY[
    'AspNetRoleClaims','AspNetRoles','AspNetUserClaims','AspNetUserLogins','AspNetUserRoles',
    'AspNetUserTokens','AspNetUsers','AuditEntries','IdempotencyRecords','OrganisationMemberships',
    'Organisations','OutboxMessages','PermissionSets'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO zeka_auth_runtime', object_name);
    END IF;
  END LOOP;
  FOREACH object_name IN ARRAY ARRAY['AspNetRoleClaims_Id_seq','AspNetUserClaims_Id_seq'] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('GRANT SELECT, USAGE ON SEQUENCE public.%I TO zeka_auth_runtime', object_name);
    END IF;
  END LOOP;
END $existing_objects$;
DO $lifecycle_objects$
DECLARE object_name text;
        lifecycle_object record;
        table_grant record;
        column_grant record;
        function_name text;
        function_grant record;
        update_list text;
        public_grantee CONSTANT text := 'PUBLIC';
BEGIN
  FOREACH object_name IN ARRAY ARRAY[
    'LifecycleParticipantRegistryRevisions','LifecycleParticipantRegistryBindings',
    'LifecycleParticipantRegistryActivation'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('GRANT SELECT ON TABLE public.%I TO zeka_auth_runtime', object_name);
    END IF;
  END LOOP;
  -- Table REVOKE does not erase pg_attribute.attacl. Reconcile every lifecycle table from
  -- one explicit data inventory so future columns cannot inherit stale grants.
  FOR lifecycle_object IN
    SELECT * FROM (VALUES
      ('OrganisationLifecycleOperations', ARRAY['State','Revision','SnapshotAt','FenceEvidenceHash',
        'PackageSha256','PackageReference','FailureCode','CompletedAt','IsActive','ClosingAt',
        'ArchivedAt','ClosureFenceEvidenceHash','DispositionReadyAt',
        'RetentionDecisionSetHash']::text[], false),
      ('OrganisationLifecycleParticipants', ARRAY['State','FailureBoundaryDisposition','FailureCode',
        'FailureRetryable','FailedAt']::text[], false),
      ('LifecycleCoordinatorLeases', ARRAY['LeaseId','ExpiresAt','Version']::text[], false),
      ('LifecycleClosureFenceReceipts', ARRAY[]::text[], true),
      ('LifecycleExportFenceReceipts', ARRAY[]::text[], false),
      ('LifecycleExportFragments', ARRAY[]::text[], false),
      ('LifecycleExportPackages', ARRAY[]::text[], false),
      ('LifecycleInboxReceipts', ARRAY[]::text[], false),
      ('AuthExportParticipantExecutions', ARRAY['SnapshotAt','FenceEvidenceHash','FragmentHash',
        'CategoriesJson','ReleasedAt','State']::text[], true),
      ('AuthExportParticipantInbox', ARRAY[]::text[], true),
      ('AuthExportParticipantOutbox', ARRAY[]::text[], true),
      ('AuthClosureParticipantExecutions', ARRAY[]::text[], true),
      ('AuthClosureParticipantInbox', ARRAY[]::text[], true),
      ('AuthClosureParticipantOutbox', ARRAY[]::text[], true),
      ('RetentionDecisionSets', ARRAY[]::text[], false),
      ('RetentionDecisionRecords', ARRAY[]::text[], false),
      ('MembershipPermissionGrants', ARRAY['RevokedByMembershipId','RevokedBySubjectId',
        'RevokedAtUtc','ConcurrencyVersion']::text[], false)
    ) AS inventory(name, update_columns, reconcile_owner)
  LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', lifecycle_object.name)) IS NOT NULL THEN
      IF lifecycle_object.reconcile_owner THEN
        EXECUTE pg_catalog.format('ALTER TABLE public.%I OWNER TO zeka_auth_owner', lifecycle_object.name);
      END IF;
      FOR table_grant IN
        SELECT DISTINCT acl.grantee, pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
        FROM pg_catalog.pg_class relation
        CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
        WHERE relation.oid=pg_catalog.to_regclass(pg_catalog.format('public.%I', lifecycle_object.name))
          AND acl.grantee<>relation.relowner
      LOOP
        EXECUTE pg_catalog.format(
          'REVOKE ALL ON TABLE public.%I FROM %s CASCADE',
          lifecycle_object.name,
          CASE WHEN table_grant.grantee=0 THEN public_grantee
            ELSE pg_catalog.quote_ident(table_grant.grantee_name) END);
      END LOOP;
      FOR column_grant IN
        SELECT DISTINCT attribute.attname, acl.grantee,
          pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
        FROM pg_catalog.pg_attribute attribute
        JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
        CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
        WHERE relation.oid=pg_catalog.to_regclass(pg_catalog.format('public.%I', lifecycle_object.name))
          AND attribute.attnum>0 AND NOT attribute.attisdropped
          AND acl.grantee<>relation.relowner
      LOOP
        EXECUTE pg_catalog.format(
          'REVOKE ALL (%I) ON TABLE public.%I FROM %s CASCADE',
          column_grant.attname, lifecycle_object.name,
          CASE WHEN column_grant.grantee=0 THEN public_grantee
            ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
      END LOOP;
      EXECUTE pg_catalog.format(
        'GRANT SELECT, INSERT ON TABLE public.%I TO zeka_auth_runtime', lifecycle_object.name);
      IF pg_catalog.cardinality(lifecycle_object.update_columns) > 0 THEN
        SELECT pg_catalog.string_agg(pg_catalog.quote_ident(column_name), ', ')
          INTO update_list FROM pg_catalog.unnest(lifecycle_object.update_columns) column_name;
        EXECUTE pg_catalog.format(
          'GRANT UPDATE (%s) ON TABLE public.%I TO zeka_auth_runtime',
          update_list, lifecycle_object.name);
      END IF;
    END IF;
  END LOOP;
  -- LIFE-04A/05A evidence tables are present in production schema but unavailable to the
  -- ordinary runtime. Only isolated conformance setup may grant a fixture identity.
  FOREACH object_name IN ARRAY ARRAY[
    'LifecyclePurgePlans','LifecyclePurgeOutbox','LifecyclePurgeProgress',
    'LifecycleVerificationCommands','LifecycleVerificationEvidence'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('ALTER TABLE public.%I OWNER TO zeka_auth_owner', object_name);
      FOR table_grant IN
        SELECT DISTINCT acl.grantee, pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
        FROM pg_catalog.pg_class relation
        CROSS JOIN LATERAL pg_catalog.aclexplode(relation.relacl) acl
        WHERE relation.oid=pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name))
          AND acl.grantee<>relation.relowner
      LOOP
        EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.%I FROM %s CASCADE',
          object_name, CASE WHEN table_grant.grantee=0 THEN public_grantee
            ELSE pg_catalog.quote_ident(table_grant.grantee_name) END);
      END LOOP;
      FOR column_grant IN
        SELECT DISTINCT attribute.attname, acl.grantee,
          pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
        FROM pg_catalog.pg_attribute attribute
        JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
        CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
        WHERE relation.oid=pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name))
          AND attribute.attnum>0 AND NOT attribute.attisdropped
          AND acl.grantee<>relation.relowner
      LOOP
        EXECUTE pg_catalog.format('REVOKE ALL (%I) ON TABLE public.%I FROM %s CASCADE',
          column_grant.attname, object_name,
          CASE WHEN column_grant.grantee=0 THEN public_grantee
            ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
      END LOOP;
    END IF;
  END LOOP;
  IF pg_catalog.to_regprocedure('zeka.current_organisation_id()') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_auth_runtime;
  END IF;
  FOREACH function_name IN ARRAY ARRAY[
    'reject_auth_membership_write_during_export_fence',
    'reject_auth_owned_write_during_closure_fence',
    'reject_auth_organisation_write_during_closure_fence'
  ] LOOP
    IF pg_catalog.to_regprocedure(pg_catalog.format('zeka.%I()', function_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format(
        'ALTER FUNCTION zeka.%I() OWNER TO zeka_auth_owner', function_name);
      FOR function_grant IN
        SELECT DISTINCT acl.grantee, pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
        FROM pg_catalog.pg_proc procedure
        CROSS JOIN LATERAL pg_catalog.aclexplode(procedure.proacl) acl
        WHERE procedure.oid=pg_catalog.to_regprocedure(pg_catalog.format('zeka.%I()', function_name))
          AND acl.grantee<>procedure.proowner
      LOOP
        EXECUTE pg_catalog.format(
          'REVOKE ALL ON FUNCTION zeka.%I() FROM %s CASCADE',
          function_name,
          CASE WHEN function_grant.grantee=0 THEN public_grantee
            ELSE pg_catalog.quote_ident(function_grant.grantee_name) END);
      END LOOP;
      EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION zeka.%I() TO zeka_auth_runtime', function_name);
    END IF;
  END LOOP;
  IF pg_catalog.to_regprocedure(
       'zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamp with time zone)') IS NOT NULL THEN
    ALTER FUNCTION zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz)
      OWNER TO zeka_auth_owner;
    REVOKE ALL ON FUNCTION zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz)
      FROM PUBLIC, zeka_auth_runtime, zeka_auth_closure_recovery;
    GRANT EXECUTE ON FUNCTION zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamptz)
      TO zeka_auth_closure_recovery;
  END IF;
  IF pg_catalog.to_regclass('public."AuthClosureParticipantExecutions"') IS NOT NULL THEN
    ALTER POLICY rls_authclosureparticipantexecutions_organisation
      ON public."AuthClosureParticipantExecutions" TO zeka_auth_runtime, zeka_auth_owner;
    ALTER POLICY rls_authclosureparticipantoutbox_organisation
      ON public."AuthClosureParticipantOutbox" TO zeka_auth_runtime, zeka_auth_owner;
  END IF;
END $lifecycle_objects$;
