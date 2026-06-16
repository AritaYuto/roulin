# Multi-stage build for roulin-cli and roulin-server.
FROM golang:1.26-alpine AS builder

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN CGO_ENABLED=0 go build -o /out/roulin-cli ./cmd/roulin-cli/ && \
    CGO_ENABLED=0 go build -o /out/roulin-server ./cmd/roulin-server/

# ---
FROM alpine:3.21

RUN apk add --no-cache ca-certificates
COPY --from=builder /out/roulin-cli /usr/local/bin/
COPY --from=builder /out/roulin-server /usr/local/bin/

RUN mkdir -p /data /logs
VOLUME ["/data", "/logs"]

EXPOSE 8765

ENTRYPOINT ["roulin-server"]
CMD ["serve", "--storage", "file:///data", "--port", "8765", "--log-dir", "/logs"]
