CREATE TABLE "EmailVerificationChallenges" (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Generation" integer NOT NULL,
    "KeyId" character varying(32) NOT NULL,
    "AddressBinding" character varying(64) NOT NULL,
    "StampBinding" character varying(64) NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "Status" character varying(16) NOT NULL,
    "TransitionedAtUtc" timestamp with time zone,
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_EmailVerificationChallenges" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_EmailVerificationChallenges_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "OnboardingGrants" (
    "Id" uuid NOT NULL,
    "ChallengeId" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "Purpose" character varying(32) NOT NULL,
    "CredentialHash" character varying(64) NOT NULL,
    "StampBinding" character varying(64) NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "ConsumedAtUtc" timestamp with time zone,
    "RevokedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_OnboardingGrants" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OnboardingGrants_EmailVerificationChallenges_ChallengeId" FOREIGN KEY ("ChallengeId") REFERENCES "EmailVerificationChallenges" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "OnboardingOperationReceipts" (
    "Id" uuid NOT NULL,
    "Kind" character varying(16) NOT NULL,
    "UserId" uuid NOT NULL,
    "ChallengeId" uuid NOT NULL,
    "Generation" integer NOT NULL,
    "OutboxId" uuid,
    "GrantId" uuid,
    CONSTRAINT "PK_OnboardingOperationReceipts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OnboardingOperationReceipts_EmailVerificationChallenges_Cha~" FOREIGN KEY ("ChallengeId") REFERENCES "EmailVerificationChallenges" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "VerificationMailOutboxMessages" (
    "Id" uuid NOT NULL,
    "OperationId" uuid NOT NULL,
    "ChallengeId" uuid NOT NULL,
    "Generation" integer NOT NULL,
    "PayloadVersion" integer NOT NULL,
    "EnqueuedAtUtc" timestamp with time zone NOT NULL,
    "Status" character varying(16) NOT NULL,
    "LeaseId" uuid,
    "LeaseExpiresAtUtc" timestamp with time zone,
    "Attempts" integer NOT NULL,
    "NextAttemptAtUtc" timestamp with time zone NOT NULL,
    "DeliveredAtUtc" timestamp with time zone,
    "ObsoleteAtAcceptance" boolean NOT NULL,
    "Outcome" character varying(32),
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_VerificationMailOutboxMessages" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_VerificationMailOutboxMessages_EmailVerificationChallenges_~" FOREIGN KEY ("ChallengeId") REFERENCES "EmailVerificationChallenges" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_EmailVerificationChallenges_AddressBinding_CreatedAtUtc" ON "EmailVerificationChallenges" ("AddressBinding", "CreatedAtUtc");

CREATE UNIQUE INDEX "IX_EmailVerificationChallenges_UserId" ON "EmailVerificationChallenges" ("UserId") WHERE "Status" = 'Pending';

CREATE UNIQUE INDEX "IX_EmailVerificationChallenges_UserId_Generation" ON "EmailVerificationChallenges" ("UserId", "Generation");

CREATE UNIQUE INDEX "IX_OnboardingGrants_ChallengeId" ON "OnboardingGrants" ("ChallengeId");

CREATE UNIQUE INDEX "IX_OnboardingGrants_CredentialHash" ON "OnboardingGrants" ("CredentialHash");

CREATE INDEX "IX_OnboardingOperationReceipts_ChallengeId" ON "OnboardingOperationReceipts" ("ChallengeId");

CREATE UNIQUE INDEX "IX_VerificationMailOutboxMessages_ChallengeId_Generation" ON "VerificationMailOutboxMessages" ("ChallengeId", "Generation");

CREATE UNIQUE INDEX "IX_VerificationMailOutboxMessages_OperationId" ON "VerificationMailOutboxMessages" ("OperationId");

CREATE INDEX "IX_VerificationMailOutboxMessages_Status_NextAttemptAtUtc" ON "VerificationMailOutboxMessages" ("Status", "NextAttemptAtUtc");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261115195218_OnboardingRegistration', '8.0.8');

