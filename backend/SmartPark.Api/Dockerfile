FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src
COPY global.json ./
COPY backend/SmartPark.Api/SmartPark.Api.csproj backend/SmartPark.Api/packages.lock.json backend/SmartPark.Api/
RUN dotnet restore backend/SmartPark.Api/SmartPark.Api.csproj --locked-mode
COPY backend/SmartPark.Api/ backend/SmartPark.Api/
RUN dotnet publish backend/SmartPark.Api/SmartPark.Api.csproj --no-restore -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
RUN mkdir -p /uploads /keys && chown 1654:1654 /uploads /keys
COPY --from=build /out ./
COPY samples/ ./samples/
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartPark.Api.dll"]
