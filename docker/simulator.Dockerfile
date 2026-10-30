# Camera simulator — consented/synthetic footage driver for local validation.
# Build from the repository root: docker build -f docker/simulator.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY tools/Simulator/Simulator.csproj tools/Simulator/
COPY src/EduSphere.Safety.Shared/EduSphere.Safety.Shared.csproj src/EduSphere.Safety.Shared/
COPY src/EduSphere.Safety.Core/EduSphere.Safety.Core.csproj src/EduSphere.Safety.Core/
COPY src/EduSphere.Safety.Edge/EduSphere.Safety.Edge.csproj src/EduSphere.Safety.Edge/
RUN dotnet restore tools/Simulator/Simulator.csproj
COPY tools/ tools/
COPY src/ src/
RUN dotnet publish tools/Simulator/Simulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Simulator.dll"]
