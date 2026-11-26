-- Exact Auth onboarding runtime grants for 20261115195218_OnboardingRegistration.
-- Source contract: Deployments/database/bootstrap-auth-roles.sql, onboarding_registration_runtime.
-- Execute only through the reviewed transactional Auth onboarding executor.

GRANT SELECT, INSERT ON TABLE public."EmailVerificationChallenges" TO "zeka_auth_runtime";
GRANT SELECT, INSERT ON TABLE public."OnboardingGrants" TO "zeka_auth_runtime";
GRANT SELECT, INSERT ON TABLE public."OnboardingOperationReceipts" TO "zeka_auth_runtime";
GRANT SELECT, INSERT ON TABLE public."VerificationMailOutboxMessages" TO "zeka_auth_runtime";

GRANT UPDATE ("Status", "TransitionedAtUtc", "Version")
    ON TABLE public."EmailVerificationChallenges" TO "zeka_auth_runtime";
GRANT UPDATE ("Status", "LeaseId", "LeaseExpiresAtUtc", "Attempts", "NextAttemptAtUtc",
              "DeliveredAtUtc", "ObsoleteAtAcceptance", "Outcome", "Version")
    ON TABLE public."VerificationMailOutboxMessages" TO "zeka_auth_runtime";
