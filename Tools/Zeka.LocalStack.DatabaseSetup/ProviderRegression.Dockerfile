FROM mcr.microsoft.com/dotnet/aspnet:8.0.29
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_NOLOGO=1
WORKDIR /app
COPY dotnet-sdk-linux-x64.tar.gz /tmp/sdk.tar.gz
RUN tar -xzf /tmp/sdk.tar.gz -C /usr/share/dotnet && rm /tmp/sdk.tar.gz
COPY provider-tests/ /app/tests/
USER app
ENTRYPOINT ["dotnet","test","/app/tests/Zeka.DbMigrate.Tests.dll","--filter","FullyQualifiedName~Zeka.DbMigrate.Tests.LocalStackRoleSnapshotTests.Actual_snapshot_preserves_catalogue_identity_and_rejects_every_managed_role_setting_scope","--logger","trx;LogFileName=pg17-snapshot.trx","--results-directory","/run/results"]
