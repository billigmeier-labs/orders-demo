# syntax=docker/dockerfile:1
# Demo console image, linux/amd64 and linux/arm64.
# Build from the repo root:  docker build -f src/Orders.Console/Dockerfile .
# The SDK stage runs on the build machine and cross-compiles for TARGETARCH (amd64 or arm64).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Orders.Console/Orders.Console.csproj src/Orders.Console/packages.lock.json src/Orders.Console/
RUN dotnet restore src/Orders.Console -a "$TARGETARCH" --locked-mode

COPY src/Orders.Console/ src/Orders.Console/
RUN dotnet publish src/Orders.Console -c Release -a "$TARGETARCH" --no-restore --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./
# The aspnet image ships a non-root "app" user (uid 1654).
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "Orders.Console.dll"]
