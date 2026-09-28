-- Run once per AdminArea database using a separately authorized PostgreSQL role administrator.
-- Credentials are supplied by the host and never belong in this file.
DO $bootstrap$
DECLARE role_spec record;
        database_setting record;
BEGIN
  FOR role_spec IN
    SELECT * FROM (VALUES
      ('zeka_adminarea_owner', 'NOLOGIN'),
      ('zeka_adminarea_migrator', 'LOGIN'),
      ('zeka_adminarea_runtime', 'LOGIN'),
      ('zeka_adminarea_closure_recovery', 'NOLOGIN')
    ) AS managed_roles(name, login_mode)
  LOOP
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname=role_spec.name) THEN
      EXECUTE pg_catalog.format(
        'CREATE ROLE %I %s NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT',
        role_spec.name, role_spec.login_mode);
    END IF;
    EXECUTE pg_catalog.format(
      'ALTER ROLE %I %s NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION',
      role_spec.name, role_spec.login_mode);
    IF role_spec.name='zeka_adminarea_closure_recovery' THEN
      ALTER ROLE zeka_adminarea_closure_recovery PASSWORD NULL;
    END IF;
    EXECUTE pg_catalog.format('ALTER ROLE %I RESET ALL', role_spec.name);
  END LOOP;
  FOR database_setting IN
    SELECT role.rolname, database.datname
    FROM pg_catalog.pg_db_role_setting role_setting
    JOIN pg_catalog.pg_roles role ON role.oid=role_setting.setrole
    JOIN pg_catalog.pg_database database ON database.oid=role_setting.setdatabase
    WHERE role.rolname=ANY(ARRAY[
      'zeka_adminarea_owner', 'zeka_adminarea_migrator', 'zeka_adminarea_runtime',
      'zeka_adminarea_closure_recovery'
    ])
  LOOP
    EXECUTE pg_catalog.format(
      'ALTER ROLE %I IN DATABASE %I RESET ALL', database_setting.rolname, database_setting.datname);
  END LOOP;
END $bootstrap$;
DO $database$ BEGIN
  EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
  EXECUTE format(
    'REVOKE ALL ON DATABASE %I FROM zeka_adminarea_owner, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery',
    current_database());
  EXECUTE format(
    'GRANT CONNECT ON DATABASE %I TO zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery',
    current_database());
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
    WHERE (parent.rolname IN (
             'zeka_adminarea_owner','zeka_adminarea_migrator','zeka_adminarea_runtime',
             'zeka_adminarea_closure_recovery')
       OR member.rolname IN (
             'zeka_adminarea_owner','zeka_adminarea_migrator','zeka_adminarea_runtime',
             'zeka_adminarea_closure_recovery'))
      AND NOT (parent.rolname='zeka_adminarea_owner' AND member.rolname='zeka_adminarea_migrator')
  LOOP EXECUTE format('REVOKE %I FROM %I', membership.parent_name, membership.member_name); END LOOP;
END $memberships$;
ALTER SCHEMA public OWNER TO zeka_adminarea_owner;
CREATE SCHEMA IF NOT EXISTS zeka AUTHORIZATION zeka_adminarea_owner;
ALTER SCHEMA zeka OWNER TO zeka_adminarea_owner;
REVOKE ALL ON SCHEMA zeka
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
GRANT USAGE ON SCHEMA zeka TO zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
REVOKE ALL ON SCHEMA public
  FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
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
    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
    WHERE n.nspname='public'
       OR p.oid=pg_catalog.to_regprocedure('zeka.reject_adminarea_write_during_export()')
       OR p.oid=pg_catalog.to_regprocedure('zeka.reject_adminarea_write_during_closure()')
       OR p.oid=pg_catalog.to_regprocedure('zeka.release_adminarea_closure_fence(uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)')
  LOOP EXECUTE format('ALTER FUNCTION %I.%I(%s) OWNER TO zeka_adminarea_owner', object.nspname, object.proname, object.arguments); END LOOP;
END $functions$;
REVOKE ALL ON ALL TABLES IN SCHEMA public
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
REVOKE ALL ON ALL TABLES IN SCHEMA zeka
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA zeka
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA zeka
  FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON TABLES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON FUNCTIONS FROM zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON TYPES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE ALL ON SCHEMAS FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE ALL ON TYPES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON TABLES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON SEQUENCES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON FUNCTIONS FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE ALL ON TYPES FROM PUBLIC, zeka_adminarea_migrator, zeka_adminarea_runtime, zeka_adminarea_closure_recovery;
DO $existing_objects$
DECLARE object_name text;
        lifecycle_object record;
        column_grant record;
        update_list text;
BEGIN
  FOR lifecycle_object IN
    SELECT * FROM (VALUES
      ('AdminAreaExportFences', ARRAY['ReleasedAt']::text[]),
      ('AdminAreaExportFragments', ARRAY[]::text[]),
      ('AdminAreaExportInbox', ARRAY[]::text[]),
      ('AdminAreaExportOutbox', ARRAY[]::text[]),
      ('AdminAreaClosureFences', ARRAY[]::text[]),
      ('AdminAreaClosureInbox', ARRAY[]::text[]),
      ('AdminAreaClosureOutbox', ARRAY[]::text[])
    ) AS inventory(name, update_columns)
  LOOP
    IF pg_catalog.to_regclass(pg_catalog.format('public.%I', lifecycle_object.name)) IS NOT NULL THEN
      EXECUTE pg_catalog.format(
        'REVOKE ALL ON TABLE public.%I FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery', lifecycle_object.name);
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
          CASE WHEN column_grant.grantee=0 THEN 'PUBLIC'
            ELSE pg_catalog.quote_ident(column_grant.grantee_name) END);
      END LOOP;
      EXECUTE pg_catalog.format(
        'GRANT SELECT, INSERT ON TABLE public.%I TO zeka_adminarea_runtime', lifecycle_object.name);
      IF pg_catalog.cardinality(lifecycle_object.update_columns) > 0 THEN
        SELECT pg_catalog.string_agg(pg_catalog.quote_ident(column_name), ', ')
          INTO update_list FROM pg_catalog.unnest(lifecycle_object.update_columns) column_name;
        EXECUTE pg_catalog.format(
          'GRANT UPDATE (%s) ON TABLE public.%I TO zeka_adminarea_runtime',
          update_list, lifecycle_object.name);
      END IF;
    END IF;
  END LOOP;
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
  FOREACH object_name IN ARRAY ARRAY[
    'zeka.reject_adminarea_write_during_export()',
    'zeka.reject_adminarea_write_during_closure()'
  ] LOOP
    IF pg_catalog.to_regprocedure(object_name) IS NOT NULL THEN
      EXECUTE pg_catalog.format(
        'REVOKE ALL ON FUNCTION %s FROM PUBLIC, zeka_adminarea_runtime', object_name);
      EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION %s TO zeka_adminarea_runtime', object_name);
    END IF;
  END LOOP;
  object_name := 'zeka.release_adminarea_closure_fence(uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)';
  IF pg_catalog.to_regprocedure(object_name) IS NOT NULL THEN
    EXECUTE pg_catalog.format(
      'REVOKE ALL ON FUNCTION %s FROM PUBLIC, zeka_adminarea_runtime, zeka_adminarea_closure_recovery',
      object_name);
    EXECUTE pg_catalog.format(
      'GRANT EXECUTE ON FUNCTION %s TO zeka_adminarea_closure_recovery', object_name);
  END IF;
  FOREACH object_name IN ARRAY ARRAY['public.zeka_city_key(text)', 'public.zeka_city_text(text)'] LOOP
    IF pg_catalog.to_regprocedure(object_name) IS NOT NULL THEN
      EXECUTE pg_catalog.format(
        'GRANT EXECUTE ON FUNCTION %s TO zeka_adminarea_runtime', object_name);
    END IF;
  END LOOP;
END $existing_objects$;
