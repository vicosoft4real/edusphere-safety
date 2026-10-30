# EduSphere.Safety.EdgeAgent — school edge appliance service (.NET 10).
# Needs `ffmpeg` on PATH (override with --ffmpeg). Camera networks are
# reached over the school LAN; no inbound ports are opened.
# Build from the repository root: docker build -f docker/edgeagent.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY src/EduSphere.Safety.Shared/EduSphere.Safety.Shared.csproj src/EduSphere.Safety.Shared/
COPY src/EduSphere.Safety.Core/EduSphere.Safety.Core.csproj src/EduSphere.Safety.Core/
COPY src/EduSphere.Safety.Application/EduSphere.Safety.Application.csproj src/EduSphere.Safety.Application/
COPY src/EduSphere.Safety.Infrastructure/EduSphere.Safety.Infrastructure.csproj src/EduSphere.Safety.Infrastructure/
COPY src/EduSphere.Safety.Edge/EduSphere.Safety.Edge.csproj src/EduSphere.Safety.Edge/
COPY src/EduSphere.Safety.EdgeAgent/EduSphere.Safety.EdgeAgent.csproj src/EduSphere.Safety.EdgeAgent/
RUN dotnet restore src/EduSphere.Safety.EdgeAgent/EduSphere.Safety.EdgeAgent.csproj
COPY src/ src/
RUN dotnet publish src/EduSphere.Safety.EdgeAgent/EduSphere.Safety.EdgeAgent.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
VOLUME ["/evidence-spool"]
ENTRYPOINT ["dotnet", "EduSphere.Safety.EdgeAgent.dll"]
