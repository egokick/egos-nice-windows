FROM golang:1.26.4-bookworm AS build
WORKDIR /src
COPY go.mod main.go ./
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -trimpath -ldflags="-s -w" -o /out/accept-invite .

FROM tailscale/tailscale:v1.102.1
COPY --from=build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
COPY --from=build /out/accept-invite /accept-invite
ENTRYPOINT ["/accept-invite"]
