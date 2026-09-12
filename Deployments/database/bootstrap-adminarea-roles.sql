-- Run once per AdminArea database using a separately authorized PostgreSQL role administrator.
-- Credentials are supplied by the host and never belong in this file.
DO $bootstrap$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_adminarea_owner') THEN
    CREATE ROLE zeka_adminarea_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_adminarea_migrator') THEN
    CREATE ROLE zeka_adminarea_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeka_adminarea_runtime') THEN
    CREATE ROLE zeka_adminarea_runtime LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
  END IF;
END $bootstrap$;
ALTER ROLE zeka_adminarea_owner NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_adminarea_owner RESET ALL;
ALTER ROLE zeka_adminarea_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_adminarea_migrator RESET ALL;
ALTER ROLE zeka_adminarea_runtime LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION;
ALTER ROLE zeka_adminarea_runtime RESET ALL;
DO $database$ BEGIN
  EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO zeka_adminarea_migrator, zeka_adminarea_runtime', current_database());
END $database$;
GRANT zeka_adminarea_owner TO zeka_adminarea_migrator WITH INHERIT FALSE, SET TRUE, ADMIN FALSE;
DO $memberships$
DECLARE membership record;
BEGIN
  FOR membership IN
    SELECT parent.rolname AS parent_name, member.rolname AS member_name
    FROM pg_auth_members m
    JOIN pg_roles parent ON parent.oid=m.roleid
    JOIN pg_roles member ON member.oid=m.member
    WHERE (parent.rolname IN ('zeka_adminarea_owner','zeka_adminarea_migrator','zeka_adminarea_runtime')
       OR member.rolname IN ('zeka_adminarea_owner','zeka_adminarea_migrator','zeka_adminarea_runtime'))
      AND NOT (parent.rolname='zeka_adminarea_owner' AND member.rolname='zeka_adminarea_migrator')
  LOOP EXECUTE format('REVOKE %I FROM %I', membership.parent_name, membership.member_name); END LOOP;
END $memberships$;
ALTER SCHEMA public OWNER TO zeka_adminarea_owner;
CREATE SCHEMA IF NOT EXISTS zeka AUTHORIZATION zeka_adminarea_owner;
ALTER SCHEMA zeka OWNER TO zeka_adminarea_owner;
REVOKE ALL ON SCHEMA zeka FROM PUBLIC, zeka_adminarea_runtime;
GRANT USAGE ON SCHEMA zeka TO zeka_adminarea_runtime;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO zeka_adminarea_migrator, zeka_adminarea_runtime;
DO $ownership$
DECLARE object record;
BEGIN
  FOR object IN SELECT format('%I.%I', n.nspname, c.relname) name
    FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
    WHERE n.nspname='public' AND c.relkind IN ('r','p')
  LOOP
    EXECUTE 'ALTER TABLE ' || object.name || ' OWNER TO zeka_adminarea_owner';
  END LOOP;
END $ownership$;
DO $functions$
DECLARE object record;
BEGIN
  FOR object IN SELECT n.nspname, p.proname, pg_get_function_identity_arguments(p.oid) arguments
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public'
  LOOP EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO zeka_adminarea_owner', object.nspname, object.proname, object.arguments); END LOOP;
END $functions$;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC, zeka_adminarea_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM PUBLIC, zeka_adminarea_runtime;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO zeka_adminarea_runtime;
