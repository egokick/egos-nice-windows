ARG HEADSCALE_IMAGE=headscale/headscale@sha256:d337f1be4a9155b330aa9077bf6c82d24ff0581b8e69390ebc6d7c623bb339ce
ARG CADDY_IMAGE=caddy@sha256:c3d7ee5d2b11f9dc54f947f68a734c84e9c9666c92c88a7f30b9cba5da182adb

FROM ${HEADSCALE_IMAGE} AS headscale
FROM ${CADDY_IMAGE}

USER root

COPY --from=headscale /ko-app/headscale /usr/local/bin/headscale
COPY Caddyfile /etc/caddy/Caddyfile
COPY headscale-config.yaml.template /etc/headscale/config.yaml.template
COPY policy.hujson /etc/headscale/policy.hujson
COPY entrypoint.sh /usr/local/bin/stayactive-headscale-entrypoint

RUN chmod 0755 /usr/local/bin/headscale /usr/local/bin/stayactive-headscale-entrypoint

ENV HEADSCALE_CONFIG=/data/runtime/headscale-config.yaml \
    XDG_DATA_HOME=/data/caddy \
    XDG_CONFIG_HOME=/data/caddy-config

EXPOSE 443/tcp 3478/udp

ENTRYPOINT ["/usr/local/bin/stayactive-headscale-entrypoint"]
