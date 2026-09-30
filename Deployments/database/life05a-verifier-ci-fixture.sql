-- Isolated synthetic CI database. Not a production migration.
CREATE SCHEMA zeka;
CREATE FUNCTION zeka.current_organisation_id() RETURNS uuid
  LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('zeka.organisation_id', true), '')::uuid
  $$;
REVOKE ALL ON SCHEMA zeka FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION zeka.current_organisation_id() FROM PUBLIC;
CREATE SCHEMA life04a_fixture;
REVOKE ALL ON SCHEMA life04a_fixture FROM PUBLIC;
CREATE TABLE life04a_fixture."Items" (
  "OrganisationId" uuid NOT NULL,
  "ParticipantId" text NOT NULL,
  "Category" text NOT NULL,
  "Disposition" text NOT NULL
);
CREATE TABLE life04a_fixture."Payloads" (
  "OrganisationId" uuid NOT NULL,
  "ParticipantId" text NOT NULL,
  "Category" text NOT NULL,
  "SyntheticContent" text NOT NULL
);
CREATE TABLE life04a_fixture."Inbox" ("OrganisationId" uuid NOT NULL);
CREATE TABLE life04a_fixture."Starts" ("OrganisationId" uuid NOT NULL);
CREATE TABLE life04a_fixture."Auth" ("OrganisationId" uuid NOT NULL);
CREATE TABLE life04a_fixture."Progress" ("OrganisationId" uuid NOT NULL);
INSERT INTO life04a_fixture."Items"
  SELECT tenant.organisation_id::uuid, participant.participant_id,
         'synthetic', 'PURGE'
  FROM (VALUES
    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
    ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
  ) AS tenant(organisation_id)
  CROSS JOIN (VALUES
    ('auth-management'), ('admin-area'),
    ('admin-area-documents'), ('client-management')
  ) AS participant(participant_id);
INSERT INTO life04a_fixture."Payloads"
  SELECT tenant.organisation_id::uuid, participant.participant_id,
         'synthetic', 'fixture-' || participant.participant_id
  FROM (VALUES
    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
    ('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
  ) AS tenant(organisation_id)
  CROSS JOIN (VALUES
    ('auth-management'), ('admin-area'),
    ('admin-area-documents'), ('client-management')
  ) AS participant(participant_id);
