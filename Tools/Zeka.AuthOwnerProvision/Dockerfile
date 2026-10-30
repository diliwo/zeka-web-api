# Build context is the clean, source-bound host publish directory, never the repository or private configuration root.
FROM mcr.microsoft.com/dotnet/aspnet:8.0.29
WORKDIR /app
ARG SOURCE_COMMIT
ARG BINARY_SHA256
COPY . /app/
RUN test ${#SOURCE_COMMIT} -eq 40 && test ${#BINARY_SHA256} -eq 64 \
    && printf '%s  %s\n' "$BINARY_SHA256" /app/zeka-auth-owner-provision.dll | sha256sum -c -
LABEL org.opencontainers.image.revision="${SOURCE_COMMIT}" \
      com.diliwo.zeka.binary-sha256="${BINARY_SHA256}" \
      com.diliwo.zeka.scope="m1-owner-provision" \
      com.diliwo.zeka.owner="windows-codex-m1" \
      com.docker.compose.project="zeka-m1"
USER app
ENTRYPOINT ["dotnet", "/app/zeka-auth-owner-provision.dll"]
