FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=amd64
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /source
COPY src/relay/RemoteDisplaySwitch.Relay/ src/relay/RemoteDisplaySwitch.Relay/
COPY src/shared/RemoteDisplaySwitch.Contracts/ src/shared/RemoteDisplaySwitch.Contracts/
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac \
    && dotnet publish src/relay/RemoteDisplaySwitch.Relay/RemoteDisplaySwitch.Relay.csproj \
       --configuration Release --runtime "$rid" --self-contained true \
       -p:RuntimeIdentifiers= -p:RestorePackagesWithLockFile=false -p:UseAppHost=true --output /out

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    RDS_RELAY_CONFIG=/run/relay/config.json \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["./RemoteDisplaySwitch.Relay"]
