FROM mcr.microsoft.com/dotnet/aspnet:8.0.30
WORKDIR /app
COPY mvp02-ca.crt /usr/local/share/ca-certificates/zeka-mvp02.crt
RUN update-ca-certificates
COPY app/ ./
USER app
