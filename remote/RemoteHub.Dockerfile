# syntax=docker/dockerfile:1.7
#
# Both base-image arguments are mandatory from compose.yaml and must be
# supplied as vetted digest-pinned references. The deliberately narrow build
# context is remote/RemoteHub, not the deployment directory, so untracked .env
# files and rendered secret configuration never reach the image builder.
ARG DOTNET_SDK_IMAGE
ARG DOTNET_RUNTIME_IMAGE

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY RemoteHub.csproj packages.lock.json ./
RUN dotnet restore ./RemoteHub.csproj --locked-mode

# Copy only production source and static Admin assets. Tests, local journals,
# generated output, and deployment secrets are deliberately not image inputs.
COPY Program.cs appsettings.json ./
COPY Auth ./Auth
COPY Configuration ./Configuration
COPY Domain ./Domain
COPY Persistence ./Persistence
COPY wwwroot ./wwwroot
RUN dotnet publish ./RemoteHub.csproj --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
ARG REMOTEHUB_SOURCE_REVISION
LABEL org.opencontainers.image.title="stayactive-remotehub" \
      org.opencontainers.image.revision="${REMOTEHUB_SOURCE_REVISION}"

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5088 \
    DOTNET_EnableDiagnostics=0

WORKDIR /app
COPY --from=build /out/ ./

# Compose enforces the same numeric identity. The host journal directory must
# be writable only by this identity; the rendered configuration is root-owned
# and group-readable by it.
USER 65532:65532
EXPOSE 5088
ENTRYPOINT ["dotnet", "RemoteHub.dll"]
