FROM ubuntu:24.04

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bluez \
        bluez-tools \
        ca-certificates \
        curl \
        dbus \
        dbus-x11 \
        fonts-liberation \
        fonts-noto-color-emoji \
        gnupg \
        iproute2 \
        kmod \
        libasound2t64 \
        libgbm1 \
        libnss3 \
        libu2f-udev \
        netcat-openbsd \
        novnc \
        openbox \
        procps \
        python3 \
        python3-minimal \
        python3-websocket \
        supervisor \
        usbutils \
        websockify \
        wget \
        x11vnc \
        x11-utils \
        xauth \
        xdg-utils \
        xvfb \
    && install -d -m 0755 /etc/apt/keyrings \
    && curl -fsSL https://dl.google.com/linux/linux_signing_key.pub \
        | gpg --dearmor -o /etc/apt/keyrings/google-chrome.gpg \
    && printf '%s\n' \
        'deb [arch=amd64 signed-by=/etc/apt/keyrings/google-chrome.gpg] https://dl.google.com/linux/chrome/deb/ stable main' \
        > /etc/apt/sources.list.d/google-chrome.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends google-chrome-stable \
    && rm -f /usr/share/dbus-1/system-services/org.bluez.service \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --gid 10001 chrome \
    && useradd --uid 10001 --gid chrome --create-home --shell /bin/bash chrome \
    && install -d -o chrome -g chrome -m 0700 \
        /home/chrome/work-profile \
        /home/chrome/Downloads \
    && install -d -m 0755 \
        /opt/stayactive \
        /opt/stayactive/test \
        /var/log/stayactive \
        /run/dbus \
        /run/stayactive \
    && install -d -m 0755 /etc/opt/chrome/policies/managed

COPY container/supervisord.conf /etc/supervisor/conf.d/stayactive.conf
COPY container/entrypoint.sh /opt/stayactive/entrypoint.sh
COPY container/run-chrome.sh /opt/stayactive/run-chrome.sh
COPY container/healthcheck.sh /opt/stayactive/healthcheck.sh
COPY container/prepare-detach.sh /opt/stayactive/prepare-detach.sh
COPY container/resume-bluetooth.sh /opt/stayactive/resume-bluetooth.sh
COPY container/passkey-test.html /opt/stayactive/test/index.html
COPY container/stayactive-keepalive.py /opt/stayactive/stayactive-keepalive.py
COPY container/check-chrome-hybrid.py /opt/stayactive/check-chrome-hybrid.py

RUN chmod 0755 /opt/stayactive/*.sh \
    && chmod 0755 /opt/stayactive/stayactive-keepalive.py \
    && chmod 0755 /opt/stayactive/check-chrome-hybrid.py \
    && chown -R chrome:chrome /home/chrome

ENV DISPLAY=:99
ENV HOME=/home/chrome
ENV CHROME_START_URL=https://windows365.microsoft.com/ent#/devices

HEALTHCHECK --interval=15s --timeout=40s --start-period=30s --retries=4 \
    CMD ["/opt/stayactive/healthcheck.sh", "--container"]

ENTRYPOINT ["/opt/stayactive/entrypoint.sh"]
