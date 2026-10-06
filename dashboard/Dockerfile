# Dashboard image. Build from the repository root:
#   docker build -f dashboard/Dockerfile .
# Separate from the LB image (src/Dockerfile). It contains only the dashboard service, the shared wire records and the web app.

FROM oven/bun:1.4 AS web
WORKDIR /src/dashboard/web
COPY dashboard/web/package.json dashboard/web/bun.lock ./
RUN bun install --frozen-lockfile
COPY dashboard/web/ ./
# Vite writes to ../server/wwwroot.
RUN bun run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY src/Contracts/openai-loadbalancer.Contracts.csproj src/Contracts/
COPY dashboard/server/openai-dashboard.csproj dashboard/server/
RUN dotnet restore dashboard/server/openai-dashboard.csproj
COPY src/Contracts/ src/Contracts/
COPY dashboard/server/ dashboard/server/
COPY --from=web /src/dashboard/server/wwwroot/ dashboard/server/wwwroot/
RUN dotnet publish dashboard/server/openai-dashboard.csproj -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER app
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "openai-dashboard.dll"]
