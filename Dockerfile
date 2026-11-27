FROM --platform=$BUILDPLATFORM hub.aiursoft.com/aiursoft/internalimages/dotnet AS build-env
WORKDIR /src
COPY . .
RUN dotnet publish ./src/Aiursoft.AnduinOS.OfflineBundles/Aiursoft.AnduinOS.OfflineBundles.csproj \
    --configuration Release --no-self-contained -p:UseAppHost=false -p:GeneratePackageOnBuild=false --output /app --configfile nuget.config

FROM hub.aiursoft.com/aiursoft/internalimages/dotnet
RUN apt-get update && apt-get install -y --no-install-recommends apt-utils gpgv ca-certificates && \
    rm -rf /var/lib/apt/lists/* && useradd --create-home --uid 10001 builder && \
    mkdir -p /data && chown builder:builder /data
WORKDIR /app
COPY --from=build-env /app .
COPY profiles /app/profiles
USER builder
VOLUME /data
ENTRYPOINT ["dotnet", "/app/anduinos-offline-bundles.dll"]
CMD ["automate", "--settings", "/config/automation.json"]
