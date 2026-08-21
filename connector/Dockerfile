# Crawldad self-registering browser connector.
#
# Bundles a maintained headless Chromium (Debian's chromium package), an nginx
# Host-rewriting reverse proxy, and cloudflared for a free ephemeral quick
# tunnel. The entrypoint launches all three and self-registers the tunnel with
# Crawldad. Runs unprivileged.
FROM debian:bookworm-slim

# Pinned via build args; override to bump. cloudflared is additionally verified
# against a pinned per-arch SHA-256 below (the base image and apt packages are
# not digest-pinned, so this is a hardened download, not a bit-reproducible image).
ARG CLOUDFLARED_VERSION=2024.8.2
# Populated by BuildKit/buildx (amd64 or arm64); defaults for a plain build.
ARG TARGETARCH=amd64
# SHA-256 of the pinned cloudflared release asset, per architecture. Bump these
# in lockstep with CLOUDFLARED_VERSION (sha256sum of the release download).
ARG CLOUDFLARED_SHA256_amd64=e6cb78348e05680805c8317b5073c54401c1ebac9fa88a2cc35be752858bdc6b
ARG CLOUDFLARED_SHA256_arm64=f0cc2f42b658a89a794ca91210f73df2f3d51c459f050ae1ee57b221d1e30f98

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/home/crawldad

# bash (not the default dash) so the cloudflared step can source the connector's
# shared helpers; pipefail so a failed download aborts the layer.
SHELL ["/bin/bash", "-euxo", "pipefail", "-c"]

RUN apt-get update; \
    apt-get install -y --no-install-recommends \
    chromium \
    nginx-light \
    curl \
    ca-certificates \
    jq \
    tini; \
    rm -rf /var/lib/apt/lists/*; \
    useradd --create-home --home-dir /home/crawldad --uid 10001 --shell /usr/sbin/nologin crawldad

# Fetch cloudflared and verify it against the pinned per-arch SHA-256 *before* it
# is placed on PATH or made executable, so a corrupted or tampered release asset
# can never run. verify_sha256 is the same helper the connector's selftest covers.
COPY lib.sh /opt/connector/lib.sh
RUN source /opt/connector/lib.sh; \
    case "${TARGETARCH}" in \
    amd64) cf_sha="${CLOUDFLARED_SHA256_amd64}" ;; \
    arm64) cf_sha="${CLOUDFLARED_SHA256_arm64}" ;; \
    *) echo "unsupported TARGETARCH='${TARGETARCH}'" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /tmp/cloudflared \
    "https://github.com/cloudflare/cloudflared/releases/download/${CLOUDFLARED_VERSION}/cloudflared-linux-${TARGETARCH}"; \
    verify_sha256 /tmp/cloudflared "${cf_sha}"; \
    install -m 0755 /tmp/cloudflared /usr/local/bin/cloudflared; \
    rm -f /tmp/cloudflared; \
    cloudflared --version

# The nginx config is a template; entrypoint.sh renders it (with CDP_PORT/
# PROXY_PORT substituted) to a writable path under /tmp at start-up.
COPY nginx.conf.template /opt/connector/nginx.conf.template
COPY entrypoint.sh /opt/connector/entrypoint.sh
RUN chmod 0755 /opt/connector/entrypoint.sh

USER crawldad
WORKDIR /home/crawldad

# tini reaps the browser's child processes; the entrypoint supervises the rest.
ENTRYPOINT ["/usr/bin/tini", "--", "/opt/connector/entrypoint.sh"]
