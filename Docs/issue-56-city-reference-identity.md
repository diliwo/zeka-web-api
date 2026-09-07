# Issue #56 — City reference identity and validation

## Binding policy and authority

The user confirmed this implementation policy on 2026-09-07, superseding issue #56's proposed organisation-scoped key and its organisation-specific acceptance wording. City is **shared reference data**. This preserves the classification in [the issue #43 inventory](adr-003-tenant-persistence-migration.md). No OrganisationId, tenant filter, tenant index, RLS change, or reclassification is introduced.

Accepted Ubongo ADR-001, ADR-002 and ADR-003 remain the architectural constraints: the service owns the rule; Domain owns text invariants; Application uses semantic ports; Infrastructure owns PostgreSQL enforcement. A future organisation-owned City model needs its own architectural decision and migration issue. Neither GitHub issue text nor Ubongo ADRs were modified.

## Text and identity

- The active natural key is global: (NormalizedName, NormalizedCountry).
- Stored Name and Country are NFC-normalized and outer-trimmed before persistence. Display casing, accents and internal whitespace are preserved. Domain constructors and setters return the canonical value; PostgreSQL rejects noncanonical raw inserts and updates.
- Both fields must contain 1–100 Unicode scalar values after NFC normalization and trimming. This is not a UTF-16 code-unit limit or a grapheme limit. For example, a supplementary-plane character counts once, and decomposed e + acute becomes one scalar after NFC.
- Empty/whitespace-only values, malformed UTF-16 and U+0000 are rejected. City construction and setters use the Domain policy; Application returns field-specific validation errors. The prior City whitespace-acceptance test now checks rejection of an empty normalized value under this approved policy. School behavior is unchanged.
- Trimming uses the Unicode White_Space set shared by .NET Trim and the explicit PostgreSQL trim function: U+0009–000D, 0020, 0085, 00A0, 1680, 2000–200A, 2028, 2029, 202F, 205F, 3000. Internal whitespace is never collapsed.
- PostgreSQL computes each identity key as NFC(root-ICU uppercase(root-ICU lowercase(NFC-and-trim(display)))). Lowercasing first handles uppercase sharp S as well as its lowercase/expanded variants. The root locale is explicitly und-x-icu, independent of the database/session locale. Key columns use deterministic byte comparison under collation C. Accent marks remain significant. Casing expansion is permitted in keys; the 100-scalar limit applies before casing.
- For example, composed/decomposed LIÈGE compare equal, but LIEGE remains distinct. Straße/STRASSE and Greek sigma case variants compare equal. Different countries and different internal spacing remain distinct.

PostgreSQL's [Unicode normalization and string functions](https://www.postgresql.org/docs/17/functions-string.html) and [root ICU/deterministic collation behavior](https://www.postgresql.org/docs/17/collation.html) underpin this implementation.

## Application and persistence boundaries

ICityQueries belongs to Application. ActiveCityExistsAsync accepts the complete display pair and a CancellationToken and returns a boolean; it has no organisation parameter. Invalid fields do not query persistence. Infrastructure calls the same PostgreSQL key function used by the generated columns, avoiding a separate .NET casing algorithm.

The legacy ICityRepository.GetCities IQueryable member is removed. The City list handler receives a materialized page through ICityQueries.GetPageAsync. Filtering, sorting, projection, counting and paging stay in Infrastructure. No provider query is handed to an Application sorting/paging helper. Existing City HTTP DTOs and list response shape are unchanged.

The delete command's existing restore behavior checks the active key before restoring. A duplicate produces the existing Application ValidationException and does not persist. The partial unique index remains authoritative when a concurrent insert or restoration wins after the check. Database uniqueness violations still use the existing infrastructure exception path; this issue does not redesign repository-wide HTTP conflict translation.

Other legacy repositories, IRepositoryManager placement, shared pagination helpers and unrelated EF dependencies are outside this affected boundary and unchanged.

## PostgreSQL enforcement and migration

Migration: 20260907185334_CityReferenceIdentity, after 20260904124303_OrganisationTenantConstraints.

The migration installs immutable, strict SQL normalization functions, preflights legacy data, then canonicalizes Name and Country transactionally before narrowing both to varchar(100). PostgreSQL counts Unicode scalars for that limit. Both checks require canonical, nonempty stored text using deterministic C comparison. Stored generated NormalizedName/NormalizedCountry columns and UX_Cities_ActiveNormalizedIdentity with predicate NOT "Softdelete" enforce global active uniqueness. Keys cannot be supplied or forged by ordinary INSERT/UPDATE statements. Enforcement covers raw SQL, updates, concurrent inserts and restorations.

Soft-deleted rows do not reserve the active key. A replacement may be created, but restoring an older row while a replacement remains active is rejected. Text validity applies to deleted rows too.

Rollout:

1. Use PostgreSQL 17 with UTF8 encoding and root ICU collation und-x-icu available; retain the tested PostgreSQL/ICU versions in deployment evidence.
2. Back up the database and complete any prerequisites of the preceding issue #43 migration.
3. Schedule a maintenance window. The migration locks Cities against concurrent writers while checking, canonicalizing and installing the bounded columns, derived keys and index.
4. Both transactional preflights run before rewriting any data. Invalid normalized text or duplicate active normalized pairs abort without changing the original text. Review and approve collision/invalid-data remediation separately, then retry. Diagnostics do not include raw city values. Successful preflight permits only NFC normalization and outer trimming, including on deleted rows; it never merges, truncates, deletes or silently soft-deletes records.
5. Apply the migration before deploying the new query adapter. Verify both varchar(100) display columns, the two generated columns, two canonical-text checks and active unique index, then run the PostgreSQL evidence tests.
6. Reopen writes only after these checks. Legacy/raw writers must supply canonical text; noncanonical or oversized values are rejected by the database.

Rollback drops the derived columns, index, checks and functions and widens Name/Country back to text. It preserves all rows and their canonical values; it cannot reconstruct removed outer whitespace or the original decomposed representation. Restore the pre-migration backup if exact original representation is required. Coordinate rollback with the application version, which otherwise still queries those columns/functions. Rollback past the preceding tenant migration remains governed by that migration's existing restrictions.

Unicode/ICU upgrades require an explicit collision audit and recomputation of stored keys under the new casing rules, followed by index validation. Reindexing alone does not recompute stored generated values. Do not silently change the key function semantics on a live database.

Migration scaffolding exposed pre-existing timestamp-model drift (with/without time zone). This migration and snapshot deliberately preserve the previous timestamp schema; no timestamp conversion or unrelated table alteration is included.

## Evidence

- AdminArea Domain: 60 passed.
- AdminArea Application unit tests: 35 passed, including all four original CreateCity failures.
- AdminArea Application integration tests: 4 passed.
- New AdminArea Infrastructure integration suite: 28 passed on disposable PostgreSQL 17 databases. Includes canonical storage on construction/mutation/migration, rejection of noncanonical raw inserts/updates, long legacy text canonicalized before narrowing, unchanged original text after failed preflight, legacy seed preservation, text/case/accent/whitespace semantics, scalar lengths, generated-key forgery rejection, soft deletion, competing inserts/restorations, cancellation, listing behavior, composition, migration retry and rollback.
- Full solution build: succeeded, zero errors; existing warnings remain.
- Full solution tests: 177 passed, 1 failed, 0 skipped. The remaining failure is the pre-existing ClientManagement Domain test Contructor_FormatNiss_ThrowException, tracked by issue #57 and outside issue #56. No assertion was suppressed or test excluded.
- Migration tests use raw PostgreSQL writes in addition to EF queries, so enforcement is not inferred from an in-memory provider or only from Application validation.

No production database was migrated. Production data remediation, rollout and Unicode/ICU upgrade operations remain deployment responsibilities, not actions performed by this implementation.

## Review correction — 2026-09-07

The review of commit 23bc32a identified that retaining outer whitespace and decomposed display text contradicted canonical storage. Domain, mapping, the unapplied migration, tests and this document now require canonical storage. The semantic Application port and shared-data classification are unchanged. Generated migration/project line endings are normalized to LF, consistent with the repository's whitespace check and core.autocrlf=false. Validate the complete branch plus working tree with git diff --check origin/develop, not only the uncommitted diff.
