# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first
COPY *.sln .
COPY MyGeotabAPIAdapter/*.csproj MyGeotabAPIAdapter/
COPY MyGeotabAPIAdapter.Configuration/*.csproj MyGeotabAPIAdapter.Configuration/
COPY MyGeotabAPIAdapter.Database/*.csproj MyGeotabAPIAdapter.Database/
COPY MyGeotabAPIAdapter.Database.EntityPersisters/*.csproj MyGeotabAPIAdapter.Database.EntityPersisters/
COPY MyGeotabAPIAdapter.DataOptimizer/*.csproj MyGeotabAPIAdapter.DataOptimizer/
COPY MyGeotabAPIAdapter.Exceptions/*.csproj MyGeotabAPIAdapter.Exceptions/
COPY MyGeotabAPIAdapter.GeotabObjectMappers/*.csproj MyGeotabAPIAdapter.GeotabObjectMappers/
COPY MyGeotabAPIAdapter.Geospatial/*.csproj MyGeotabAPIAdapter.Geospatial/
COPY MyGeotabAPIAdapter.Helpers/*.csproj MyGeotabAPIAdapter.Helpers/
COPY MyGeotabAPIAdapter.Logging/*.csproj MyGeotabAPIAdapter.Logging/
COPY MyGeotabAPIAdapter.MyGeotabAPI/*.csproj MyGeotabAPIAdapter.MyGeotabAPI/
COPY MyGeotabAPIAdapter.Tests/*.csproj MyGeotabAPIAdapter.Tests/

# Install and restore packages
RUN dotnet add MyGeotabAPIAdapter/MyGeotabAPIAdapter.csproj package Azure.Storage.Files.DataLake --version 12.17.1 && \
    dotnet add MyGeotabAPIAdapter/MyGeotabAPIAdapter.csproj package Microsoft.Extensions.Configuration.Binder --version 8.0.0 && \
    dotnet restore

# Copy the rest of the source code
COPY . .

# Build and publish
RUN dotnet build MyGeotabAPIAdapter/MyGeotabAPIAdapter.csproj -c Release && \
    dotnet publish MyGeotabAPIAdapter/MyGeotabAPIAdapter.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Set environment variables
ENV DOTNET_ENVIRONMENT=Production

# Create a non-root user
RUN useradd -M -s /bin/bash appuser && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "MyGeotabAPIAdapter.dll"]
