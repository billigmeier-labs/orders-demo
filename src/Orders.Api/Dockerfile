# syntax=docker/dockerfile:1
# Orders API image, plus an EF Core migration bundle at /app/efbundle.
# Build from the repo root:  docker build -f src/Orders.Api/Dockerfile .
# The SDK stage runs on the build machine and cross-compiles for TARGETARCH (amd64 or arm64).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY .config/dotnet-tools.json .config/
COPY src/Orders.Api/Orders.Api.csproj src/Orders.Api/packages.lock.json src/Orders.Api/
RUN dotnet tool restore \
    && dotnet restore src/Orders.Api -a "$TARGETARCH" --locked-mode

COPY src/Orders.Api/ src/Orders.Api/
RUN dotnet publish src/Orders.Api -c Release -a "$TARGETARCH" --no-restore --no-self-contained -o /app \
    && dotnet ef migrations bundle --project src/Orders.Api --configuration Release \
        --target-runtime "linux-$(echo "$TARGETARCH" | sed 's/amd64/x64/')" --self-contained \
        --output /app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
# CMD, not ENTRYPOINT, so the same image can run the bundle: `docker run <image> ./efbundle ...`
CMD ["dotnet", "Orders.Api.dll"]
