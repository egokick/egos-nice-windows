# Taildesk local control plane

This stack runs Headscale and Caddy entirely on the command-center laptop.
Persistent controller state is stored below `state/` and is intentionally not
committed. The public LAN endpoint blocks Headscale's administrative REST API;
Taildesk reaches it only through `https://headscale-controller.stayactive.test:4443`
on Windows loopback.

The recovery configuration binds both endpoints to Windows loopback, does not
publish the DERP/STUN UDP listener, and does not auto-restart its containers. It is
safe for local command-center validation but cannot enroll other devices until
you deliberately expose a self-owned HTTPS endpoint. No third-party relay or hosted control plane is configured here.
