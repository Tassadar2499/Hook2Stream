ARG DOTNET_SDK_VERSION=10.0.302
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Hook2Stream.Api/Hook2Stream.Api.csproj
RUN dotnet publish src/Hook2Stream.Api/Hook2Stream.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false \
    /p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Hook2Stream.Api.dll"]
