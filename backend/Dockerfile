# Build and run the API (which also hosts the settlement worker).
#
# Multi-stage: the SDK never reaches the final image, so what ships is the runtime plus the
# published output — smaller, and no compiler or source on a running container.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, restore, then the source. Restore is the slow step and only depends on
# the csproj/props, so editing a .cs file re-uses the cached restore layer.
COPY Directory.Build.props ./
COPY src/Payments.Domain/Payments.Domain.csproj src/Payments.Domain/
COPY src/Payments.Infrastructure/Payments.Infrastructure.csproj src/Payments.Infrastructure/
COPY src/Payments.Worker/Payments.Worker.csproj src/Payments.Worker/
COPY src/Payments.Api/Payments.Api.csproj src/Payments.Api/
RUN dotnet restore src/Payments.Api/Payments.Api.csproj

COPY src/ src/
RUN dotnet publish src/Payments.Api/Payments.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl: the runtime image ships no HTTP client, and compose's healthcheck needs one.
# libgssapi-krb5-2: Npgsql probes for Kerberos at startup and, without it, logs
#   "Error: libgssapi_krb5.so.2: cannot open shared object file"
# before carrying on perfectly happily over password auth. It is pure noise — but it is the
# first thing printed when a reviewer runs the demo, and it reads like a failure.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# The aspnet image ships a non-root `app` user; nothing here needs to write to the filesystem.
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "Payments.Api.dll"]
