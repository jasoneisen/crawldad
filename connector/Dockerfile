# Crawldad self-registering browser connector.
#
# Bundles a maintained headless Chromium (Debian's chromium package), an nginx
# Host-rewriting reverse proxy, and cloudflared for a free ephemeral quick
# tunnel. The entrypoint launches all three and self-registers the tunnel with
# Crawldad. Runs unprivileged.
FROM debian:bookworm-slim

# Pinned via build args so the image is reproducible; override to bump.
ARG CLOUDFLARED_VERSION=2024.8.2
# Populated by BuildKit/buildx (amd64 or arm64); defaults for a plain build.
ARG TARGETARCH=amd64

ENV DEBIAN_FRONTEND=noninteractive \
    HOME=/home/crawldad

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
    chromium \
    nginx-light \
    curl \
    ca-certificates \
    jq \
    tini; \
    curl -fsSL -o /usr/local/bin/cloudflared \
    "https://github.com/cloudflare/cloudflared/releases/download/${CLOUDFLARED_VERSION}/cloudflared-linux-${TARGETARCH}"; \
    chmod 0755 /usr/local/bin/cloudflared; \
    cloudflared --version; \
    rm -rf /var/lib/apt/lists/*; \
    useradd --create-home --home-dir /home/crawldad --uid 10001 --shell /usr/sbin/nologin crawldad

COPY nginx.conf /opt/connector/nginx.conf
COPY lib.sh /opt/connector/lib.sh
COPY entrypoint.sh /opt/connector/entrypoint.sh
RUN chmod 0755 /opt/connector/entrypoint.sh

USER crawldad
WORKDIR /home/crawldad

# tini reaps the browser's child processes; the entrypoint supervises the rest.
ENTRYPOINT ["/usr/bin/tini", "--", "/opt/connector/entrypoint.sh"]
