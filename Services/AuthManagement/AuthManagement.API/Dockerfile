#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY nuget.config .
COPY ["Services/AuthManagement/AuthManagement.API/AuthManagement.API.csproj", "Services/AuthManagement/AuthManagement.API/"]

RUN --mount=type=secret,id=github_token \
    dotnet nuget update source github \
      --username diliwo \
      --password "$(cat /run/secrets/github_token)" \
      --store-password-in-clear-text \
      --configfile nuget.config && \
    dotnet restore "./Services/AuthManagement/AuthManagement.API/./AuthManagement.API.csproj" \
      --configfile nuget.config

COPY . .
WORKDIR "/src/Services/AuthManagement/AuthManagement.API"
RUN dotnet build "./AuthManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AuthManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AuthManagement.API.dll"]