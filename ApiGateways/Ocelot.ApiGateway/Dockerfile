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
COPY ["ApiGateways/Ocelot.ApiGateway/Ocelot.ApiGateway.csproj","ApiGateways/Ocelot.ApiGateway/packages.lock.json","ApiGateways/Ocelot.ApiGateway/"]
RUN --mount=type=secret,id=github_token,required=true \
    PACKAGE_USERNAME=diliwo PACKAGE_READ_TOKEN="$(cat /run/secrets/github_token)" \
    bash scripts/ci/strict-restore.sh docker gateway "ApiGateways/Ocelot.ApiGateway/Ocelot.ApiGateway.csproj" "$STRICT_RESTORE_HEAD" /strict-restore-receipt.json

FROM scratch AS strict-restore-proof
COPY --from=restore /strict-restore-receipt.json /strict-restore-receipt.json

FROM restore AS build
ARG BUILD_CONFIGURATION=Release
COPY . .
WORKDIR "/src/ApiGateways/Ocelot.ApiGateway"
RUN test "$BUILD_CONFIGURATION" = Release \
    && dotnet build "./Ocelot.ApiGateway.csproj" -c "$BUILD_CONFIGURATION" -o /app/build -p:UseAppHost=false --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Ocelot.ApiGateway.csproj" -c "$BUILD_CONFIGURATION" -o /app/publish -p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Ocelot.ApiGateway.dll"]
