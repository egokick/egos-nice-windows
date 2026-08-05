FROM golang:1.26.5-bookworm@sha256:1ecb7edf62a0408027bd5729dfd6b1b8766e578e8df93995b225dfd0944eb651 AS gateway-build
WORKDIR /src
COPY gateway/ ./
RUN CGO_ENABLED=0 go test ./... && CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/opticon-gateway .

FROM headscale/headscale:0.29.3@sha256:0e7f1c6e4ce6c2a2a001103ecd3fa645a045adf30ac8a5234fe037b43000cd72

ARG FINAL_UID=65532
ARG MIGRATE_PERMISSIONS=0
ENV OPTICON_MIGRATE_PERMISSIONS=${MIGRATE_PERMISSIONS}
COPY --from=gateway-build --chown=65532:65532 /out/opticon-gateway /opt/opticon/gateway
COPY --chown=65532:65532 config.yaml /etc/headscale/config.yaml
COPY --chown=65532:65532 policy.hujson /etc/headscale/policy.hujson
COPY --chown=65532:65532 artifacts/ /opt/opticon/artifacts/

EXPOSE 8080/tcp 3478/udp

USER ${FINAL_UID}:${FINAL_UID}
ENTRYPOINT ["/opt/opticon/gateway"]
