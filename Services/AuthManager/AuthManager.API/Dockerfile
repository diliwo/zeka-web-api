#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.29 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0.419@sha256:3e7b0cefe1b00a350ffb0e06b4e1271a4e791df6b8ac4239be05f1ae2736fcff AS sdk
COPY ["scripts/ci/install-strict-sdk.sh", "/tmp/install-strict-sdk.sh"]
RUN bash /tmp/install-strict-sdk.sh && rm /tmp/install-strict-sdk.sh
ENV DOTNET_ROOT=/opt/dotnet-8.0.129 \
    DOTNET_SDK_VERSION=8.0.129 \
    PATH=/opt/dotnet-8.0.129:$PATH

FROM sdk AS restore
ARG STRICT_RESTORE_HEAD=local
WORKDIR /src
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["scripts/ci/strict-restore.sh", "scripts/ci/strict-restore-projects.tsv", "scripts/ci/strict-restore-locks.sha256", "scripts/ci/nuget.strict.config", "scripts/ci/"]
COPY ["Services/AuthManager/AuthManager.API/AuthManager.API.csproj","Services/AuthManager/AuthManager.API/packages.lock.json","Services/AuthManager/AuthManager.API/"]
COPY ["Services/AuthManager/AuthManager.Application/AuthManager.Application.csproj","Services/AuthManager/AuthManager.Application/packages.lock.json","Services/AuthManager/AuthManager.Application/"]
COPY ["Services/AuthManager/AuthManager.Core/AuthManager.Core.csproj","Services/AuthManager/AuthManager.Core/packages.lock.json","Services/AuthManager/AuthManager.Core/"]
COPY ["Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj","Services/AuthManager/AuthManager.Infrastructure/packages.lock.json","Services/AuthManager/AuthManager.Infrastructure/"]
COPY ["Shared/Zeka.Authentication/Zeka.Authentication.csproj","Shared/Zeka.Authentication/packages.lock.json","Shared/Zeka.Authentication/"]
COPY ["Shared/Zeka.Lifecycle.Contracts/Zeka.Lifecycle.Contracts.csproj","Shared/Zeka.Lifecycle.Contracts/packages.lock.json","Shared/Zeka.Lifecycle.Contracts/"]
RUN --mount=type=secret,id=github_token,required=true \
    PACKAGE_USERNAME=diliwo PACKAGE_READ_TOKEN="$(cat /run/secrets/github_token)" \
    bash scripts/ci/strict-restore.sh docker auth "Services/AuthManager/AuthManager.API/AuthManager.API.csproj" "$STRICT_RESTORE_HEAD" /strict-restore-receipt.json

FROM scratch AS strict-restore-proof
COPY --from=restore /strict-restore-receipt.json /strict-restore-receipt.json

FROM restore AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/Services/AuthManager/AuthManager.API"
RUN test "$BUILD_CONFIGURATION" = Release \
    && dotnet build "./AuthManager.API.csproj" -c "$BUILD_CONFIGURATION" -p:UseAppHost=false --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AuthManager.API.csproj" -c "$BUILD_CONFIGURATION" -o /app/publish -p:UseAppHost=false --no-restore --no-build

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AuthManager.API.dll"]
