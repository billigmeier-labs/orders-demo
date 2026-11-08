# Demo console image, linux/amd64 and linux/arm64.
# Build context is this directory:
#   docker buildx build --platform linux/amd64,linux/arm64 -t orders-console src/Orders.Console
# The SDK stage runs on the build machine's own architecture and cross-publishes for the target,
# so an arm64 image builds on an amd64 host without emulation.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Orders.Console.csproj ./
RUN dotnet restore -a $TARGETARCH
COPY . ./
RUN dotnet publish -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
# The aspnet image ships a non-root "app" user (uid 1654).
USER $APP_UID
ENTRYPOINT ["dotnet", "Orders.Console.dll"]
