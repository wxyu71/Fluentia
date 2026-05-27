# Build mobile web
FROM node:20-alpine AS mobile-build
WORKDIR /app/mobile
COPY mobile/package*.json ./
RUN npm ci
COPY mobile/ ./
RUN npm run build

# Build server
FROM golang:1.24-alpine AS server-build
WORKDIR /app/server
COPY server/go.* ./
RUN go mod download
COPY server/*.go ./
RUN CGO_ENABLED=0 GOOS=linux go build -o fluentia-server .

# Final image
FROM alpine:3.20
RUN apk --no-cache add ca-certificates && \
    addgroup -S fluentia && adduser -S fluentia -G fluentia
WORKDIR /app
COPY --from=server-build /app/server/fluentia-server .
COPY --from=mobile-build /app/mobile/dist ./static/
RUN chown -R fluentia:fluentia /app
USER fluentia
EXPOSE 8080
ENV PORT=8080
CMD ["./fluentia-server"]
