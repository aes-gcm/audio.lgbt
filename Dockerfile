FROM golang:1.26 AS build
WORKDIR /src
COPY go.mod go.sum ./
COPY third_party ./third_party
RUN go mod download
COPY cmd ./cmd
COPY internal ./internal
COPY web ./web
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /audio-server ./cmd/audio-server

FROM gcr.io/distroless/static-debian12:nonroot
LABEL org.opencontainers.image.source="https://github.com/aes-gcm/audio.lgbt"
COPY --from=build /audio-server /audio-server
WORKDIR /data
EXPOSE 443/tcp 3478/udp
ENTRYPOINT ["/audio-server"]
