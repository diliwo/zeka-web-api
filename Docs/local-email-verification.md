# Local email verification

The ZEK-49 frontend is served through the existing HTTPS Gateway origin. Registration
and sign-in keep the established Origin, CSRF, HTTPS and cookie rules. A verification
link opens `/verify-email#token=…`; GET does not confirm the address. The user must
press **Confirmer mon adresse**. Confirmation gives a restricted onboarding cookie,
then the user signs in separately with their password.

On the configured owner-local workstation, start the slice with:

```powershell
pwsh -File D:\Workspace\zeka-zek49-api\scripts\local\Start-MailpitVerification.ps1
```

The script uses the prepared `D:\Workspace\Zeka49Runtime\compose.zek49.json`, starts
only the existing PostgreSQL/Auth and feature Gateway/frontend, and verifies the real
HTTPS JWKS publication required by the issuer after Auth restarts. It does not rebuild
images, provision a database, change keys or privileges, modify trust, or manage Mailpit.
Mailpit must already be running. The separately stored compose file retains the existing
restricted mounts and loopback bindings; it is local runtime configuration, not a fresh
machine installer.

Open <https://gateway.discovery-4070192a.zeka-m1.test:24446/register>, register a synthetic
address, and open its message in <http://127.0.0.1:18025>. Confirm, sign in, then check
`/account`. A new account has no organisation access. A consumed verification link is
rejected. Verification links and passwords must not be copied into logs or screenshots.

The unrelated PR #116 fresh-initialization and Gateway discovery issues remain integration
dependencies. This local slice does not accept or repair those baselines.
