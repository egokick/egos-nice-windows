# VPN-only two-laptop setup

The former LAN-only procedure is no longer the travel-ready path. When the two
laptops are on different networks, use the deployed, fully self-hosted Fly
Headscale control plane and the tested handoff in
[`../fly-headscale/README.md`](../fly-headscale/README.md).

The private LAN stack remains useful for isolated development of MeshCentral,
RemoteHub, Keycloak, and the production enrollment broker. Do not use it for
the urgent outside-network exit-node setup.
