#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0.29 AS base
USER app
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0.419 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY nuget.config .
COPY ["Services/ClientManagement/Client.API/ClientManagement.API.csproj", "Services/ClientManagement/Client.API/"]

RUN --mount=type=secret,id=github_token,required=true \
    NuGetPackageSourceCredentials_github="Username=diliwo;Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" \
    dotnet restore "./Services/ClientManagement/Client.API/./ClientManagement.API.csproj" \
      --configfile nuget.config

COPY . .
WORKDIR "/src/Services/ClientManagement/Client.API"
RUN --mount=type=secret,id=github_token,required=true \
    NuGetPackageSourceCredentials_github="Username=diliwo;Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" \
    dotnet build "./ClientManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN --mount=type=secret,id=github_token,required=true \
    NuGetPackageSourceCredentials_github="Username=diliwo;Password=$(cat /run/secrets/github_token);ValidAuthenticationTypes=Basic" \
    dotnet publish "./ClientManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ClientManagement.API.dll"]
