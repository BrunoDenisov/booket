# syntax=docker/dockerfile:1

# ---- Build -----------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, using only the project files, so the package layer is cached across code changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Booket.Domain/Booket.Domain.csproj                 src/Booket.Domain/
COPY src/Booket.Application/Booket.Application.csproj       src/Booket.Application/
COPY src/Booket.Infrastructure/Booket.Infrastructure.csproj src/Booket.Infrastructure/
COPY src/Booket.Api/Booket.Api.csproj                       src/Booket.Api/
RUN dotnet restore src/Booket.Api/Booket.Api.csproj

COPY src/ src/
RUN dotnet publish src/Booket.Api/Booket.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# ---- Runtime ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

COPY --from=build /app/publish .

# Built-in non-root user provided by the .NET images.
USER $APP_UID

ENTRYPOINT ["dotnet", "Booket.Api.dll"]
