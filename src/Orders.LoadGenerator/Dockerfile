# syntax=docker/dockerfile:1
# Load generator image, linux/amd64 and linux/arm64.
# Build from the repo root:  docker build -f src/Orders.LoadGenerator/Dockerfile .
# The SDK stage runs on the build machine and cross-compiles for TARGETARCH (amd64 or arm64).
# Run:  docker run --rm -e TARGET_BASE_URL=https://staging.demo.joshb.app -e SCENARIO=normal -e DURATION_MINUTES=5 orders-loadgen

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Orders.LoadGenerator/Orders.LoadGenerator.csproj src/Orders.LoadGenerator/packages.lock.json src/Orders.LoadGenerator/
RUN dotnet restore src/Orders.LoadGenerator -a "$TARGETARCH" --locked-mode

COPY src/Orders.LoadGenerator/ src/Orders.LoadGenerator/
RUN dotnet publish src/Orders.LoadGenerator -c Release -a "$TARGETARCH" --no-restore --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
# The runtime image ships a non-root "app" user (uid 1654). NBomber writes its reports under /tmp.
USER $APP_UID
ENV SCENARIO=normal DURATION_MINUTES=5
ENTRYPOINT ["dotnet", "Orders.LoadGenerator.dll"]
