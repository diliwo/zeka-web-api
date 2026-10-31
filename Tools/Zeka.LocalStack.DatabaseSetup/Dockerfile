FROM mcr.microsoft.com/dotnet/aspnet:8.0.29
WORKDIR /app
COPY published/ .
USER app
ENTRYPOINT ["dotnet","zeka-local-db-setup.dll"]
