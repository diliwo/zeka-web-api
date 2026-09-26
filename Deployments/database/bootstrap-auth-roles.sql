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
    GRANT SELECT, INSERT, UPDATE ON TABLE public."OrganisationLifecycleOperations" TO zeka_auth_runtime;
  END IF;
  IF pg_catalog.to_regclass('public."OrganisationLifecycleParticipants"') IS NOT NULL THEN
    GRANT SELECT, INSERT ON TABLE public."OrganisationLifecycleParticipants" TO zeka_auth_runtime;
  END IF;
  IF pg_catalog.to_regprocedure('zeka.current_organisation_id()') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_auth_runtime;
  END IF;
END $lifecycle_objects$;
