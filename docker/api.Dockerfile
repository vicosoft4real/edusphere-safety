# EduSphere.Safety.Api — ASP.NET Core (.NET 10).
# Build from the repository root: docker build -f docker/api.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY src/EduSphere.Safety.Shared/EduSphere.Safety.Shared.csproj src/EduSphere.Safety.Shared/
COPY src/EduSphere.Safety.Core/EduSphere.Safety.Core.csproj src/EduSphere.Safety.Core/
COPY src/EduSphere.Safety.Application/EduSphere.Safety.Application.csproj src/EduSphere.Safety.Application/
COPY src/EduSphere.Safety.Infrastructure/EduSphere.Safety.Infrastructure.csproj src/EduSphere.Safety.Infrastructure/
COPY src/EduSphere.Safety.Api/EduSphere.Safety.Api.csproj src/EduSphere.Safety.Api/
RUN dotnet restore src/EduSphere.Safety.Api/EduSphere.Safety.Api.csproj
COPY src/ src/
RUN dotnet publish src/EduSphere.Safety.Api/EduSphere.Safety.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "EduSphere.Safety.Api.dll"]
