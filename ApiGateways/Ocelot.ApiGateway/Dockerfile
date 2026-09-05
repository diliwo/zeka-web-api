#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY nuget.config .
COPY ["ApiGateways/Ocelot.ApiGateway/Ocelot.ApiGateway.csproj", "ApiGateways/Ocelot.ApiGateway/"]
COPY ["nuget.config", "ApiGateways/"]

RUN --mount=type=secret,id=github_token \
    dotnet nuget update source github \
      --username diliwo \
      --password "$(cat /run/secrets/github_token)" \
      --store-password-in-clear-text \
      --configfile nuget.config && \
    dotnet restore "./ApiGateways/Ocelot.ApiGateway/./Ocelot.ApiGateway.csproj" \
      --configfile nuget.config

COPY . .
WORKDIR "/src/ApiGateways/Ocelot.ApiGateway"
RUN dotnet build "./Ocelot.ApiGateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Ocelot.ApiGateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Ocelot.ApiGateway.dll"]
