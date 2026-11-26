#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.29 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

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
COPY ["Services/ClientManagement/Client.API/ClientManagement.API.csproj","Services/ClientManagement/Client.API/packages.lock.json","Services/ClientManagement/Client.API/"]
COPY ["Services/ClientManagement/Client.Application/ClientManagement.Application.csproj","Services/ClientManagement/Client.Application/packages.lock.json","Services/ClientManagement/Client.Application/"]
COPY ["Services/ClientManagement/Client.Core/ClientManagement.Core.csproj","Services/ClientManagement/Client.Core/packages.lock.json","Services/ClientManagement/Client.Core/"]
COPY ["Services/ClientManagement/Client.Infrastructure/ClientManagement.Infrastructure.csproj","Services/ClientManagement/Client.Infrastructure/packages.lock.json","Services/ClientManagement/Client.Infrastructure/"]
COPY ["Shared/Zeka.Authentication/Zeka.Authentication.csproj","Shared/Zeka.Authentication/packages.lock.json","Shared/Zeka.Authentication/"]
COPY ["Shared/Zeka.Lifecycle.Contracts/Zeka.Lifecycle.Contracts.csproj","Shared/Zeka.Lifecycle.Contracts/packages.lock.json","Shared/Zeka.Lifecycle.Contracts/"]
RUN --mount=type=secret,id=github_token,required=true \
    PACKAGE_USERNAME=diliwo PACKAGE_READ_TOKEN="$(cat /run/secrets/github_token)" \
    bash scripts/ci/strict-restore.sh docker client "Services/ClientManagement/Client.API/ClientManagement.API.csproj" "$STRICT_RESTORE_HEAD" /strict-restore-receipt.json

FROM scratch AS strict-restore-proof
COPY --from=restore /strict-restore-receipt.json /strict-restore-receipt.json

FROM restore AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/Services/ClientManagement/Client.API"
RUN test "$BUILD_CONFIGURATION" = Release \
    && dotnet build "./ClientManagement.API.csproj" -c "$BUILD_CONFIGURATION" -o /app/build -p:UseAppHost=false --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ClientManagement.API.csproj" -c "$BUILD_CONFIGURATION" -o /app/publish -p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ClientManagement.API.dll"]
