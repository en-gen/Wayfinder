# syntax=docker/dockerfile:1

# Flow.Silo container image (work item #49): multi-stage build so the shipped runtime image
# carries only published bits, not the SDK. Build context MUST be the REPOSITORY ROOT, not
# this directory - Flow.Silo's ProjectReferences pull in sibling projects (Flow.Grains,
# Flow.Grains.Interfaces) and restore/build also depend on repo-root files (global.json's SDK
# pin, Directory.Build.props' NuGet Audit gate, nuget.config's pinned package source). Build
# from the repo root, e.g.:
#
#   docker build -f src/Flow.Silo/Dockerfile -t case-flow-silo:eval .
#
# (devops/eval/docker-compose.yml does exactly this via its build.context/build.dockerfile.)

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Solution-wide files that affect restore/build - copied first so this layer only invalidates
# when one of THEM changes, not on every source edit.
COPY global.json Directory.Build.props nuget.config ./

# Project files only, for the same layer-caching reason: `dotnet restore` only needs to re-run
# when a .csproj actually changes.
COPY src/Flow.Silo/Flow.Silo.csproj src/Flow.Silo/
COPY src/Flow.Grains/Flow.Grains.csproj src/Flow.Grains/
COPY src/Flow.Grains.Interfaces/Flow.Grains.Interfaces.csproj src/Flow.Grains.Interfaces/

# EnableSourceControlManagerQueries=false: SourceLink's git-metadata probe has no .git to
# query inside a Docker build context (COPY doesn't bring .git along - see .dockerignore) and
# errors out otherwise; same flag used for solution builds outside a real git checkout.
RUN dotnet restore src/Flow.Silo/Flow.Silo.csproj -p:EnableSourceControlManagerQueries=false

# Now the rest of the source for those same three projects.
COPY src/Flow.Silo/ src/Flow.Silo/
COPY src/Flow.Grains/ src/Flow.Grains/
COPY src/Flow.Grains.Interfaces/ src/Flow.Grains.Interfaces/

RUN dotnet publish src/Flow.Silo/Flow.Silo.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl backs the HEALTHCHECK below (GET /health) - the aspnet runtime image ships no HTTP
# client tool by default. Installed as root, before the USER switch further down.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Kestrel (the co-hosted web host - see Program.cs ConfigureWebHostDefaults) listens on 8080,
# the .NET 8+ container convention for a non-root default HTTP port (ports below 1024 need
# root). Orleans silo/gateway ports (11111/30000, see Program.cs ConfigureDeployedEndpoints)
# are documented via EXPOSE too, though each is a real listener regardless of EXPOSE - EXPOSE
# is metadata only; devops/eval/docker-compose.yml does the actual host port mapping.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080 11111 30000

# Non-root: $APP_UID is the non-root "app" user baked into Microsoft's .NET 8+ container
# images - no manual useradd/addgroup needed.
USER $APP_UID

HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Flow.Silo.dll"]
