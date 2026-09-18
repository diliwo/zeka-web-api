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
DO $settings$
DECLARE setting record;
BEGIN
  FOR setting IN
    SELECT role.rolname, database.datname
    FROM pg_catalog.pg_db_role_setting role_setting
    JOIN pg_catalog.pg_roles role ON role.oid=role_setting.setrole
    JOIN pg_catalog.pg_database database ON database.oid=role_setting.setdatabase
    WHERE role.rolname IN ('zeka_adminarea_owner','zeka_adminarea_migrator','zeka_adminarea_runtime')
  LOOP
    EXECUTE pg_catalog.format('ALTER ROLE %I IN DATABASE %I RESET ALL', setting.rolname, setting.datname);
  END LOOP;
END $settings$;
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
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON TABLES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON FUNCTIONS FROM zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON TYPES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON SCHEMAS FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON TYPES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON TABLES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON TYPES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime;
DO $existing_objects$
DECLARE object_name text;
BEGIN
  FOREACH object_name IN ARRAY ARRAY[
    'Cities','ContactPersons','DocumentPartners','Emails','Nationalities','Partners','Professions',
    'Schools','StaffMembers','StaffProjectionOutbox','Teams','TrainingFields','TrainingTypes','Trainings'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO zeka_adminarea_runtime', object_name);
    END IF;
  END LOOP;
  FOREACH object_name IN ARRAY ARRAY[
    'Cities_CityId_seq','DocumentPartners_Id_seq','Emails_Id_seq','Nationalities_NationalityId_seq',
    'Partners_Id_seq','Professions_Id_seq','Schools_Id_seq','StaffMembers_Id_seq',
    'StaffProjectionOutbox_Id_seq','Teams_Id_seq','TrainingFields_TrainingFieldId_seq',
    'TrainingTypes_TrainingTypeId_seq','Trainings_Id_seq'
  ] LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', object_name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format('GRANT SELECT, USAGE ON SEQUENCE public.%I TO zeka_adminarea_runtime', object_name);
    END IF;
  END LOOP;
  IF pg_catalog.to_regprocedure('zeka.current_organisation_id()') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_adminarea_runtime;
  END IF;
  IF pg_catalog.to_regprocedure('public.zeka_city_key(text)') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION public.zeka_city_key(text) TO zeka_adminarea_runtime;
  END IF;
  IF pg_catalog.to_regprocedure('public.zeka_city_text(text)') IS NOT NULL THEN
    GRANT EXECUTE ON FUNCTION public.zeka_city_text(text) TO zeka_adminarea_runtime;
  END IF;
END $existing_objects$;
