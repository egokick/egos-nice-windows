# syntax=docker/dockerfile:1.7
#
# The build context is only remote/EnrollmentBroker.  That deliberately keeps
# rendered deployment configuration, Headscale credentials, and local journals
# outside the builder's reach.
ARG DOTNET_SDK_IMAGE
ARG DOTNET_RUNTIME_IMAGE

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY EnrollmentBroker.csproj packages.lock.json ./
RUN dotnet restore ./EnrollmentBroker.csproj --locked-mode

# Tests and local build output are excluded from the production image.  The
# explicit copies make additions to the broker surface a conscious review.
COPY Program.cs ./
COPY Auth ./Auth
COPY Configuration ./Configuration
COPY Domain ./Domain
COPY Persistence ./Persistence
COPY Services ./Services
RUN dotnet publish ./EnrollmentBroker.csproj --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
ARG ENROLLMENTBROKER_SOURCE_REVISION
LABEL org.opencontainers.image.title="stayactive-enrollmentbroker" \
      org.opencontainers.image.revision="${ENROLLMENTBROKER_SOURCE_REVISION}"

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:5090 \
    DOTNET_EnableDiagnostics=0

WORKDIR /app
COPY --from=build /out/ ./

# Compose enforces this same numeric identity and gives it only the two secret
# files, its journal directory, and Caddy's public trust root.
USER 65532:65532
EXPOSE 5090
ENTRYPOINT ["dotnet", "EnrollmentBroker.dll"]
