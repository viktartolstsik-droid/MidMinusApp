# Build context is the repository root, so shared projects under src/ can be copied in later.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ingestor/Ingestor.csproj src/Ingestor/
RUN dotnet restore src/Ingestor/Ingestor.csproj
COPY src/Ingestor/ src/Ingestor/
RUN dotnet publish src/Ingestor/Ingestor.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ingestor.dll"]
