#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER app
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Services/AdminAreaManagement/AdminAreaManagement.API/AdminAreaManagement.API.csproj", "Services/AdminAreaManagement/AdminAreaManagement.API/"]
COPY ["Services/AdminAreaManagement/AdminAreaManagement.Application/AdminAreaManagement.Application.csproj", "Services/AdminAreaManagement/AdminAreaManagement.Application/"]
COPY ["Services/AdminAreaManagement/AdminAreaManagement.Core/AdminAreaManagement.Core.csproj", "Services/AdminAreaManagement/AdminAreaManagement.Core/"]
COPY ["Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/AdminAreaManagement.Infrastructure.csproj", "Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/"]
RUN dotnet restore "./Services/AdminAreaManagement/AdminAreaManagement.API/./AdminAreaManagement.API.csproj"
COPY . .
WORKDIR "/src/Services/AdminAreaManagement/AdminAreaManagement.API"
RUN dotnet build "./AdminAreaManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./AdminAreaManagement.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AdminAreaManagement.API.dll"]