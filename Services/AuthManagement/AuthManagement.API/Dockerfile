#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
RUN mkdir -p /src/zeka-web-api/local-nuget-packages
COPY ["zeka-web-api/Services/AuthManagement/AuthManagement.API/AuthManagement.API.csproj", "zeka-web-api/Services/AuthManagement/AuthManagement.API/"]
COPY ["zeka-web-api/Services/ClientManagement/Client.Application/ClientManagement.Application.csproj", "zeka-web-api/Services/ClientManagement/Client.Application/"]
COPY ["zeka-web-api/Services/ClientManagement/Client.Core/ClientManagement.Core.csproj", "zeka-web-api/Services/ClientManagement/Client.Core/"]
COPY ["auth-dotnet-lib/Authentication/Authentication.csproj", "auth-dotnet-lib/Authentication/"]
COPY ["eventbus-dotnet-lib/Eventbus/Eventbus.csproj", "eventbus-dotnet-lib/Eventbus/"]
COPY ["tenant-dotnet-lib/Tenant/Tenant.csproj", "tenant-dotnet-lib/Tenant/"]
RUN dotnet restore "./zeka-web-api/Services/AuthManagement/AuthManagement.API/AuthManagement.API.csproj"
COPY . .
WORKDIR "/src/zeka-web-api/Services/AuthManagement/AuthManagement.API"
RUN dotnet build "./AuthManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AuthManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AuthManagement.API.dll"]
