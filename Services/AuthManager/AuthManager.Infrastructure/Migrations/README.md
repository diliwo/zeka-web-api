# AuthManager Identity migration strategy

## Current baseline

`MicrosoftIdentity` is the initial AuthManager Identity migration. At the time this baseline was regenerated, the AuthManager database had not been deployed and contained no production data that required preservation.

The baseline therefore creates a new ASP.NET Core Identity schema with:

- `Guid` user and role keys;
- tenant-neutral users;
- Identity password hashes, security stamps, email confirmation, and lockout fields;
- required lifecycle status and UTC timestamps;
- a database-unique normalized-email index.

The former prototype credential store and its plaintext password data are intentionally not migrated. Importing those credentials into this schema is prohibited.

## Deployment

Apply the migration only to a new or explicitly reset AuthManager database:

```bash
dotnet ef database update \
  --project Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj \
  --startup-project Services/AuthManager/AuthManager.API/AuthManager.API.csproj
```

Before the first shared or production deployment, generate and review the SQL script:

```bash
dotnet ef migrations script 0 MicrosoftIdentity \
  --idempotent \
  --project Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj \
  --startup-project Services/AuthManager/AuthManager.API/AuthManager.API.csproj
```

## Rollback

Because this is an undeployed initial baseline, rollback means dropping the Identity schema by reverting to migration `0` or recreating the disposable database:

```bash
dotnet ef database update 0 \
  --project Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj \
  --startup-project Services/AuthManager/AuthManager.API/AuthManager.API.csproj
```

Once any persistent environment applies this migration, never delete, rename, or regenerate it. All subsequent schema changes must use additive migrations with an explicit data transition and rollback assessment.
