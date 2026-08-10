# Crawldad.Web — multi-stage container image.
#
# The one host is a .NET 10 Wolverine.HTTP + Marten API (no Blazor). This image publishes a Release build on
# the .NET SDK pinned by global.json and runs it on the aspnet runtime base as a non-root user. The image is
# built server-side by `az acr build` in the deploy workflow (no local docker needed there); `docker build .`
# from the repo root works identically for local validation.
#
# Ports: the aspnet base sets ASPNETCORE_HTTP_PORTS=8080 and Kestrel listens there, which is the Container Apps
# ingress targetPort. Schema is applied out-of-band via the `db-apply` JasperFx command (a Container Apps job
# runs `dotnet Crawldad.Web.dll db-apply`), never on a normal server start — the same argument-driven entrypoint
# below serves with no args and runs CLI commands with args (see Program.cs / RunJasperFxCommands).

# syntax=docker/dockerfile:1

# The SDK/runtime major is 10.0; global.json (copied into the restore layer) pins the exact SDK feature band
# (10.0.3xx, rollForward latestFeature), so the build fails loudly if the base image ever drifts below it.
ARG DOTNET_SDK_TAG=10.0
ARG DOTNET_RUNTIME_TAG=10.0

# ── Build + publish ────────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_TAG} AS build
WORKDIR /src

# Restore layer: copy ONLY the files that affect restore first, so the (slow) NuGet restore layer is cached and
# reused across source-only edits. Central package versions + shared build props govern every project.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Crawldad.Contracts/Crawldad.Contracts.csproj src/Crawldad.Contracts/
COPY src/Crawldad.Web/Crawldad.Web.csproj src/Crawldad.Web/
RUN dotnet restore src/Crawldad.Web/Crawldad.Web.csproj

# Source layer: the web/contracts source, plus the two repo-root files embedded into the assembly as resources
# (schema/crawldad-1.schema.json and llms.txt — referenced by Crawldad.Web.csproj via ..\..\). The Fixtures under
# src/Crawldad.Web ship as content (the `fake` browser backend loads them at runtime), so they publish with the app.
COPY schema/ schema/
COPY llms.txt ./
# The repo .editorconfig carries the analyzer severity config (e.g. CA1062/MA0048 = none). It MUST be present at build
# time or those rules fire as errors under warnings-as-errors. Placed at the WORKDIR root so it governs all of src/.
COPY .editorconfig ./
COPY src/ src/

# Publish Release. TreatWarningsAsErrors + analyzers are inherited from Directory.Build.props (config-agnostic),
# so any warning fails this build exactly as CI does. UseAppHost=false: we launch via `dotnet Crawldad.Web.dll`,
# so no native apphost is needed. SourceRevisionId stamps the build commit into the assembly for traceability.
ARG SOURCE_REVISION_ID=""
RUN dotnet publish src/Crawldad.Web/Crawldad.Web.csproj \
      -c Release -o /app --no-restore \
      /p:UseAppHost=false \
      /p:SourceRevisionId=${SOURCE_REVISION_ID}

# ── Runtime ────────────────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_TAG} AS final
WORKDIR /app

# The aspnet base already exports ASPNETCORE_HTTP_PORTS=8080; document the listen port for humans + tooling.
EXPOSE 8080

COPY --from=build /app ./

# Run as the base image's non-root user (APP_UID=1654). Staging uses the Azure Blob storage provider, so the app
# writes nothing to the container filesystem; in-memory Wolverine codegen and any temp use land under /tmp.
USER $APP_UID

# Argument-driven entrypoint: no args → serve (Kestrel on 8080); `db-apply`/`projections`/… → run that CLI command
# and exit with its code (used by the Container Apps db-apply job). See Program.cs (RunJasperFxCommands).
ENTRYPOINT ["dotnet", "Crawldad.Web.dll"]
