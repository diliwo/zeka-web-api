SET LOCAL ROLE zeka_auth_owner;

LOCK TABLE public."OrganisationMemberships" IN SHARE MODE;

DO $active_owner_precheck$
BEGIN
  IF EXISTS (
    SELECT 1 FROM public."OrganisationMemberships"
    WHERE "PermissionSetId"='d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid AND "Status"=1
    GROUP BY "OrganisationId" HAVING count(*)>1)
  THEN RAISE EXCEPTION 'AUTH_ACTIVE_OWNER_CONFLICTS_PRESENT' USING ERRCODE='23505';
  END IF;
END $active_owner_precheck$;

CREATE UNIQUE INDEX "UX_OrganisationMemberships_ActiveOwner" ON "OrganisationMemberships" ("OrganisationId") WHERE "PermissionSetId" = 'D8ECEEEB-B796-4D77-92A5-6A11FAB10A01' AND "Status" = 1;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261124170000_OrganisationSingleActiveOwner', '8.0.8');
