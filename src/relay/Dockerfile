FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /source
COPY THIRD-PARTY-NOTICES.md ./
COPY licenses/ licenses/
COPY src/relay/Fy1Switch.Relay/ src/relay/Fy1Switch.Relay/
COPY src/shared/Fy1Switch.Contracts/ src/shared/Fy1Switch.Contracts/
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet publish src/relay/Fy1Switch.Relay/Fy1Switch.Relay.csproj \
       --configuration Release --runtime "$rid" --self-contained true \
       -p:RuntimeIdentifiers= -p:RestorePackagesWithLockFile=false -p:UseAppHost=true --output /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /var/lib/fy1switch-relay && chown "$APP_UID:$APP_UID" /var/lib/fy1switch-relay && chmod 700 /var/lib/fy1switch-relay
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    FY1SWITCH_RELAY_CONFIG=/run/relay/config.json \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["./Fy1Switch.Relay"]
