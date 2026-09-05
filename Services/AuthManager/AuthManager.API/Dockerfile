#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Services/AuthManager/AuthManager.API/AuthManager.API.csproj", "Services/AuthManager/AuthManager.API/"]
COPY ["Services/AuthManager/AuthManager.Application/AuthManager.Application.csproj", "Services/AuthManager/AuthManager.Application/"]
COPY ["Services/AuthManager/AuthManager.Core/AuthManager.Core.csproj", "Services/AuthManager/AuthManager.Core/"]
COPY ["Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj", "Services/AuthManager/AuthManager.Infrastructure/"]

RUN dotnet restore "Services/AuthManager/AuthManager.API/AuthManager.API.csproj" \
    --source https://api.nuget.org/v3/index.json

COPY . .
WORKDIR "/src/Services/AuthManager/AuthManager.API"
RUN dotnet build "./AuthManager.API.csproj" -c $BUILD_CONFIGURATION --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AuthManager.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-build

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AuthManager.API.dll"]
