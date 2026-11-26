# Build only from Prepare-OwnerHelperBuildContext.ps1 output; reviewed runtime inventory and effective context-local ignore policy.
FROM mcr.microsoft.com/dotnet/aspnet:8.0.29
WORKDIR /app
ARG SOURCE_COMMIT
ARG BINARY_SHA256
COPY [ \
    "AuthManager.Application.dll", \
    "AuthManager.Core.dll", \
    "AuthManager.Infrastructure.dll", \
    "FluentValidation.DependencyInjectionExtensions.dll", \
    "FluentValidation.dll", \
    "Microsoft.AspNetCore.Authentication.JwtBearer.dll", \
    "Microsoft.AspNetCore.Cryptography.Internal.dll", \
    "Microsoft.AspNetCore.Cryptography.KeyDerivation.dll", \
    "Microsoft.AspNetCore.Identity.EntityFrameworkCore.dll", \
    "Microsoft.EntityFrameworkCore.Abstractions.dll", \
    "Microsoft.EntityFrameworkCore.dll", \
    "Microsoft.EntityFrameworkCore.Relational.dll", \
    "Microsoft.Extensions.Identity.Core.dll", \
    "Microsoft.Extensions.Identity.Stores.dll", \
    "Microsoft.Extensions.Options.dll", \
    "Microsoft.IdentityModel.Abstractions.dll", \
    "Microsoft.IdentityModel.JsonWebTokens.dll", \
    "Microsoft.IdentityModel.Logging.dll", \
    "Microsoft.IdentityModel.Protocols.dll", \
    "Microsoft.IdentityModel.Protocols.OpenIdConnect.dll", \
    "Microsoft.IdentityModel.Tokens.dll", \
    "NodaTime.dll", \
    "Npgsql.dll", \
    "Npgsql.EntityFrameworkCore.PostgreSQL.dll", \
    "System.IdentityModel.Tokens.Jwt.dll", \
    "zeka-auth-owner-provision.deps.json", \
    "zeka-auth-owner-provision.dll", \
    "zeka-auth-owner-provision.runtimeconfig.json", \
    "Zeka.Authentication.dll", \
    "Zeka.Extensions.MultiTenancy.Abstractions.dll", \
    "Zeka.Lifecycle.Contracts.dll", \
    "/app/" ]
RUN test ${#SOURCE_COMMIT} -eq 40 && test ${#BINARY_SHA256} -eq 64 \
    && printf '%s  %s\n' "$BINARY_SHA256" /app/zeka-auth-owner-provision.dll | sha256sum -c -
LABEL org.opencontainers.image.revision="${SOURCE_COMMIT}" \
      com.diliwo.zeka.binary-sha256="${BINARY_SHA256}" \
      com.diliwo.zeka.scope="m1-owner-provision" \
      com.diliwo.zeka.owner="windows-codex-m1" \
      com.docker.compose.project="zeka-m1"
USER app
ENTRYPOINT ["dotnet", "/app/zeka-auth-owner-provision.dll"]
