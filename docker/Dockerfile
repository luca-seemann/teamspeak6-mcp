# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone so the layer caches across source changes.
COPY global.json Directory.Build.props Directory.Packages.props TeamSpeakMcp.slnx ./
COPY src/TeamSpeak.Query/TeamSpeak.Query.csproj src/TeamSpeak.Query/
COPY src/TeamSpeak.Mcp/TeamSpeak.Mcp.csproj src/TeamSpeak.Mcp/
COPY tests/TeamSpeak.Query.FakeServer/TeamSpeak.Query.FakeServer.csproj tests/TeamSpeak.Query.FakeServer/
COPY tests/TeamSpeak.Query.Tests/TeamSpeak.Query.Tests.csproj tests/TeamSpeak.Query.Tests/
COPY tests/TeamSpeak.Mcp.Tests/TeamSpeak.Mcp.Tests.csproj tests/TeamSpeak.Mcp.Tests/
COPY tests/TeamSpeak.Mcp.IntegrationTests/TeamSpeak.Mcp.IntegrationTests.csproj tests/TeamSpeak.Mcp.IntegrationTests/
RUN dotnet restore src/TeamSpeak.Mcp/TeamSpeak.Mcp.csproj

COPY . .
RUN dotnet publish src/TeamSpeak.Mcp/TeamSpeak.Mcp.csproj \
      --no-restore \
      --configuration Release \
      --output /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The container serves remote clients, so HTTP is the only transport that makes sense here.
EXPOSE 7801
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

USER $APP_UID
ENTRYPOINT ["./teamspeak6-mcp", "--transport", "http", "--url", "http://0.0.0.0:7801"]
