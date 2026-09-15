# Build stage. It runs on the build machine's own architecture and cross-publishes for the target, so
# an arm64 image needs no emulation:
#
#   docker buildx build --platform linux/amd64,linux/arm64 -f docker/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore against the manifests alone so the layer caches across source changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/TeamSpeak.Query/TeamSpeak.Query.csproj src/TeamSpeak.Query/
COPY src/TeamSpeak.Mcp/TeamSpeak.Mcp.csproj src/TeamSpeak.Mcp/
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64 ;; \
      arm64) echo linux-arm64 ;; \
      *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac > /tmp/rid \
 && dotnet restore src/TeamSpeak.Mcp/TeamSpeak.Mcp.csproj --runtime "$(cat /tmp/rid)"

COPY src/ src/
RUN dotnet publish src/TeamSpeak.Mcp/TeamSpeak.Mcp.csproj \
      --no-restore \
      --configuration Release \
      --runtime "$(cat /tmp/rid)" \
      --output /app

# Runtime stage. The binary is self-contained, so the image needs only the native libraries .NET
# depends on, not a .NET runtime.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/teamspeak6-mcp .

# The container serves remote clients, so HTTP is the only transport that makes sense here.
EXPOSE 7801
USER $APP_UID
ENTRYPOINT ["./teamspeak6-mcp", "--transport", "http", "--url", "http://0.0.0.0:7801"]
