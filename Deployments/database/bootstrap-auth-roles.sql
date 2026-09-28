-- AuthManagement registry tables are explicit Issue #45 RLS exclusions; role separation still applies.
-- LIFE-FND-01 lifecycle grants are reconciled only when the separately migrated objects exist.
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
END $bootstrap$;
ALTER ROLE zeka_auth_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_owner RESET ALL;
ALTER ROLE zeka_auth_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_migrator RESET ALL;
ALTER ROLE zeka_auth_runtime LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_auth_runtime RESET ALL;
DO $settings$
DECLARE setting record;
BEGIN
  FOR setting IN
    SELECT role.rolname, database.datname
    FROM pg_catalog.pg_db_role_setting role_setting
    JOIN pg_catalog.pg_roles role ON role.oid=role_setting.setrole
    JOIN pg_catalog.pg_database database ON database.oid=role_setting.setdatabase
    WHERE role.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime')
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
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO zeka_auth_migrator, zeka_auth_runtime', current_database());
END $database$;
GRANT zeka_auth_owner TO zeka_auth_migrator WITH INHERIT FALSE, SET TRUE, ADMIN FALSE;
DO $memberships$
DECLARE membership record;
BEGIN
  FOR membership IN
    SELECT parent.rolname AS parent_name, member.rolname AS member_name
    FROM pg_auth_members m JOIN pg_roles parent ON parent.oid=m.roleid JOIN pg_roles member ON member.oid=m.member
    WHERE (parent.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime')
       OR member.rolname IN ('zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime'))
      AND NOT (parent.rolname='zeka_auth_owner' AND member.rolname='zeka_auth_migrator')
  LOOP EXECUTE format('REVOKE %I FROM %I', membership.parent_name, membership.member_name); END LOOP;
END $memberships$;
ALTER SCHEMA public OWNER TO zeka_auth_owner;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO zeka_auth_migrator, zeka_auth_runtime;
REVOKE ALL ON SCHEMA zeka FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
GRANT USAGE ON SCHEMA zeka TO zeka_auth_runtime;
DO $ownership$
DECLARE object record;
BEGIN
  FOR object IN SELECT format('%I.%I', n.nspname, c.relname) name
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname IN ('public','zeka') AND c.relkind IN ('r','p')
  LOOP
    EXECUTE 'ALTER TABLE ' || object.name || ' OWNER TO zeka_auth_owner';
  END LOOP;
END $ownership$;
DO $functions$
DECLARE object record;
BEGIN
  FOR object IN SELECT n.nspname, p.proname, pg_get_function_identity_arguments(p.oid) arguments
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('public','zeka')
  LOOP EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO zeka_auth_owner', object.nspname, object.proname, object.arguments); END LOOP;
END $functions$;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC, zeka_auth_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC, zeka_auth_runtime;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC, zeka_auth_runtime;
DO $managed_schema_objects$
BEGIN
  IF pg_catalog.to_regnamespace('zeka') IS NOT NULL THEN
    REVOKE ALL ON ALL TABLES IN SCHEMA zeka FROM PUBLIC, zeka_auth_runtime;
    REVOKE ALL ON ALL SEQUENCES IN SCHEMA zeka FROM PUBLIC, zeka_auth_runtime;
    REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA zeka FROM PUBLIC, zeka_auth_runtime;
  END IF;
END $managed_schema_objects$;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON TABLES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON FUNCTIONS FROM zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON TYPES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner REVOKE ALL ON SCHEMAS FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA public REVOKE ALL ON TYPES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
DO $managed_schema_defaults$
BEGIN
  IF pg_catalog.to_regnamespace('zeka') IS NOT NULL THEN
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON TABLES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
    ALTER DEFAULT PRIVILEGES FOR ROLE zeka_auth_owner IN SCHEMA zeka REVOKE ALL ON TYPES FROM PUBLIC, zeka_auth_migrator, zeka_auth_runtime;
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
        column_grant record;
BEGIN
  FOREACH object_name IN ARRAY ARRAY[
    'LifecycleParticipantRegistryRevisions','LifecycleParticipantRegistryBindings',
    'LifecycleParticipantRegistryActivation'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('GRANT SELECT ON TABLE public.%I TO zeka_auth_runtime', object_name);
    END IF;
  END LOOP;
  IF pg_catalog.to_regclass('public."OrganisationLifecycleOperations"') IS NOT NULL THEN
    -- Table REVOKE does not erase pg_attribute.attacl. Remove every non-owner
    -- column grant, including stale grants on future columns, before the allowlist.
    FOR column_grant IN
      SELECT DISTINCT attribute.attname, acl.grantee,
        pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
      FROM pg_catalog.pg_attribute attribute
      JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
      CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
      WHERE relation.oid='public."OrganisationLifecycleOperations"'::regclass
        AND attribute.attnum>0 AND NOT attribute.attisdropped
        AND acl.grantee<>relation.relowner
    LOOP
      EXECUTE pg_catalog.format(
        'REVOKE ALL (%I) ON TABLE public."OrganisationLifecycleOperations" FROM %s CASCADE',
        column_grant.attname,
        CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
          ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
    END LOOP;
    REVOKE ALL ON TABLE public."OrganisationLifecycleOperations" FROM PUBLIC, zeka_auth_runtime;
    GRANT SELECT, INSERT ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
    GRANT UPDATE ("State", "Revision", "SnapshotAt", "FenceEvidenceHash", "PackageSha256",
      "PackageReference", "FailureCode", "CompletedAt", "IsActive")
      ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
  END IF;
  IF pg_catalog.to_regclass('public."OrganisationLifecycleParticipants"') IS NOT NULL THEN
    FOR column_grant IN
      SELECT DISTINCT attribute.attname, acl.grantee,
        pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
      FROM pg_catalog.pg_attribute attribute
      JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
      CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
      WHERE relation.oid='public."OrganisationLifecycleParticipants"'::regclass
        AND attribute.attnum>0 AND NOT attribute.attisdropped
        AND acl.grantee<>relation.relowner
    LOOP
      EXECUTE pg_catalog.format(
        'REVOKE ALL (%I) ON TABLE public."OrganisationLifecycleParticipants" FROM %s CASCADE',
        column_grant.attname,
        CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
          ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
    END LOOP;
    REVOKE ALL ON TABLE public."OrganisationLifecycleParticipants" FROM PUBLIC, zeka_auth_runtime;
    GRANT SELECT, INSERT ON TABLE public."OrganisationLifecycleParticipants" TO zeka_auth_runtime;
    GRANT UPDATE ("State") ON TABLE public."OrganisationLifecycleParticipants" TO zeka_auth_runtime;
  END IF;
  FOREACH object_name IN ARRAY ARRAY[
    'LifecycleCoordinatorLeases','LifecycleExportFenceReceipts','LifecycleExportFragments',
    'LifecycleExportPackages','LifecycleInboxReceipts'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.%I FROM PUBLIC, zeka_auth_runtime', object_name);
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
        EXECUTE pg_catalog.format(
          'REVOKE ALL (%I) ON TABLE public.%I FROM %s CASCADE',
          column_grant.attname, object_name,
          CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
            ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
      END LOOP;
      EXECUTE pg_catalog.format('GRANT SELECT, INSERT ON TABLE public.%I TO zeka_auth_runtime', object_name);
    END IF;
  END LOOP;
  IF pg_catalog.to_regclass('public."LifecycleCoordinatorLeases"') IS NOT NULL THEN
    GRANT UPDATE ("LeaseId", "ExpiresAt", "Version")
      ON TABLE public."LifecycleCoordinatorLeases" TO zeka_auth_runtime;
  END IF;
  IF pg_catalog.to_regclass('public."AuthExportParticipantExecutions"') IS NOT NULL THEN
    ALTER TABLE public."AuthExportParticipantExecutions" OWNER TO zeka_auth_owner;
    REVOKE ALL ON TABLE public."AuthExportParticipantExecutions" FROM PUBLIC, zeka_auth_runtime;
    FOR column_grant IN
      SELECT DISTINCT attribute.attname, acl.grantee,
        pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
      FROM pg_catalog.pg_attribute attribute
      JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
      CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
      WHERE relation.oid='public."AuthExportParticipantExecutions"'::regclass
        AND attribute.attnum>0 AND NOT attribute.attisdropped
        AND acl.grantee<>relation.relowner
    LOOP
      EXECUTE pg_catalog.format(
        'REVOKE ALL (%I) ON TABLE public."AuthExportParticipantExecutions" FROM %s CASCADE',
        column_grant.attname,
        CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
          ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
    END LOOP;
    GRANT SELECT, INSERT ON TABLE public."AuthExportParticipantExecutions" TO zeka_auth_runtime;
    GRANT UPDATE ("SnapshotAt", "FenceEvidenceHash", "FragmentHash", "CategoriesJson",
      "ReleasedAt", "State")
      ON TABLE public."AuthExportParticipantExecutions" TO zeka_auth_runtime;
  END IF;
  FOREACH object_name IN ARRAY ARRAY[
    'AuthExportParticipantInbox','AuthExportParticipantOutbox'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('ALTER TABLE public.%I OWNER TO zeka_auth_owner', object_name);
      EXECUTE pg_catalog.format('REVOKE ALL ON TABLE public.%I FROM PUBLIC, zeka_auth_runtime', object_name);
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
        EXECUTE pg_catalog.format(
          'REVOKE ALL (%I) ON TABLE public.%I FROM %s CASCADE',
          column_grant.attname, object_name,
          CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
            ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
      END LOOP;
      EXECUTE pg_catalog.format('GRANT SELECT, INSERT ON TABLE public.%I TO zeka_auth_runtime', object_name);
    END IF;
  END LOOP;
  IF pg_catalog.to_regprocedure('zeka.current_organisation_id()') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_auth_runtime;
  END IF;
  IF pg_catalog.to_regprocedure('zeka.reject_auth_membership_write_during_export_fence()') IS NOT NULL THEN
    ALTER FUNCTION zeka.reject_auth_membership_write_during_export_fence() OWNER TO zeka_auth_owner;
    REVOKE ALL ON FUNCTION zeka.reject_auth_membership_write_during_export_fence()
      FROM PUBLIC, zeka_auth_runtime;
    GRANT EXECUTE ON FUNCTION zeka.reject_auth_membership_write_during_export_fence() TO zeka_auth_runtime;
  END IF;
END $lifecycle_objects$;
DO $membership_permission_objects$
DECLARE column_grant record;
BEGIN
  IF pg_catalog.to_regclass('public."MembershipPermissionGrants"') IS NOT NULL THEN
    REVOKE ALL ON TABLE public."MembershipPermissionGrants" FROM PUBLIC, zeka_auth_runtime;
    FOR column_grant IN
      SELECT DISTINCT attribute.attname, acl.grantee,
        pg_catalog.pg_get_userbyid(acl.grantee) AS grantee_name
      FROM pg_catalog.pg_attribute attribute
      JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
      CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
      WHERE relation.oid='public."MembershipPermissionGrants"'::regclass
        AND attribute.attnum>0 AND NOT attribute.attisdropped
        AND acl.grantee<>relation.relowner
    LOOP
      EXECUTE pg_catalog.format(
        'REVOKE ALL (%I) ON TABLE public."MembershipPermissionGrants" FROM %s CASCADE',
        column_grant.attname,
        CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
          ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
    END LOOP;
    GRANT SELECT, INSERT ON TABLE public."MembershipPermissionGrants" TO zeka_auth_runtime;
    GRANT UPDATE ("RevokedByMembershipId", "RevokedBySubjectId", "RevokedAtUtc", "ConcurrencyVersion")
      ON TABLE public."MembershipPermissionGrants" TO zeka_auth_runtime;
  END IF;
END $membership_permission_objects$;
