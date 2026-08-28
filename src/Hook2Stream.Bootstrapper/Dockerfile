ARG DOTNET_SDK_VERSION=10.0.302
ARG DOTNET_SDK_DIGEST=sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}@${DOTNET_SDK_DIGEST} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/Hook2Stream.Bootstrapper/Hook2Stream.Bootstrapper.csproj
RUN dotnet publish src/Hook2Stream.Bootstrapper/Hook2Stream.Bootstrapper.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:1dcd9841b075d1d1013caa170b86ae58b8a8a563de9a3e319fd46a45e7ecc130 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "Hook2Stream.Bootstrapper.dll"]
