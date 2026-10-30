# EduSphere.Safety.Worker — durable dispatch + retention (.NET 10).
# Build from the repository root: docker build -f docker/worker.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY src/EduSphere.Safety.Shared/EduSphere.Safety.Shared.csproj src/EduSphere.Safety.Shared/
COPY src/EduSphere.Safety.Core/EduSphere.Safety.Core.csproj src/EduSphere.Safety.Core/
COPY src/EduSphere.Safety.Application/EduSphere.Safety.Application.csproj src/EduSphere.Safety.Application/
COPY src/EduSphere.Safety.Infrastructure/EduSphere.Safety.Infrastructure.csproj src/EduSphere.Safety.Infrastructure/
COPY src/EduSphere.Safety.Worker/EduSphere.Safety.Worker.csproj src/EduSphere.Safety.Worker/
RUN dotnet restore src/EduSphere.Safety.Worker/EduSphere.Safety.Worker.csproj
COPY src/ src/
RUN dotnet publish src/EduSphere.Safety.Worker/EduSphere.Safety.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "EduSphere.Safety.Worker.dll"]
